module N26DirectImport.Importer

open System
open FSharp.Data
open Milekic.YoLo
open MBrace.FsPickler.Json
open Microsoft.WindowsAzure.Storage.Blob
open Microsoft.WindowsAzure.Storage

let serializer = FsPickler.CreateJsonSerializer()
let deserializeBlob (blob : CloudBlockBlob) = async {
    let! stream = blob.OpenReadAsync() |> Async.AwaitTask
    let! bytes = stream.AsyncRead <| int stream.Length
    return serializer.UnPickle bytes
}

let private getChangeSet
    ynabHeaders n26Headers n26ToYnab (from : DateTimeOffset) ``to`` = async {

    let! ynabRetriever =
        async {
            let! ts = Ynab.getTransactions ynabHeaders (Some from.Date)
            return
                ts
                |> Seq.map (fun yt -> yt.Id, yt)
                |> Map.ofSeq
        }
        |> Async.StartChild

    let! n26Retriever =
        async {
            let! x = N26.getTransactions n26Headers from ``to``
            return
                x
                |> Seq.map (fun nt -> nt.Id, nt)
                |> Map.ofSeq
        }
        |> Async.StartChild

    let ynabToN26 =
        n26ToYnab
        |> Map.toSeq
        |> Seq.groupBy (fun (_, (y, _)) -> y)
        |> Seq.map (fun (_, ts) ->
            ts
            |> Seq.sortByDescending (snd >> snd)
            |> Seq.head
            |> fun (ntk, (ytk, _)) -> ytk, ntk)
        |> Map.ofSeq

    let! ynabTransactionsToConsider = ynabRetriever
    let! n26TransactionsToConsider = n26Retriever

    let ynabOrphans =
        ynabTransactionsToConsider
        |> Map.toSeq
        |> Seq.map snd
        |> Seq.where (fun yt ->
           yt.Cleared <> Reconciled &&
           match Map.tryFind yt.Id ynabToN26 with
           | None -> true
           | Some ntk -> Map.containsKey ntk n26TransactionsToConsider |> not)
        |> List.ofSeq

    let (transactionsToAdd, transactionsToUpdate, newBindings) =
        n26TransactionsToConsider
        |> Map.toSeq
        |> Seq.map snd
        |> Seq.fold
            (fun (toAdd, toUpdate, bindings) nt ->
                let yto =
                    Map.tryFind nt.Id n26ToYnab
                    |> Option.bind (fun (key, _) ->
                        Map.tryFind key ynabTransactionsToConsider)
                match yto with
                | Some yt when yt.Cleared <> Reconciled ->
                    toAdd, (nt, yt)::toUpdate, bindings
                | Some _ -> toAdd, toUpdate, bindings
                | None ->
                    let orphan =
                        ynabOrphans
                        |> List.tryFind (fun yt ->
                            (
                                yt.Amount = Some nt.Amount ||

                                nt.OriginalCurrency <> Some "EUR" &&
                                yt.Amount.IsSome &&
                                Math.Abs(1m - nt.Amount / yt.Amount.Value) < 0.02m
                            ) &&

                            toUpdate
                            |> List.exists (fun (_, y) -> yt = y)
                            |> not)
                    match orphan with
                    | None -> nt::toAdd, toUpdate, bindings
                    | Some orphan ->
                        toAdd,
                        (nt, orphan)::toUpdate,
                        Map.add nt.Id (orphan.Id, nt.VisibleTs) bindings)
            ([], [], n26ToYnab)

    let ynabTransactionsToUpdate =
        transactionsToUpdate
        |> Seq.map (fun (_, yt) -> yt)
        |> Set.ofSeq

    let transactionsToDelete =
        ynabOrphans
        |> List.where (fun o ->
            Set.contains o ynabTransactionsToUpdate |> not &&
            Map.containsKey o.Id ynabToN26)

    return
        transactionsToAdd
        |> List.map (fun nt ->
            let date = Rules.getDateFromN26Transaction nt
            let initial = TransactionModel.makeEmpty date
            nt, Rules.applyAddRules initial nt),

        transactionsToUpdate
        |> List.map (fun (nt, original) ->
            nt, original, Rules.applyUpdateRules original nt),

        transactionsToDelete,
        newBindings
}

let private add ynabHeaders toAdd = async {
    if Seq.isEmpty toAdd then return List.empty else

    let sortKey t = t.Amount, t.Memo
    let toAddSorted = Seq.sortBy (snd >> sortKey) toAdd

    let! result =
        toAdd
        |> Seq.map (snd >> Ynab.getCreateTransaction)
        |> Array.ofSeq
        |> YnabData.Data
        |> fun d ->
            d.JsonValue.RequestAsync(
                Ynab.putAndPostTransactionsEndpoint,
                httpMethod = "POST",
                headers = ynabHeaders
            )

    match result.Body with
    | Binary _ -> return failwith "Unexpected YNAB response"
    | Text s ->
        let added =
            (YnabData.Parse s).Data.Transactions
            |> Seq.map Ynab.makeTransactionModel
            |> Seq.sortBy sortKey
        return
            Seq.zip toAddSorted added
            |> Seq.map (fun ((nt, _), yt) -> nt, yt)
            |> List.ofSeq
}

let private update ynabHeaders toUpdate =
    toUpdate
    |> Seq.map (fun (_, original, updated) ->
        Ynab.getUpdateTransaction original updated
        |> Option.map (fun t -> original, updated, t))
    |> Seq.onlySome
    |> Seq.map (fun (original, _, t) ->
        let body =
            ("transaction", t.JsonValue)
            |> Array.singleton
            |> JsonValue.Record
        body.RequestAsync(
            sprintf "%s/%s" Ynab.putAndPostTransactionsEndpoint original.Id,
            httpMethod = "PUT",
            headers = ynabHeaders))
    |> Async.Parallel
    |> Async.Ignore

let private delete ynabHeaders (toDelete : TransactionModel list) =
    toDelete
    |> Seq.map (fun t ->
        let id = t.Id.ToString()
        let body =
            (("flag_color", JsonValue.String "red")
            |> Array.singleton
            |> JsonValue.Record
            |> fun yt -> ("transaction", yt)
            |> Array.singleton
            |> JsonValue.Record)
        body.RequestAsync(
            sprintf "%s/%s" Ynab.putAndPostTransactionsEndpoint id,
            httpMethod = "PUT",
            headers = ynabHeaders
        ))
    |> Async.Parallel
    |> Async.Ignore

let run n26Headers ynabHeaders (bindings : CloudBlockBlob) = async {
    let! leaser =
        TimeSpan.FromSeconds(20.0)
        |> Some
        |> Option.toNullable
        |> bindings.AcquireLeaseAsync
        |> Async.AwaitTask
        |> Async.StartChild

    let! oldBindings = deserializeBlob bindings

    let ``to`` = DateTimeOffset.Now
    let from =
        if Map.isEmpty oldBindings then DateTimeOffset(``to``.Date) else

        let x = ``to``.AddMonths(-2)
        let startOfThreeMonthWindow =
            DateTimeOffset(x.Year, x.Month, 1, 0, 0, 0, x.Offset)
        let oldestBinding =
            oldBindings
            |> Map.toSeq
            |> Seq.map (snd >> snd)
            |> Seq.min
            |> DateTimeOffset.FromUnixTimeMilliseconds
            |> fun d -> DateTimeOffset(d.Date)
        if oldestBinding > startOfThreeMonthWindow
        then oldestBinding else startOfThreeMonthWindow

    let! toAdd, toUpdate, toDelete, newBindings =
        getChangeSet ynabHeaders n26Headers oldBindings from ``to``

    let! updateDeleteJobs =
        [
            update ynabHeaders toUpdate
            delete ynabHeaders toDelete
        ]
        |> Async.Parallel
        |> Async.Ignore
        |> Async.StartChild

    let! added = add ynabHeaders toAdd

    let fromInUnixMs = from.ToUnixTimeMilliseconds()
    let newBindings =
        added
        |> Seq.fold
            (fun bindings (nt, yt) ->
                Map.add nt.Id (yt.Id, nt.VisibleTs) bindings)
            newBindings
        |> Map.filter (fun _ (_, visibleTs) -> visibleTs >= fromInUnixMs)

    let! lease = leaser

    if oldBindings <> newBindings then
        let pickled = serializer.Pickle newBindings
        do!
            bindings.UploadFromByteArrayAsync(
                pickled,
                0,
                pickled.Length,
                AccessCondition.GenerateLeaseCondition(lease),
                null,
                null)
            |> Async.AwaitTask

    do! updateDeleteJobs

    do!
        bindings.ReleaseLeaseAsync(
           AccessCondition.GenerateLeaseCondition(lease))
        |> Async.AwaitTask

    return Map.count newBindings
}
