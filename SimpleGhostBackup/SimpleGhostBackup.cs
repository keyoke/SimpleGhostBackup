using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.File;
using Newtonsoft.Json.Linq;
using Polly;
using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace SimpleGhostBackup
{
    /// <summary>
    /// Simple retry https://stackoverflow.com/a/35183487
    /// </summary>
    public class HttpRetryMessageHandler : DelegatingHandler
    {
        public HttpRetryMessageHandler(HttpClientHandler handler) : base(handler) { }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            System.Threading.CancellationToken cancellationToken) =>
            Policy
                .Handle<HttpRequestException>()
                .Or<TaskCanceledException>()
                .OrResult<HttpResponseMessage>(x => !x.IsSuccessStatusCode)
                .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(3, retryAttempt)))
                .ExecuteAsync(() => base.SendAsync(request, cancellationToken));
    }

    public static class SimpleGhostBackup
    {
        [FunctionName("SimpleGhostBackup")]
        public async static Task Run([TimerTrigger("0 0 0 * * Sun")]TimerInfo myTimer, ILogger log, ExecutionContext context)
        {
            log.LogInformation($"SimpleGhostBackup function started execution at: {DateTime.Now}");

            var config = new ConfigurationBuilder()
             .SetBasePath(context.FunctionAppDirectory)
             .AddJsonFile("local.settings.json", optional: true, reloadOnChange: true)
             .AddEnvironmentVariables()
             .Build();

            var clientId = config["ClientId"];
            if (String.IsNullOrEmpty(clientId))
                throw new ArgumentNullException("ClientId is Required!");

            var clientSecret = config["ClientSecret"];
            if (String.IsNullOrEmpty(clientSecret))
                throw new ArgumentNullException("ClientSecret is Required!");

            var blogUrl = config["BlogUrl"];
            if (String.IsNullOrEmpty(blogUrl))
                throw new ArgumentNullException("BlogUrl is Required!");

            var storageShareName = config["StorageShareName"];
            if (String.IsNullOrEmpty(storageShareName))
                throw new ArgumentNullException("StorageShareName is Required!");

            var storageConnection = config["StorageConnectionString"];
            if (String.IsNullOrEmpty(storageConnection))
                throw new ArgumentNullException("storageConnection is Required!");

            var client = new HttpClient(new HttpRetryMessageHandler(new HttpClientHandler()))
            {
                BaseAddress = new Uri(String.Format("https://{0}", blogUrl))
            };

            log.LogInformation($"Requesting Ghost Backup");
            var response = await client.PostAsync(String.Format("/ghost/api/v0.1/db/backup?client_id={0}&client_secret={1}", clientId, clientSecret), null);

            if (response.StatusCode == System.Net.HttpStatusCode.OK)
            {
                // Get our response content which contains the created backup file name
                var content = await response.Content.ReadAsStringAsync();
                var json = JObject.Parse(content);

                // Connect to our Azure Storage Account
                CloudStorageAccount storageAccount = CloudStorageAccount.Parse(storageConnection);
                CloudFileClient fileClient = storageAccount.CreateCloudFileClient();
                CloudFileShare share = fileClient.GetShareReference(storageShareName);
                CloudFileDirectory root = share.GetRootDirectoryReference();
                CloudFileDirectory data = root.GetDirectoryReference("data");

                //Does the data folder exist
                if (await data.ExistsAsync())
                {
                    log.LogInformation($"Data folder exists.");

                    // get the backup file name
                    var filename = System.IO.Path.GetFileName((string)json["db"][0]["filename"]);
                    CloudFile file = data.GetFileReference(filename);

                    //Confirm that the backup file exists
                    if (await file.ExistsAsync())
                    {
                        // Create the snapshotg of the file share
                        log.LogInformation($"Backup file created - {0}", filename);
                        log.LogInformation($"Creating Azure Fileshare Snapshot");
                        await share.SnapshotAsync();
                    }
                }
            }

            log.LogInformation($"SimpleGhostBackup function ended execution at: {DateTime.Now}");
        }
    }
}
