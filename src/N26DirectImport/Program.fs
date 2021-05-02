module N26DirectImport.Program

open System
open System.Diagnostics
open System.IO
open Argu
open Milekic.YoLo
open FSharpPlus
open MBrace.FsPickler
open FSharp.Data

[<NoComparison; NoEquality>]
type Argument =
    | [<ExactlyOnce>] N26UserName of string
    | N26Password of string
    | N26PasswordCommand of string
    | [<ExactlyOnce>] YnabAccountId of string
    | [<ExactlyOnce>] YnabBudgetId of string
    | YnabAuthenticationToken of string
    | YnabAuthenticationTokenCommand of string
    | [<NoAppSettings; Unique>] From of string
    | [<NoAppSettings; Unique>] Until of string
    | [<NoAppSettings>] Version
    interface IArgParserTemplate with
        member _.Usage = " "

let configFilePath =
    Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".N26DirectImport",
        "N26DirectImport.config")

let accessTokenFilePath =
    Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".N26DirectImport",
        "N26DirectImportAccessToken")

Directory.CreateDirectory(Path.GetDirectoryName(configFilePath)) |> ignore

let runCommand (command : string) =
    let words = command.Split (" ")
    let command = Array.head words
    let args = words |> Array.skip 1 |> String.concat " "
    let psi =
        ProcessStartInfo(
            command,
            args,
            RedirectStandardOutput=true,
            RedirectStandardError=true)
    let p = Process.Start(psi)
    p.WaitForExit()
    if p.ExitCode = 0
    then p.StandardOutput.ReadToEnd().Trim()
    else failwith (p.StandardError.ReadToEnd())

[<EntryPoint>]
let main argv =
    printfn
        "N26DirectImport Version: %s (%s)"
        (Metadata.getCallingAssemblyInformationalVersion())
        (DateTimeOffset.Parse(ThisAssembly.Git.CommitDate).ToString("yyyy-MM-dd"))

    try
        let arguments =
            ArgumentParser
                .Create(programName = "N26DirectImport")
                .Parse(
                    inputs = argv,
                    configurationReader =
                        if File.Exists configFilePath
                        then ConfigurationReader.FromAppSettingsFile configFilePath
                        else ConfigurationReader.NullReader
                )

        if arguments.TryGetResult Version |> Option.isSome then exit 0

        arguments.GetAllResults()
        |> arguments.Parser.PrintAppSettingsArguments
        |> curry File.WriteAllText configFilePath

        let n26AuthenticationHeaders =
            let serializer = FsPickler.CreateXmlSerializer(indent = true)
            let accessToken =
                if File.Exists accessTokenFilePath
                then serializer.Deserialize(File.OpenRead accessTokenFilePath) |> Some
                else None
                |> flip Option.bind <| fun (t : N26Api.N26AccessToken) ->
                    if t.ValidUntil > DateTimeOffset.Now then
                        printfn "Reusing saved token."
                        Some t
                    else
                        printfn "Saved access token expired on %A" t.ValidUntil
                        None
                |> flip Option.defaultWith <| fun () ->
                    let n26UserName = arguments.GetResult N26UserName

                    let n26Password =
                        arguments.TryGetResult N26Password
                        |> Option.defaultWith(fun () ->
                            arguments.PostProcessResult(
                                N26PasswordCommand, runCommand))

                    let result =
                        N26Api.requestAccessToken (n26UserName, n26Password)
                        |> Async.RunSynchronously
                    serializer.Serialize(File.Create accessTokenFilePath, result)
                    result
            N26Api.makeHeaders accessToken

        let from =
            arguments.TryPostProcessResult (From, DateTimeOffset.Parse)
            |> Option.defaultValue (DateTimeOffset.Now - TimeSpan.FromDays 14.0)
        let until =
            arguments.TryPostProcessResult (Until, DateTimeOffset.Parse)
            |> Option.defaultValue DateTimeOffset.Now

        let typesToInclude = [| "PT"; "DT"; "CT"; "DD"; "AV"; "PF" |]
        let ynabAccountId =
            arguments.PostProcessResult (YnabAccountId, Guid.Parse)
        let ynabBudgetId =
            arguments.PostProcessResult (YnabBudgetId, Guid.Parse)

        let ynabAuthenticationToken =
            arguments.TryGetResult YnabAuthenticationToken
            |> Option.defaultWith (fun () ->
                arguments.PostProcessResult(
                    YnabAuthenticationTokenCommand, runCommand))

        let n26Transactions =
            N26Api.getTransactions n26AuthenticationHeaders (from, until)
            |> Seq.toArray

        printfn "The following transactions have been received from N26:"

        n26Transactions
        |> Seq.iter (fun x -> printfn "%s" (x.JsonValue.ToString()))

        let transactions =
            n26Transactions
            |> Seq.where (fun x -> Array.contains x.Type typesToInclude)
            |> flip Seq.map <| fun n26 ->
                let payeeName =
                    seq { n26.MerchantName; n26.PartnerName }
                    |> Seq.choose id
                    |> Seq.tryFind (fun x -> String.IsNullOrWhiteSpace(x) = false)
                let referenceText =
                    n26.ReferenceText
                    |> flip Option.bind <| fun x ->
                        if String.IsNullOrWhiteSpace(x)
                        then None else Some x

                seq {
                    "import_id", n26.Id.ToString()
                    "cleared", "cleared"
                    "amount", Math.Round(n26.Amount * 1000.0m).ToString()
                    "payee_name", Option.defaultValue "" payeeName
                    "date",
                        n26.VisibleTs
                        |> int64
                        |> DateTimeOffset.FromUnixTimeMilliseconds
                        |> fun d -> d.ToLocalTime().Date.ToString("yyyy-MM-dd")
                    "memo",
                        seq {
                            payeeName |>> sprintf "Payee: %s"
                            referenceText |>> sprintf "Reference: %s"
                        }
                        |> Seq.choose id
                        |> String.concat " "
                    "account_id", ynabAccountId.ToString()
                }
                |> Seq.map (fun (k,v ) -> k, JsonValue.String v)
                |> Seq.toArray
                |> JsonValue.Record
                |> YnabApi.TransactionsResponse.Transaction

        printfn "The following transactions will be imported:"
        transactions
        |> Seq.iter (fun x -> printfn "%s" (x.JsonValue.ToString()))

        printfn "Would you like to proceed (y/N)?"

        if Console.ReadLine().ToLower() = "y" then
            let ynabHeaders = YnabApi.makeHeaders ynabAuthenticationToken
            YnabApi.addYnabTransactions ynabHeaders ynabBudgetId transactions
            |> Result.map (fun _ -> printfn "Done.")
            |> Result.failOnError "Adding YNAB transactions failed"

        let accountInfo = N26Api.getAccountInfo n26AuthenticationHeaders

        printfn "Cleared balance: %s" (String.Format("{0,0:N2}", accountInfo.BankBalance))

        0
    with
        | :? ArguParseException as ex ->
            printfn "%s" ex.Message
            int ex.ErrorCode
        | x ->
            printfn "%s" x.Message
            -1
