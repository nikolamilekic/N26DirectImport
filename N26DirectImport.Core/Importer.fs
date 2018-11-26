module N26DirectImport.Core.Importer

open System
open FSharp.Data
open N26DirectImport.Core

let private getChangeSet ynabHeaders n26Headers (``to`` : DateTimeOffset) =
    let twoMonthsPrior = (``to`` - TimeSpan.FromDays 60.0)
    let ynabTransactions =
        Ynab.getTransactions ynabHeaders (twoMonthsPrior.DateTime)

    let from =
        ynabTransactions
        |> Seq.map snd
        |> Seq.where (fun yt -> yt.Cleared <> Reconciled)
        |> Seq.tryLast
        |> Option.defaultWith (fun () ->
            ynabTransactions
            |> Seq.map snd
            |> Seq.head)
        |> (fun yt -> (yt.Date - TimeSpan.FromDays 1.0) |> DateTimeOffset)

    let ynabTransactionsToConsider =
        ynabTransactions
        |> List.where (fun (_, yt) -> (DateTimeOffset yt.Date) >= from)

    let n26Transactions = N26.getTransactions n26Headers from ``to``

    let n26Dates =
        n26Transactions
        |> Seq.map (fun nt -> nt.VisibleTs)
        |> Set

    let ynabStale =
        ynabTransactionsToConsider
        |> Seq.where (fun (_, yt) -> yt.Cleared <> Reconciled)
        |> Seq.map (fun (id, yt) -> id, yt, Rules.tryGetDateFromImportId yt)
        |> Seq.where (fun (_, _, d) ->
            match d with
            | Some x when Set.contains x n26Dates |> not -> true
            | _ -> false)
        |> Seq.map (fun (id, yt, _) -> id, yt)
        |> List.ofSeq

    let n26New =
        n26Transactions
        |> Seq.where (
            Rules.findMatchingYnabTransaction ynabTransactionsToConsider
            >> Option.isNone)
        |> List.ofSeq

    let (transactionsToDelete, transactionsToUpdate) =
        ynabStale
        |> List.fold
            (fun (toDelete, toUpdate) (id, yt) ->
                let ytAmount = yt.Amount |> Option.defaultValue 0m
                let matchingN26 =
                    n26New
                    |> List.tryFind (fun nt -> nt.Amount = ytAmount)

                match matchingN26 with
                | None -> id::toDelete, toUpdate
                | Some matching ->
                    let updated = Rules.apply yt matching
                    toDelete, (id, yt, updated, matching)::toUpdate)
            ([], [])

    let transactionsToAdd =
        n26New
        |> Seq.where (fun nt ->
            transactionsToUpdate
            |> Seq.map (fun (_, _, _, nt) -> nt)
            |> Seq.contains nt |> not)
        |> Seq.map (fun nt ->
            let date = Rules.getDateFromN26Transaction nt
            let initial = TransactionModel.makeEmpty date
            Rules.apply initial nt)
        |> List.ofSeq

    transactionsToAdd,
    (transactionsToUpdate
    |> Seq.map (fun (id, original, updated, _) -> id, original, updated)),
    transactionsToDelete

let private add ynabHeaders toAdd =
    toAdd
    |> Seq.map Ynab.getCreateTransaction
    |> Array.ofSeq
    |> YnabData.Data
    |> fun d ->
        d.JsonValue.Request(
            Ynab.transactionsEndpoint,
            httpMethod = "POST",
            headers = ynabHeaders
        )
    |> ignore

let private update ynabHeaders toUpdate =
    toUpdate
    |> Seq.map (fun (id, original, updated) ->
        Ynab.getUpdateTransaction original updated
        |> Option.map (fun t -> id.ToString(), t))
    |> Seq.onlySome
    |> Seq.map (fun (id, t) ->
        id,
        ("transaction", t.JsonValue)
        |> Array.singleton
        |> JsonValue.Record)
    |> Seq.iter (fun (id, body) ->
        body.Request(
            sprintf "%s/%s" Ynab.transactionsEndpoint id,
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
            sprintf "%s/%s" Ynab.transactionsEndpoint id,
            httpMethod = "PUT",
            headers = ynabHeaders
        )
        |> ignore)

let run ynabHeaders n26Headers ``to`` =
    let toAdd, toUpdate, toDelete = getChangeSet ynabHeaders n26Headers ``to``
    add ynabHeaders toAdd
    update ynabHeaders toUpdate
    delete ynabHeaders toDelete
