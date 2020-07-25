module N26DirectImport.Program

open System.IO
open Argu
open Milekic.YoLo

[<NoComparison; NoEquality>]
type Argument =
    | [<ExactlyOnce>] N26UserName of string
    | [<ExactlyOnce>] N26Password of string
    interface IArgParserTemplate with
        member _.Usage = "TODO Add usage"

let configFilePath = "N26DirectImport.config"

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

        let n26UserName = arguments.GetResult N26UserName
        let n26Password = arguments.GetResult N26Password

        let headers = N26Api.makeHeaders (n26UserName, n26Password)
                      |> Async.RunSynchronously

        printfn "Headers: %A" headers

        0
    with
        | :? ArguParseException as ex ->
            printfn "%s" ex.Message
            int ex.ErrorCode
        | x ->
            printfn "%s" x.Message
            -1
