module N26DirectImport.Importer

open System
open FSharp.Data
open Microsoft.WindowsAzure.Storage.Table

type Binding() =
    inherit TableEntity()
    member val Ynab = "" with get, set
    member val VisibleTs = 0L with get, set

let private getChangeSet
    ynabHeaders
    n26Headers
    (bindingsTable : CloudTable)
    (``to`` : DateTimeOffset) = async {

    let twoMonthsPrior = (``to`` - TimeSpan.FromDays 60.0)
    let! ynabTransactions =
        Ynab.getTransactions ynabHeaders (Some twoMonthsPrior.DateTime)

    let from =
        ynabTransactions
        |> Seq.where (fun yt -> yt.Cleared <> Reconciled)
        |> Seq.tryLast
        |> Option.defaultWith (fun () -> ynabTransactions |> Seq.head)
        |> (fun yt -> (yt.Date - TimeSpan.FromDays 1.0) |> DateTimeOffset)

    let! mappingsRetriever =
        let rec retrieveBindings results token = async {
            let query =
                TableQuery<Binding>()
                    .Where(
                        TableQuery.GenerateFilterConditionForLong(
                            "VisibleTs",
                            QueryComparisons.GreaterThanOrEqual,
                            from.ToUnixTimeMilliseconds()))
            let! segment =
                bindingsTable.ExecuteQuerySegmentedAsync(query, token)
                |> Async.AwaitTask

            let results = segment.Results::results
            let token = segment.ContinuationToken

            if isNull token |> not
            then return! retrieveBindings results token
            else
                return
                    results
                    |> Seq.collect id
                    |> Seq.map (fun b -> Guid(b.RowKey), b.Ynab)
                    |> Map.ofSeq
        }

        async {
            let! n26ToYnab = retrieveBindings [] null
            let ynabToN26 =
                n26ToYnab
                |> Map.toSeq
                |> Seq.map (fun (ntk, ytk) -> ytk, ntk)
                |> Map.ofSeq
            return (n26ToYnab, ynabToN26)
        }
        |> Async.StartChild

    let ynabTransactionsToConsider =
        ynabTransactions
        |> Seq.where (fun yt -> (DateTimeOffset yt.Date) >= from)
        |> Seq.map (fun yt -> yt.Id, yt)
        |> Map.ofSeq

    let! n26TransactionsToConsider = async {
        let! x = N26.getTransactions n26Headers from ``to``
        return
            x
            |> Seq.map (fun nt -> nt.Id, nt)
            |> Map.ofSeq
    }

    let! n26ToYnab, ynabToN26 = mappingsRetriever

    let ynabOrphans =
        ynabTransactionsToConsider
        |> Map.toSeq
        |> Seq.map snd
        |> Seq.where (fun yt ->
           match Map.tryFind yt.Id ynabToN26 with
           | None -> true
           | Some ntk -> Map.containsKey ntk n26TransactionsToConsider |> not)
        |> List.ofSeq

    let (transactionsToAdd, transactionsToUpdate, transactionsToBind) =
        n26TransactionsToConsider
        |> Map.toSeq
        |> Seq.map snd
        |> Seq.fold
            (fun (toAdd, toUpdate, toBind) nt ->
                let yto =
                    Map.tryFind nt.Id n26ToYnab
                    |> Option.bind
                        (fun key -> Map.tryFind key ynabTransactionsToConsider)
                match yto with
                | Some yt when yt.Cleared <> Reconciled ->
                    toAdd, (nt, yt)::toUpdate, toBind
                | Some _ -> toAdd, toUpdate, toBind
                | None ->
                    let orphan =
                        ynabOrphans
                        |> List.tryFind (fun yt ->
                            yt.Amount = Some nt.Amount &&
                            yt.Cleared <> Reconciled &&
                            toUpdate
                            |> List.exists (fun (_, y) -> yt = y)
                            |> not)
                    match orphan with
                    | None -> nt::toAdd, toUpdate, toBind
                    | Some orphan ->
                        toAdd, (nt, orphan)::toUpdate, (nt, orphan)::toBind)
            ([], [], [])

    let ynabTransactionsToUpdate =
        transactionsToUpdate
        |> Seq.map (fun (_, yt) -> yt)
        |> Set.ofSeq

    let transactionsToDelete =
        ynabOrphans
        |> List.where (fun o ->
            o.Cleared <> Reconciled &&
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

        transactionsToBind
}

let private add ynabHeaders toAdd = async {
    if Seq.isEmpty toAdd then return [||] else

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
    | Text s -> return (YnabData.Parse s).Data.Transactions
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

let run ynabKey n26Username n26Password n26Token bindings = async {
    let ynabHeaders = Ynab.makeHeaders ynabKey
    let now = DateTimeOffset.Now
    let! n26Headers = N26.makeHeaders n26Username n26Password n26Token
    let! toAdd, toUpdate, toDelete, toBind =
        getChangeSet ynabHeaders n26Headers bindings now

    let! updateDeleteJobs =
        [
            update ynabHeaders toUpdate
            delete ynabHeaders toDelete
        ]
        |> Async.Parallel
        |> Async.Ignore
        |> Async.StartChild

    let! accountInfoRetriever = N26.getAccountInfo n26Headers |> Async.StartChild

    let! newYnabTransactions = add ynabHeaders toAdd

    do!
        seq {
            yield!
                Seq.zip toAdd newYnabTransactions
                |> Seq.map (fun ((nt, _), y) ->
                    let partition =
                        y.Date
                        |> Option.map (fun d -> d.DayOfWeek)
                        |> Option.defaultValue DayOfWeek.Monday
                    nt.Id, nt.VisibleTs, y.Id.String.Value, partition)

            yield!
                toBind
                |> Seq.map (fun (nt, yt) ->
                    nt.Id, nt.VisibleTs, yt.Id, yt.Date.DayOfWeek)
        }
        |> Seq.groupBy (fun (_, _, _, p) -> p)
        |> Seq.map (fun (_, currentPartition) ->
            currentPartition
            |> Seq.map (fun (ntk, visibleTs, ytk, partition) ->
                Binding(
                    RowKey = ntk.ToString(),
                    Ynab = ytk,
                    VisibleTs = visibleTs,
                    PartitionKey = partition.ToString())
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
            })
        |> Async.Parallel
        |> Async.Ignore

    do! updateDeleteJobs
    let! accountInfo = accountInfoRetriever
    return accountInfo.BankBalance
}
