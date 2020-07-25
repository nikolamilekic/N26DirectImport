module N26DirectImport.YnabApi

open FSharp.Data
open Milekic.YoLo

[<Literal>]
let TransactionsResponseSamplePath = __SOURCE_DIRECTORY__ + "/Samples/YnabData.json"
type TransactionsResponse = JsonProvider<TransactionsResponseSamplePath>

type KnowledgeTag = NoKnowledge | Knowledge of int

let makeHeaders key = [
    "Authorization", sprintf "Bearer %s" key
    "Accept", "application/json"
]

let getTransactions headers budgetId since =
    let endpoint =
        sprintf
            "https://api.youneedabudget.com/v1/budgets/%O/transactions"
            budgetId
    let query =
        match since with
        | NoKnowledge -> []
        | Knowledge tag -> [ "last_knowledge_of_server", tag.ToString() ]
    let responseString = Http.RequestString (endpoint, query, headers )
    let parsed = TransactionsResponse.Parse responseString
    let serverKnowledge = Knowledge parsed.Data.ServerKnowledge
    serverKnowledge, parsed.Data.Transactions

let updateYnabTransactions ynabHeaders budgetId transactions =
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
            httpMethod = "PATCH",
            headers = ynabHeaders)
    |> fun response ->
        match response.StatusCode, response.Body with
        | 209, Text x ->
             Ok ((TransactionsResponse.Parse x).Data.Transactions)
        | code, Text s -> Error (sprintf "StatusCode: %i, Text: %s" code s)
        | _ -> Error "Unexpected binary response from Ynab"

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
