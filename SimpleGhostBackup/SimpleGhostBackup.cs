using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.File;
using Microsoft.WindowsAzure.Storage.File.Protocol;
using Microsoft.WindowsAzure.Storage.Shared.Protocol;
using Newtonsoft.Json.Linq;
using Polly;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using System.Linq;

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
                .OrResult<HttpResponseMessage>(x => !x.IsSuccessStatusCode)
                .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(3, retryAttempt)))
                .ExecuteAsync(() => base.SendAsync(request, cancellationToken));
    }

    public static class SimpleGhostBackup
    {
        [FunctionName("SimpleGhostBackup")]
        public async static Task Run([TimerTrigger("0 0 0 * * Sun"
            #if DEBUG
            ,RunOnStartup=true
            #endif
            )]TimerInfo myTimer, ILogger log, ExecutionContext context)
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

            // Let get the number of snapshots that we should keep, default to last 4
            int maxSnapshots = 4;
            Int32.TryParse(config["MaxSnapshots"], out maxSnapshots);

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
                        log.LogInformation($"Backup file created - {filename}");
                        log.LogInformation($"Creating Azure Fileshare Snapshot");
                        var s = await share.SnapshotAsync();
                        if (s != null)
                        {
                            //Lets get all the current shares/snapshots
                            FileContinuationToken token = null;
                            var snapshots = new List<CloudFileShare>();
                            do
                            {
                                ShareResultSegment resultSegment = await fileClient.ListSharesSegmentedAsync(storageShareName, token);
                                snapshots.AddRange(resultSegment.Results);
                                token = resultSegment.ContinuationToken;
                            }
                            while (token != null);

                            //lets delete the old ones
                            foreach (var snapshot in snapshots.Where(os => os.IsSnapshot).OrderByDescending(oos => oos.SnapshotTime).Skip(maxSnapshots).ToList())
                            {
                                try
                                {
                                    log.LogInformation($"Deleting snapshot - {snapshot.Name}, Created at {snapshot.SnapshotTime}");
                                    await snapshot.DeleteAsync();
                                }
                                catch (Exception ex)
                                {
                                    log.LogError($"Failed to delete snapshot - '{ex}'");
                                }    
                            }
                        }
                    }
                }
            }

            log.LogInformation($"SimpleGhostBackup function ended execution at: {DateTime.Now}");
        }
    }
}
