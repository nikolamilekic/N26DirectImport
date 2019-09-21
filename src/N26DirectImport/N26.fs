namespace N26DirectImport

open System
open FSharp.Data

type N26Transactions = JsonProvider<"Samples/N26Transactions.json", RootName="Transaction">
type N26Token = JsonProvider<"""{"access_token":"2db29b4e-ed3a-4a6d-9628-ae0fa8269015","token_type":"bearer","refresh_token":"90e4ba6e-8da2-4580-8411-fb0ac736c927","expires_in":1799,"scope":"read write trust"}""">
type N26AccountInfo = JsonProvider<"""{"id":"343dde6b-de8d-4651-bb1c-59b9c9d1f9a4","availableBalance":14.47,"usableBalance":14.47,"bankBalance":55.5,"iban":"DE89100110012628266077","bic":"NTSBDEB1XXX","bankName":"N26 Bank","seized":false,"currency":"EUR","legalEntity":"EU","externalId":{"iban":"DE89100110012628266077"}}""">

module N26 =
    let makeHeaders =
        sprintf "Bearer %s"
        >> fun x -> Seq.singleton ("Authorization", x)

    let refreshToken basicToken refreshToken =
        Http.RequestString (
            "https://api.tech26.de/oauth/token",
            body = (
                [
                    "grant_type", "refresh_token"
                    "refresh_token", refreshToken
                ] :> seq<_>
                |> HttpRequestBody.FormValues),
            headers = (Seq.singleton ("Authorization", sprintf "Basic %s" basicToken)),
            httpMethod = "POST"
        )
        |> N26Token.Parse

    let getTransactions headers (from : DateTimeOffset) (``to`` : DateTimeOffset) =
        let rec inner previous lastId = async {
            let! current = async {
                let! x =
                    Http.AsyncRequestString (
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
                return N26Transactions.Parse x
            }

            if Array.length current = 200 then
                let lastId = (Array.last current).Id.ToString()
                return! inner (current::previous) (Some lastId)
            else return current::previous
        }

        async {
            let! x = inner [] None
            return Seq.collect id x
        }

    let getAccountInfo headers =
        async {
            let! x =
                Http.AsyncRequestString(
                    "https://api.tech26.de/api/accounts",
                    headers = headers
                )
            return N26AccountInfo.Parse x
        }
