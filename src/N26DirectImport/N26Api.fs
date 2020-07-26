module N26DirectImport.N26Api

open System
open System.Net
open FSharp.Data

[<Literal>]
let TransactionsResponseSamplePath = __SOURCE_DIRECTORY__ + "/Samples/N26Transactions.json"
type TransactionsResponse = JsonProvider<TransactionsResponseSamplePath, RootName="Transaction">

[<Literal>]
let MfaResponseSamplePath = __SOURCE_DIRECTORY__ + "/Samples/MfaResponse.json"
type MfaTokenResponse = JsonProvider<MfaResponseSamplePath>

[<Literal>]
let AccessTokenSamplePath = __SOURCE_DIRECTORY__ + "/Samples/AccessToken.json"
type AccessToken = JsonProvider<AccessTokenSamplePath>

type N26AccountInfo = JsonProvider<"""{"id":"343dde6b-de8d-4651-bb1c-59b9c9d1f9a4","availableBalance":14.47,"usableBalance":14.47,"bankBalance":55.5,"iban":"DE89100110012628266077","bic":"NTSBDEB1XXX","bankName":"N26 Bank","seized":false,"currency":"EUR","legalEntity":"EU","externalId":{"iban":"DE89100110012628266077"}}""">

let authenticationHeaders = [
    "Authorization", "Basic bXktdHJ1c3RlZC13ZHBDbGllbnQ6c2VjcmV0"
    "device-token", "0ff70bc0-787a-4a0e-a7a3-c6efce0b92a3"
]

let getTransactions headers (from : DateTimeOffset, until : DateTimeOffset) =
    let rec inner lastId = seq {
        let current =
            Http.RequestString (
                "https://api.tech26.de/api/smrt/transactions",
                headers = headers,
                query = [
                    yield "from", from.ToUnixTimeMilliseconds().ToString()
                    yield "to", until.ToUnixTimeMilliseconds().ToString()
                    yield "limit", "200"

                    match lastId with
                    | None -> ()
                    | Some x ->
                        yield "lastId", x
                ]
            )
            |> TransactionsResponse.Parse

        yield! current
        if Array.length current = 200 then
            let lastId = (Array.last current).Id.ToString()
            yield! inner (Some lastId)
    }

    inner None

type N26AccessToken = {
    Token : Guid
    ValidUntil : DateTimeOffset
}

let makeHeaders token =
    Seq.singleton ("Authorization", sprintf "Bearer %A" token.Token)

let requestAccessToken (username, password) = async {
    printfn "Requesting MFA token..."

    let mfaResponse =
        Http.RequestString (
            "https://api.tech26.de/oauth/token",
            body = (
                [
                    "username", username
                    "password", password
                    "grant_type", "password"
                ] :> seq<_>
                |> FormValues),
            headers = authenticationHeaders,
            httpMethod = "POST",
            silentHttpErrors = true
        )
        |> MfaTokenResponse.Parse

    printfn "MFA token received. Sending MFA challenge..."

    Http.Request (
        "https://api.tech26.de/api/mfa/challenge",
        body = (
            [
                "challengeType", JsonValue.String "oob"
                "mfaToken", JsonValue.String (mfaResponse.MfaToken.ToString())
            ]
            |> List.toArray
            |> JsonValue.Record
            |> fun r -> r.ToString() |> TextRequest),
        headers = authenticationHeaders @ [ "Content-Type", "application/json" ],
        httpMethod = "POST"
    )
    |> ignore

    printfn "MFA challenge sent. Please approve authentication request."

    let timeout = DateTime.Now + TimeSpan.FromMinutes 5.0

    let rec tryGetToken () = async {
        try
            let! tokenString =
                Http.AsyncRequestString (
                    "https://api.tech26.de/oauth/token",
                    body = (
                        [
                            "grant_type", "mfa_oob"
                            "mfaToken", mfaResponse.MfaToken.ToString()
                        ] :> seq<_>
                        |> FormValues),
                    headers = authenticationHeaders,
                    httpMethod = "POST"
                )

            let rawToken = AccessToken.Parse tokenString
            return {
                Token = rawToken.AccessToken
                ValidUntil =
                    DateTimeOffset.Now
                    + TimeSpan.FromSeconds (float rawToken.ExpiresIn)
            }

        with :? WebException as e ->
            let waitTime = TimeSpan.FromSeconds 2.0
            if (DateTime.Now + waitTime) < timeout then
                do! Async.Sleep(int waitTime.TotalMilliseconds)
                return! tryGetToken ()
            else return raise e
    }

    let! result = tryGetToken ()

    printfn "Authenticated."

    return result
}

let getAccountInfo headers =
    Http.RequestString(
        "https://api.tech26.de/api/accounts",
        headers = headers
    )
    |> N26AccountInfo.Parse
