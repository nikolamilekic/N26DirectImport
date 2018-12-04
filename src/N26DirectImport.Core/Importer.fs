module N26DirectImport.Core.Importer

open System
open FSharp.Data
open N26DirectImport.Core
open Microsoft.WindowsAzure.Storage.Table

type Binding() =
    inherit TableEntity()
    member val Ynab = "" with get, set

let private getChangeSet
    ynabHeaders
    n26Headers
    (bindingsTable : CloudTable)
    (``to`` : DateTimeOffset) =

    let twoMonthsPrior = (``to`` - TimeSpan.FromDays 60.0)
    let ynabTransactions =
        Ynab.getTransactions ynabHeaders (Some twoMonthsPrior.DateTime)

    let from =
        ynabTransactions
        |> Seq.where (fun yt -> yt.Cleared <> Reconciled)
        |> Seq.tryLast
        |> Option.defaultWith (fun () -> ynabTransactions |> Seq.head)
        |> (fun yt -> (yt.Date - TimeSpan.FromDays 1.0) |> DateTimeOffset)

    let ynabTransactionsToConsider =
        ynabTransactions
        |> Seq.where (fun yt -> (DateTimeOffset yt.Date) >= from)
        |> Seq.map (fun yt -> yt.Id, yt)
        |> Map.ofSeq

    let n26ToYnab =
        let rec retrieveBindings token results =
            TableQuery<Binding>()
                .Where(
                    TableQuery.GenerateFilterCondition(
                        "PartitionKey",
                        QueryComparisons.GreaterThanOrEqual,
                        from.ToUnixTimeMilliseconds().ToString()))
            |> fun q -> bindingsTable.ExecuteQuerySegmentedAsync(q, token).Result
            |> fun segment ->
                let newResults = segment.Results::results
                if isNull segment.ContinuationToken then newResults else
                retrieveBindings segment.ContinuationToken newResults

        retrieveBindings null []
        |> Seq.collect id
        |> Seq.map (fun b -> b.RowKey, b.Ynab)
        |> Map.ofSeq

    let ynabToN26 =
        n26ToYnab
        |> Map.toSeq
        |> Seq.map (fun (ntk, ytk) -> ytk, ntk)
        |> Map.ofSeq

    let n26TransactionsToConsider =
        N26.getTransactions n26Headers from ``to``
        |> Seq.map (fun nt -> nt.VisibleTs.ToString(), nt)
        |> Map.ofSeq

    let ynabOrphans =
        ynabTransactionsToConsider
        |> Map.toSeq
        |> Seq.map snd
        |> Seq.where (fun yt ->
           match Map.tryFind yt.Id ynabToN26 with
           | None -> true
           | Some ntk -> Map.containsKey ntk n26TransactionsToConsider |> not)
        |> List.ofSeq

    let (transactionsToAdd, transactionsToUpdate) =
        n26TransactionsToConsider
        |> Map.toSeq
        |> Seq.map snd
        |> Seq.fold
            (fun (toAdd, toUpdate) nt ->
                let yto =
                    Map.tryFind (nt.VisibleTs.ToString()) n26ToYnab
                    |> Option.bind
                        (fun key -> Map.tryFind key ynabTransactionsToConsider)
                match yto with
                | Some yt when yt.Cleared <> Reconciled ->
                    toAdd, (nt, yt, false)::toUpdate
                | Some _ -> toAdd, toUpdate
                | None ->
                    let orphan =
                        ynabOrphans
                        |> List.tryFind (fun yt ->
                            yt.Cleared <> Reconciled &&
                            yt.Amount = Some nt.Amount &&

                            toUpdate
                            |> List.exists (fun (_, y, _) -> yt = y)
                            |> not)
                    match orphan with
                    | None -> nt::toAdd, toUpdate
                    | Some orphan -> toAdd, (nt, orphan, true)::toUpdate)
            ([], [])

    let ynabTransactionsToUpdate =
        transactionsToUpdate
        |> Seq.map (fun (_, yt, _) -> yt)
        |> Set.ofSeq

    let transactionsToDelete =
        ynabOrphans
        |> List.where (fun o ->
            o.ImportId.IsSome &&
            o.Cleared <> Reconciled &&
            Set.contains o ynabTransactionsToUpdate |> not)

    transactionsToAdd
    |> List.map (fun nt ->
        let date = Rules.getDateFromN26Transaction nt
        let initial = TransactionModel.makeEmpty date
        nt, Rules.applyAddRules initial nt),

    transactionsToUpdate
    |> List.map (fun (nt, original, rebind) ->
        nt, original, Rules.applyUpdateRules original nt, rebind),

    transactionsToDelete

let private add ynabHeaders toAdd =
    if Seq.isEmpty toAdd then [||] else

    toAdd
    |> Seq.map (snd >> Ynab.getCreateTransaction)
    |> Array.ofSeq
    |> YnabData.Data
    |> fun d ->
        d.JsonValue.Request(
            Ynab.putAndPostTransactionsEndpoint,
            httpMethod = "POST",
            headers = ynabHeaders
        )
    |> fun r ->
        match r.Body with
        | Binary _ -> failwith "Unexpected YNAB response"
        | Text s -> (YnabData.Parse s).Data.Transactions

let private update ynabHeaders toUpdate =
    let updates =
        toUpdate
        |> Seq.map (fun (_, original, updated, _) ->
            Ynab.getUpdateTransaction original updated
            |> Option.map (fun t -> original, updated, t))
        |> Seq.onlySome
        |> Seq.toList
    updates
    |> Seq.map (fun (original, _, t) ->
        original.Id,
        ("transaction", t.JsonValue)
        |> Array.singleton
        |> JsonValue.Record)
    |> Seq.iter (fun (id, body) ->
        body.Request(
            sprintf "%s/%s" Ynab.putAndPostTransactionsEndpoint id,
            httpMethod = "PUT",
            headers = ynabHeaders
        )
        |> ignore)

let private delete ynabHeaders toDelete =
    toDelete
    |> Seq.map (fun id ->
        id.ToString(),
        (("flag_color", JsonValue.String "red")
        |> Array.singleton
        |> JsonValue.Record
        |> fun yt -> ("transaction", yt)
        |> Array.singleton
        |> JsonValue.Record))
    |> List.ofSeq
    |> Seq.iter (fun (id, body) ->
        body.Request(
            sprintf "%s/%s" Ynab.putAndPostTransactionsEndpoint id,
            httpMethod = "PUT",
            headers = ynabHeaders
        )
        |> ignore)

type Facade(ynabKey, n26Username, n26Password, n26Token) =
    let ynabHeaders = Ynab.makeHeaders ynabKey
    member __.RunAsync bindings = async {
        let now = DateTimeOffset.Now
        let n26Headers = N26.makeHeaders n26Username n26Password n26Token
        let toAdd, toUpdate, toDelete =
            getChangeSet ynabHeaders n26Headers bindings now

        let newYnabTransactions = add ynabHeaders toAdd
        update ynabHeaders toUpdate
        delete ynabHeaders toDelete

        let accountInfo = N26.getAccountInfo n26Headers

        do!
            seq {
                yield!
                    Seq.zip toAdd newYnabTransactions
                    |> Seq.map (fun ((nt, _), y) ->
                        nt.VisibleTs.ToString(), y.Id.String.Value)

                yield!
                    toUpdate
                    |> Seq.where (fun (_, _, _, rebind) -> rebind)
                    |> Seq.map (fun (nt, yt, _, _) ->
                        nt.VisibleTs.ToString(), yt.Id)
            }
            |> Seq.map (fun (ntk, ytk) ->
                Binding(RowKey = ntk, Ynab = ytk, PartitionKey = "Binding")
                |> TableOperation.InsertOrReplace)
            |> Seq.toArray
            |> fun oss -> async {
                if oss |> Array.isEmpty then return () else
                let batch = TableBatchOperation()
                for os in oss do batch.Add(os)
                return!
                    bindings.ExecuteBatchAsync(batch)
                    |> Async.AwaitTask
                    |> Async.Ignore
            }

        return accountInfo.BankBalance
    }

    member this.Run bindings =
        this.RunAsync bindings |> Async.StartImmediateAsTask

    member __.GetAllTransactions() = Ynab.getTransactionsString ynabHeaders None
