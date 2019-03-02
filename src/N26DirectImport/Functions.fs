
module N26DirectImport.Functions

open System
open System.IO
open System.Reflection
open System.Threading.Tasks
open Microsoft.Azure.WebJobs
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Configuration
open Microsoft.WindowsAzure.Storage.Blob
open Microsoft.Azure.WebJobs.Extensions.Http
open Microsoft.AspNetCore.Http
open Microsoft.Azure.WebJobs.Hosting
open Microsoft.Azure.WebJobs.Host.Config
open Microsoft.Azure.WebJobs.Description
open Microsoft.WindowsAzure.Storage

open MBrace.FsPickler.Json

[<AttributeUsage(AttributeTargets.Parameter, AllowMultiple = true)>]
[<Binding>]
type ConfigAttribute() = inherit Attribute()

type Config = {
    YnabKey : string
    N26Username : string
    N26Password : string
    N26Token : string
    Storage : string
}

type ConfigProvider(configuration : IConfiguration) =
    let config = {
        YnabKey = configuration.["YnabKey"]
        N26Username = configuration.["N26Username"]
        N26Password = configuration.["N26Password"]
        N26Token = configuration.["N26Token"]
        Storage = configuration.["AzureWebJobsStorage"]
    }
    interface IExtensionConfigProvider with
        member __.Initialize context =
            context
                .AddBindingRule<ConfigAttribute>()
                .BindToInput(fun _ -> config)
            |> ignore

type Startup() =
    interface IWebJobsStartup with
        member __.Configure builder =
            builder.AddExtension<ConfigProvider>() |> ignore

[<assembly: WebJobsStartup(typedefof<Startup>)>]
do ()

let makeN26Headers config =
    N26.makeHeaders config.N26Username config.N26Password config.N26Token
let makeYnabHeaders config = Ynab.makeHeaders config.YnabKey

let serializer = FsPickler.CreateJsonSerializer()
let deserializeBlob (blob : CloudBlockBlob) = async {
    let! stream = blob.OpenReadAsync() |> Async.AwaitTask
    let! bytes = stream.AsyncRead <| int stream.Length
    return serializer.UnPickle bytes
}

let mutable bindingsCache = None
let runWithBindings (log : ILogger) config f = async {
    let bindingsBlob =
        lazy
        let account = config.Storage |> CloudStorageAccount.Parse
        let client = account.CreateCloudBlobClient()
        let container = client.GetContainerReference("info")
        container.GetBlockBlobReference("bindings")

    let! bindings = async {
        match bindingsCache with
        | Some b ->
            log.LogInformation "Using bindings cache"
            return b
        | None ->
            log.LogInformation "Bindings cache empty. Retrieving bindings."
            return! deserializeBlob (bindingsBlob.Force())
    }

    let! result, newBindings = f bindings

    if bindings <> newBindings then
        let pickled = serializer.Pickle newBindings
        do!
            bindingsBlob.Force().UploadFromByteArrayAsync(pickled, 0, pickled.Length)
            |> Async.AwaitTask

    bindingsCache <- Some newBindings
    return result
}

[<FunctionName("Update")>]
let update
    (
        [<TimerTrigger("0 */10 * * * *")>] (timerInfo : TimerInfo),
        [<Config>] config,
        log
    ) =
    async {
        let! n26Headers = makeN26Headers config
        let ynabHeaders = makeYnabHeaders config
        let! _ = runWithBindings log config (Importer.run n26Headers ynabHeaders)

        log.LogInformation
        <| sprintf "Updated Ynab automatically at %A" DateTime.Now
    }
    |> Async.StartAsTask :> Task

[<FunctionName("TriggerUpdate")>]
let triggerUpdate
    (
        [<HttpTrigger(AuthorizationLevel.Function, "get")>]
            (request : HttpRequest),
        [<Config>] config,
        log
    ) =
    async {
        let! n26Headers = makeN26Headers config
        let ynabHeaders = makeYnabHeaders config
        bindingsCache <- None
        let! info =
            runWithBindings log config (Importer.run n26Headers ynabHeaders)
        let! accountInfo = N26.getAccountInfo n26Headers

        log.LogInformation
        <| sprintf "Updated Ynab manually at %A" DateTime.Now

        return
            sprintf
                "Cleared balance: %M\nInfo:\n%A"
                (accountInfo.BankBalance)
                info
    }
    |> Async.StartAsTask

[<FunctionName("GetVersion")>]
let getVersion
    (
        [<HttpTrigger(AuthorizationLevel.Function, "get")>]
            (request : HttpRequest)
    ) =
    Assembly.GetExecutingAssembly().GetName().Version.ToString()
