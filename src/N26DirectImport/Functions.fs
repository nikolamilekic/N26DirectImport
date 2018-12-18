
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

[<AttributeUsage(AttributeTargets.Parameter, AllowMultiple = true)>]
[<Binding>]
type ConfigAttribute() = inherit Attribute()

type ConfigProvider(configuration : IConfiguration) =
    let config : Importer.Config = {
        YnabKey = configuration.["YnabKey"]
        N26Username = configuration.["N26Username"]
        N26Password = configuration.["N26Password"]
        N26Token = configuration.["N26Token"]
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

[<FunctionName("Update")>]
let update
    (
        [<TimerTrigger("0 */10 * * * *")>] (timerInfo : TimerInfo),
        [<Config>] config,
        [<Blob("info/bindings", FileAccess.ReadWrite)>] bindings,
        [<Blob("info/balance", FileAccess.ReadWrite)>] balance,
        [<Blob("info/lastReconciliation", FileAccess.ReadWrite)>] lastReconciliation,
        (log : ILogger)
    ) =
    async {
        let! _ = Importer.run config bindings balance lastReconciliation

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
        [<Blob("info/bindings", FileAccess.ReadWrite)>] bindings,
        [<Blob("info/balance", FileAccess.ReadWrite)>] balance,
        [<Blob("info/lastReconciliation", FileAccess.ReadWrite)>] lastReconciliation,
        (log : ILogger)
    ) =
    async {
        let! newBalance = Importer.run config bindings balance lastReconciliation

        log.LogInformation
        <| sprintf "Updated Ynab manually at %A" DateTime.Now

        return newBalance
    }
    |> Async.StartAsTask

[<FunctionName("Backup")>]
let backup
    (
        [<TimerTrigger("0 2 0 * * *")>] (timerInfo : TimerInfo),
        [<Config>] (config : Importer.Config),
        [<Blob("backups", FileAccess.ReadWrite)>] (backups : CloudBlobContainer),
        (log : ILogger)
    ) =
    async {
        let headers = Ynab.makeHeaders config.YnabKey
        let! transactions = Ynab.getTransactionsString headers None
        let now = DateTime.Now
        let fileName = now.ToString("yyyy-MM-ddTHH-mm-ss") + ".json"
        let blob = backups.GetBlockBlobReference(fileName)
        do! blob.UploadTextAsync(transactions) |> Async.AwaitTask
        do!
            blob.SetStandardBlobTierAsync(StandardBlobTier.Cool)
            |> Async.AwaitTask

        log.LogInformation(sprintf "Made a Ynab backup at %A" DateTime.Now)
    }
    |> Async.StartAsTask :> Task

let getBlobs (container : CloudBlobContainer) =
    let rec inner results token = async {
        let! next = container.ListBlobsSegmentedAsync token |> Async.AwaitTask
        let results = next.Results::results
        if isNull next.ContinuationToken
        then return results |> Seq.collect id
        else return! inner results token
    }
    inner [] null

[<FunctionName("RemoveOldBackups")>]
let removeOldBackups
    (
        [<TimerTrigger("0 0 0 * * 1")>] (timerInfo : TimerInfo),
        [<Blob("backups", FileAccess.ReadWrite)>] backups,
        (log : ILogger)
    ) =
    async {
        let oneMonthAgo = DateTimeOffset.UtcNow.Subtract(TimeSpan.FromDays(30.0))
        let! blobLists = getBlobs backups
        do!
            blobLists
            |> Seq.cast<CloudBlockBlob>
            |> Seq.where (fun b ->
                match b.Properties.Created |> Option.ofNullable with
                | None -> false
                | Some c -> c < oneMonthAgo)
            |> Seq.map (fun b -> b.DeleteAsync() |> Async.AwaitTask)
            |> Async.Parallel
            |> Async.Ignore

        log.LogInformation(sprintf "Removed old Ynab backups at %A" DateTime.Now)
    }
    |> Async.StartAsTask :> Task

[<FunctionName("GetBalance")>]
let getBalance
    (
        [<HttpTrigger(AuthorizationLevel.Function, "get")>]
            (request : HttpRequest),
        [<Blob("info/balance")>] (balance : string),
        (log : ILogger)
    ) =
    log.LogInformation(sprintf "Retrieved balance at: %A" (DateTime.Now))
    balance

[<FunctionName("GetVersion")>]
let getVersion
    (
        [<HttpTrigger(AuthorizationLevel.Function, "get")>]
            (request : HttpRequest)
    ) =
    Assembly.GetExecutingAssembly().GetName().Version.ToString()
