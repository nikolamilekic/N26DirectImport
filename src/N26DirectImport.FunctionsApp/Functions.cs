using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Storage;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Microsoft.WindowsAzure.Storage.Table;

using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;

using N26DirectImport.Core;

namespace N26DirectImport.FunctionsApp
{
    public class Binding : TableEntity
    {
        public string Ynab;
    }

    public static class Functions
    {
        private static Importer.Facade ImporterFacade;
        private static Importer.Facade GetImporterFacade(ExecutionContext context)
        {
            if (ImporterFacade == null)
            {
                var config =
                    new ConfigurationBuilder()
                        .SetBasePath(context.FunctionAppDirectory)
                        .AddJsonFile(
                            "local.settings.json",
                            optional: true,
                            reloadOnChange: true)
                        .AddEnvironmentVariables()
                        .Build();

                var facade = new Importer.Facade(
                    config["YnabKey"],
                    config["N26Username"],
                    config["N26Password"],
                    config["N26Token"]);

                ImporterFacade = facade;
            }

            return ImporterFacade;
        }

        [FunctionName("Update")]
        public static async Task Update(
            [TimerTrigger("0 */10 * * * *")]TimerInfo myTimer,
            [Table("Transactions")] CloudTable bindings,
            [Blob(
                "info",
                Connection = "AzureWebJobsStorage" )] CloudBlobContainer info,
            ILogger log,
            ExecutionContext context)
        {
            var facade = GetImporterFacade(context);
            var balance = await facade.Run(bindings);

            var blob = info.GetBlockBlobReference("balance");
            await blob.UploadTextAsync(balance.ToString());

            log.LogInformation($"Updated Ynab automatically at {DateTime.Now}");
        }

        [FunctionName("TriggerUpdate")]
        public static async Task<string> TriggerUpdate(
            [HttpTrigger(
                AuthorizationLevel.Function,
                "get",
                Route = null)]HttpRequest req,
            [Table("Transactions")] CloudTable bindings,
            [Blob(
                "info",
                Connection = "AzureWebJobsStorage" )] CloudBlobContainer info,
            ILogger log,
            ExecutionContext context)
        {
            var facade = GetImporterFacade(context);
            var balance = await facade.Run(bindings);

            var blob = info.GetBlockBlobReference("balance");
            await blob.UploadTextAsync(balance.ToString());

            log.LogInformation($"Updated Ynab manually at {DateTime.Now}");
            return balance.ToString();
        }

        [FunctionName("Backup")]
        public static async Task Backup(
            [TimerTrigger("0 2 0 * * *")]TimerInfo myTimer,
            [Blob(
                "backups",
                FileAccess.ReadWrite,
                Connection = "AzureWebJobsStorage" )] CloudBlobContainer backups,
            ILogger log,
            ExecutionContext context)
        {
            var facede = GetImporterFacade(context);
            var transactions = facede.GetAllTransactions();

            var now = DateTime.Now;
            var fileName = now.ToString("yyyy-MM-ddTHH-mm-ss") + ".json";
            var blob = backups.GetBlockBlobReference(fileName);
            await blob.UploadTextAsync(transactions);

            log.LogInformation($"Backed-up Ynab at: {DateTime.Now}");
        }

        [FunctionName("GetBalance")]
        public static string GetBalance(
            [HttpTrigger(
                AuthorizationLevel.Function,
                "get",
                Route = null)]HttpRequest req,
            [Blob(
                "info/balance",
                Connection = "AzureWebJobsStorage" )] string balance,
            ILogger log)
        {
            log.LogInformation($"Retrieved balance at: {DateTime.Now}");
            return balance.ToString();
        }

        [FunctionName("GetVersion")]
        public static string GetVersion(
            [HttpTrigger(
                AuthorizationLevel.Function,
                "get",
                Route = null)] HttpRequest req) =>
                Assembly.GetExecutingAssembly().GetName().Version.ToString();
    }
}
