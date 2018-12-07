namespace N26DirectImport.Core

open System
open FSharp.Data

type YnabData = JsonProvider<"Samples/YnabData.json">

module Ynab =
    let makeHeaders key = [
        "Authorization", sprintf "Bearer %s" key
        "Accept", "application/json"
    ]

    let getTransactionsEndpoint =
        sprintf
            "https://api.youneedabudget.com/v1/budgets/%s/accounts/%s/transactions"
            (Budgets.main.ToString())
            (Accounts.n26.ToString())

    let putAndPostTransactionsEndpoint =
            sprintf
                "https://api.youneedabudget.com/v1/budgets/%s/transactions"
                (Budgets.main.ToString())

    let private parseCleared = function | "reconciled" -> Reconciled
                                        | "cleared" -> Cleared
                                        | _ -> Uncleared

    let private makeTransactionModel (yt : YnabData.Transaction) =
        {
            Id = yt.Id.String.Value
            PayeeId = None
            Date = yt.Date.Value
            PayeeName = yt.PayeeName
            CategoryId = yt.CategoryId
            Memo = yt.Memo
            Amount = yt.Amount |> Option.map (fun a -> (decimal a) / 1000.0m)
            Cleared = parseCleared yt.Cleared
        }

    let getTransactionsString headers (since : DateTime option) =
        Http.RequestString (
            getTransactionsEndpoint,
            query =
                (match since with
                | None -> []
                | Some d -> [ "since_date", d.ToString("yyyy-MM-dd") ]),
            headers = headers
        )

    let getTransactions headers since =
        getTransactionsString headers since
        |> YnabData.Parse
        |> fun x -> x.Data.Transactions
        |> Seq.where(fun t -> t.AccountId = Some Accounts.n26)
        |> Seq.sortByDescending (fun yt -> yt.Date)
        |> Seq.map makeTransactionModel
        |> List.ofSeq

    let private fieldMappings =
        let mapString x = x |> Option.map (fun x -> x.ToString())
        [
            "date", fun t -> t.Date.ToString("yyyy-MM-dd") |> Some
            "payee_id", fun t -> t.PayeeId |> mapString
            "payee_name", fun t -> t.PayeeName
            "category_id", fun t -> t.CategoryId |> mapString
            "memo", fun t -> t.Memo
            "cleared", fun t -> match t.Cleared with
                                | Cleared -> Some "cleared"
                                | Reconciled -> Some "reconciled"
                                | Uncleared -> Some "uncleared"
            "amount",
                fun t -> t.Amount
                >> Option.map (fun a ->
                    let x = (a * 1000m) |> Math.Round |> int in x.ToString())
            "account_id", fun _ -> Some (Accounts.n26.ToString())
        ]

    let private fieldsToTransaction fields =
        if fields |> List.isEmpty then None else

        fields
        |> Seq.map (fun (k, v) -> k, JsonValue.String v)
        |> Array.ofSeq
        |> JsonValue.Record
        |> YnabData.Transaction
        |> Some

    let getUpdateTransaction original updated =
        fieldMappings
        |> Seq.map (fun (k, vf) -> k, vf original, vf updated)
        |> Seq.where (fun (k, original, updated) ->
            k <> "import_id" && original <> updated)
        |> Seq.map (fun (k, _, v) ->
            let vs = match v with | Some x -> x | None -> "" in k, vs)
        |> Seq.toList
        |> fieldsToTransaction

    let getCreateTransaction t =
        fieldMappings
        |> Seq.map (fun (k, vf) -> vf t |> Option.map (fun v -> k, v))
        |> Seq.onlySome
        |> Seq.toList
        |> fieldsToTransaction
        |> Option.get
