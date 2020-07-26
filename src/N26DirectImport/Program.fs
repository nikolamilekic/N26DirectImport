module N26DirectImport.Program

open System
open System.IO
open Argu
open Milekic.YoLo
open MBrace.FsPickler

[<NoComparison; NoEquality>]
type Argument =
    | [<ExactlyOnce>] N26UserName of string
    | [<ExactlyOnce>] N26Password of string
    interface IArgParserTemplate with
        member _.Usage = "TODO Add usage"

let configFilePath = "N26DirectImport.config"
let accessTokenFilePath = "N26DirectImportAccessToken"

[<EntryPoint>]
let main argv =
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
                        printfn "%A" t.ValidUntil
                        printfn "Saved access token has expired."
                        None
                |> flip Option.defaultWith <| fun () ->
                    let n26UserName = arguments.GetResult N26UserName
                    let n26Password = arguments.GetResult N26Password
                    let result =
                        N26Api.requestAccessToken (n26UserName, n26Password)
                        |> Async.RunSynchronously
                    serializer.Serialize(File.Create accessTokenFilePath, result)
                    result
            N26Api.makeHeaders accessToken

        N26Api.getAccountInfo n26AuthenticationHeaders
        |> Async.RunSynchronously
        |> printfn "%A"

        0
    with
        | :? ArguParseException as ex ->
            printfn "%s" ex.Message
            int ex.ErrorCode
        | x ->
            printfn "%s" x.Message
            -1
