module N26DirectImport.YnabApi

open FSharp.Data
open Milekic.YoLo

[<Literal>]
let TransactionsResponseSamplePath = __SOURCE_DIRECTORY__ + "/Samples/YnabData.json"
type TransactionsResponse = JsonProvider<TransactionsResponseSamplePath>

let makeHeaders key = [
    "Authorization", sprintf "Bearer %s" key
    "Accept", "application/json"
]

let addYnabTransactions ynabHeaders budgetId transactions =
    transactions
    |> Seq.map (fun (x : TransactionsResponse.Transaction) -> x.JsonValue)
    |> Array.ofSeq
    |> JsonValue.Array
    |> fun a -> ("transactions", a) |> Array.singleton |> JsonValue.Record
    |> fun body ->
        body.Request(
            sprintf
                "https://api.youneedabudget.com/v1/budgets/%O/transactions"
                budgetId,
            httpMethod = "POST",
            headers = ynabHeaders)
    |> fun response ->
        match response.StatusCode, response.Body with
        | 201, Text x ->
             Ok ((TransactionsResponse.Parse x).Data.Transactions)
        | _, Text s -> Error s
        | _ -> Error "Unexpected binary response from Ynab"
