namespace N26DirectImport.Core

open System
open FSharp.Data

type N26Transactions = JsonProvider<"Samples/N26Transactions.json", RootName="Transaction">
type N26Token = JsonProvider<"""{"access_token":"2db29b4e-ed3a-4a6d-9628-ae0fa8269015","token_type":"bearer","refresh_token":"90e4ba6e-8da2-4580-8411-fb0ac736c927","expires_in":1799,"scope":"read write trust"}""">
type N26AccountInfo = JsonProvider<"""{"id":"343dde6b-de8d-4651-bb1c-59b9c9d1f9a4","availableBalance":14.47,"usableBalance":14.47,"bankBalance":55.5,"iban":"DE89100110012628266077","bic":"NTSBDEB1XXX","bankName":"N26 Bank","seized":false,"currency":"EUR","legalEntity":"EU","externalId":{"iban":"DE89100110012628266077"}}""">

module N26 =
    let makeHeaders username password token =
        Http.RequestString (
            "https://api.tech26.de/oauth/token",
            body = (
                [
                    "username", username
                    "password", password
                    "grant_type", "password"
                ] :> seq<_>
                |> HttpRequestBody.FormValues),
            headers = (Seq.singleton ("Authorization", sprintf "Basic %s" token)),
            httpMethod = "POST"
        )
        |> N26Token.Parse
        |> fun t -> t.AccessToken
        |> sprintf "Bearer %A"
        |> fun x -> Seq.singleton ("Authorization", x)

    let getTransactions headers (from : DateTimeOffset) (``to`` : DateTimeOffset) =
        let rec inner previous lastId =
            let current =
                Http.RequestString (
                    "https://api.tech26.de/api/smrt/transactions",
                    headers = headers,
                    query = [
                        yield "from", from.ToUnixTimeMilliseconds().ToString()
                        yield "to", ``to``.ToUnixTimeMilliseconds().ToString()
                        yield "limit", "200"

                        match lastId with
                        | None -> ()
                        | Some x ->
                            yield "lastId", x
                    ]
                )
                |> N26Transactions.Parse

            if Array.length current = 200 then
                let lastId = (Array.last current).Id.ToString()
                inner (current::previous) (Some lastId)
            else current::previous

        inner [] None |> Seq.collect id

    let getAccountInfo headers =
        Http.RequestString(
            "https://api.tech26.de/api/accounts",
            headers = headers
        )
        |> N26AccountInfo.Parse
