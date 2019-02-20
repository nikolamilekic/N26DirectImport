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
    ynabHeaders n26Headers n26ToYnab (lastReconciliation : DateTimeOffset) = async {
    let now = DateTimeOffset.Now

    let backSpan = TimeSpan.FromDays 30.0

    let! ynabTransactions =
        Ynab.getTransactions
            ynabHeaders
            (Some (lastReconciliation.DateTime - backSpan))

    let reconciliation =
        ynabTransactions
        |> Seq.where (fun yt -> yt.Cleared <> Reconciled)
        |> Seq.tryLast
        |> Option.defaultWith (fun () -> ynabTransactions |> Seq.head)
        |> fun yt -> yt.Date |> DateTimeOffset

    let from = reconciliation - TimeSpan.FromDays 30.0

    let ynabTransactionsToConsider =
        ynabTransactions
        |> Seq.where (fun yt -> (DateTimeOffset yt.Date) >= from)
        |> Seq.map (fun yt -> yt.Id, yt)
        |> Map.ofSeq

    let! n26TransactionsToConsider = async {
        let! x = N26.getTransactions n26Headers from now
        return
            x
            |> Seq.map (fun nt -> nt.Id, nt)
            |> Map.ofSeq
    }

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

    let fromInUnixMs = from.ToUnixTimeMilliseconds()
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
        |> Map.filter (fun _ (_, visibleTs) -> visibleTs >= fromInUnixMs),

        reconciliation
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

type Config = {
    YnabKey : string
    N26Username : string
    N26Password : string
    N26Token : string
}

let run
    config
    (bindings : CloudBlockBlob)
    (balance : CloudBlockBlob)
    (lastReconciliation : CloudBlockBlob) = async {

    let! leaser =
        TimeSpan.FromSeconds(20.0)
        |> Some
        |> Option.toNullable
        |> bindings.AcquireLeaseAsync
        |> Async.AwaitTask
        |> Async.StartChild

    let! n26Headers =
        N26.makeHeaders config.N26Username config.N26Password config.N26Token
    let ynabHeaders = Ynab.makeHeaders config.YnabKey

    let! balanceRetriever =
        async {
            let! accountInfo = N26.getAccountInfo n26Headers
            return accountInfo.BankBalance
        }
        |> Async.StartChild

    let! oldBindings = deserializeBlob bindings
    let! oldReconciliation = deserializeBlob lastReconciliation

    let! toAdd, toUpdate, toDelete, newBindings, newReconciliation =
        getChangeSet ynabHeaders n26Headers oldBindings oldReconciliation

    let! updateDeleteJobs =
        [
            update ynabHeaders toUpdate
            delete ynabHeaders toDelete
        ]
        |> Async.Parallel
        |> Async.Ignore
        |> Async.StartChild

    let! added = add ynabHeaders toAdd

    let newBindings =
        added
        |> Seq.fold
            (fun bindings (nt, yt) ->
                Map.add nt.Id (yt.Id, nt.VisibleTs) bindings)
            newBindings

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

    if oldReconciliation <> newReconciliation then
        let pickled = serializer.Pickle newReconciliation
        do!
            lastReconciliation.UploadFromByteArrayAsync (pickled, 0, pickled.Length)
            |> Async.AwaitTask

    do! updateDeleteJobs

    let! newBalance = balanceRetriever
    do!
        balance.UploadTextAsync (newBalance.ToString())
        |> Async.AwaitTask

    do!
        bindings.ReleaseLeaseAsync(
           AccessCondition.GenerateLeaseCondition(lease))
        |> Async.AwaitTask

    let bindingsCount = Map.count newBindings
    return
        sprintf
            "Cleared balance: %M\nBindings count: %i"
            newBalance
            bindingsCount
}
