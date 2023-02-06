using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Microsoft.WindowsAzure.Storage;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Net.Http.Headers;
using Microsoft.WindowsAzure.Storage.Auth;
using Microsoft.WindowsAzure.Storage.Table;

namespace AzureTagsIPWatcher
{
    public static class CheckRanges
    {
        [FunctionName("CheckRanges")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = null)] HttpRequest req,
            ILogger log)
        {
            log.LogInformation("C# HTTP trigger function processed a request.");

            string ret = string.Empty;

            try
            {
                string body = String.Empty;

                using (StreamReader streamReader = new StreamReader(req.Body))
                {
                    body = await streamReader.ReadToEndAsync();
                }

                if (string.IsNullOrEmpty(body))
                {
                    throw new Exception("No request body found.");
                }

                dynamic data = JsonConvert.DeserializeObject(body);
                string serviceTagRegion = data.serviceTagRegion;

                // Get token and call the API
                var token = GetToken().Result;

                var latestServiceTag = GetFile(token).Result;

                if (latestServiceTag is null)
                {
                    throw new Exception("No tag file has been downloaded.");
                }

                // Download existing file from the blob, if exists, and compare the root changeNumber                
                var existingServiceTagEntity = await ReadTableAsync();

                // If there's a file in the blob container we retrieve it and compare the changeNumber value. If it's the same there's no changes in the file.
                if (existingServiceTagEntity is not null)
                {
                    if (existingServiceTagEntity.ChangeNumber == latestServiceTag.changeNumber)
                    {
                        // Return empty containers in the JSON file
                        AddressChanges diff = new AddressChanges();

                        diff.addedAddresses = Array.Empty<string>();
                        diff.removedAddresses = Array.Empty<string>(); ;

                        ret = JsonConvert.SerializeObject(diff);

                        log.LogInformation("The downloaded file has the same changenumber as the already existing one. No changes.");

                        // Return empty JSON containers
                        return new OkObjectResult(ret);
                    }
                }

                // Process the new file
                var serviceTagSelected = latestServiceTag.values.FirstOrDefault(st => st.name.ToLower() == serviceTagRegion);

                if (serviceTagSelected is not null)
                {
                    ServiceTagAddresses addresses = new ServiceTagAddresses();

                    addresses.rootchangenumber = latestServiceTag.changeNumber;
                    addresses.nodename = serviceTagSelected.name;
                    addresses.nodechangenumber = serviceTagSelected.properties.changeNumber;
                    addresses.addresses = serviceTagSelected.properties.addressPrefixes;

                    // If a file exists in the table get the differences
                    if (existingServiceTagEntity is not null)
                    {
                        string[] existingAddresses = JsonConvert.DeserializeObject<string[]>(existingServiceTagEntity.Addresses);

                        ret = CompareAddresses(existingAddresses, addresses.addresses);
                    }

                    // Finally upload the file with the new addresses
                    var newAddressJson = JsonConvert.SerializeObject(addresses);

                    //await UploadFileAsync(fileName, newAddressJson);
                    await WriteToTableAsync(addresses);
                }
                else
                {
                    AddressChanges diff = new AddressChanges();

                    diff.addedAddresses = Array.Empty<string>();
                    diff.removedAddresses = Array.Empty<string>();

                    ret = JsonConvert.SerializeObject(diff);

                    // Return empty JSON containers
                    return new OkObjectResult(ret);
                }
            }
            catch (Exception ex)
            {
                return new BadRequestObjectResult(ex.Message);
            }

            return new OkObjectResult(ret);
        }

        internal class AzureToken
        {
            public string token_type { get; set; }
            public string scope { get; set; }
            public string resource { get; set; }
            public string access_token { get; set; }
            public string refresh_token { get; set; }
            public string id_token { get; set; }
            public string expires_in { get; set; }
        }

        internal class AddressChanges
        {
            public string[] removedAddresses { get; set; }
            public string[] addedAddresses { get; set; }
        }

        public class ServiceTags
        {
            public class ServiceTag
            {
                public int changeNumber { get; set; }
                public string cloud { get; set; }
                public Value[] values { get; set; }
            }

            public class Value
            {
                public string name { get; set; }
                public string id { get; set; }
                public Properties properties { get; set; }
            }

            public class Properties
            {
                public int changeNumber { get; set; }
                public string region { get; set; }
                public int regionId { get; set; }
                public string platform { get; set; }
                public string systemService { get; set; }
                public string[] addressPrefixes { get; set; }
                public string[] networkFeatures { get; set; }
            }

        }

        public class ServiceTagAddresses
        {
            public int rootchangenumber { get; set; }
            public string nodename { get; set; }
            public int nodechangenumber { get; set; }
            public string[] addresses { get; set; }
        }

        public class ServiceTagEntity : TableEntity
        {
            public ServiceTagEntity(string eventId)
            {
                this.PartitionKey = "HTTP";
                this.RowKey = eventId;
            }

            public ServiceTagEntity() { }

            public int ChangeNumber { get; set; }

            public string NodeName { get; set; }

            public string Addresses { get; set; }
        }

        // Gets the authentication token
        public static async Task<string> GetToken()
        {
            HttpClient client = new HttpClient();

            string url = Environment.GetEnvironmentVariable("tokenURL");

            string content = String.Format("grant_type=client_credentials&resource={0}&client_id={1}&client_secret={2}&scope=openid", Environment.GetEnvironmentVariable("resource"), Environment.GetEnvironmentVariable("appId"), Environment.GetEnvironmentVariable("secret"));

            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, url);

            request.Content = new StringContent(content, Encoding.UTF8, "application/x-www-form-urlencoded");

            HttpResponseMessage response = await client.SendAsync(request);

            string responseString = await response.Content.ReadAsStringAsync();

            AzureToken token = JsonConvert.DeserializeObject<AzureToken>(responseString);

            return token.access_token;
        }

        // Downloads the file from the Azure REST API
        public static async Task<ServiceTags.ServiceTag> GetFile(string token)
        {
            HttpClient client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            string url = Environment.GetEnvironmentVariable("apiURL");

            string content = string.Empty;

            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url);

            request.Content = new StringContent(content, Encoding.UTF8, "application/x-www-form-urlencoded");

            HttpResponseMessage response = await client.SendAsync(request);

            string responseString = await response.Content.ReadAsStringAsync();

            ServiceTags.ServiceTag serviceTag = JsonConvert.DeserializeObject<ServiceTags.ServiceTag>(responseString);

            return serviceTag;
        }

        // Compares the existing file with the newest downloaded one, and returns a JSON file with the differences
        public static string CompareAddresses(string[] existingAddresses, string[] newAddresses)
        {
            string[] removedIPs = existingAddresses.Where(ip => !newAddresses.Contains(ip)).ToArray();
            string[] newIPs = newAddresses.Where(ip => !existingAddresses.Contains(ip)).ToArray();
            string ret = "";

            if (removedIPs.Length > 0 || newIPs.Length > 0)
            {
                AddressChanges diff = new AddressChanges();

                diff.addedAddresses = newIPs;
                diff.removedAddresses = removedIPs;

                ret = JsonConvert.SerializeObject(diff);
            }

            return ret;
        }

        // Writes the addresses into the Azure Table
        public static async Task WriteToTableAsync(ServiceTagAddresses serviceTagAddresses)
        {
            StorageCredentials creds = new StorageCredentials(Environment.GetEnvironmentVariable("ContainerName"), Environment.GetEnvironmentVariable("StorageKey"));
            CloudStorageAccount account = new CloudStorageAccount(creds, useHttps: true);
            CloudTableClient client = account.CreateCloudTableClient();
            CloudTable table = client.GetTableReference(Environment.GetEnvironmentVariable("StorageTable"));
            ServiceTagEntity serviceTagEntity;

            await table.CreateIfNotExistsAsync();

            var invertedTimeKey = DateTime.MaxValue.Ticks - DateTime.UtcNow.Ticks;

            serviceTagEntity = new ServiceTagEntity()
            {
                PartitionKey = "HTTP",
                RowKey = invertedTimeKey.ToString(),
                NodeName = serviceTagAddresses.nodename,
                ChangeNumber = serviceTagAddresses.rootchangenumber,
                Addresses = JsonConvert.SerializeObject(serviceTagAddresses.addresses)
            };

            await table.ExecuteAsync(TableOperation.Insert(serviceTagEntity));
        }

        // Reads from the Azure Table
        public static async Task<ServiceTagEntity> ReadTableAsync()
        {
            StorageCredentials creds = new StorageCredentials(Environment.GetEnvironmentVariable("ContainerName"), Environment.GetEnvironmentVariable("StorageKey"));
            CloudStorageAccount account = new CloudStorageAccount(creds, useHttps: true);
            CloudTableClient client = account.CreateCloudTableClient();
            CloudTable table = client.GetTableReference(Environment.GetEnvironmentVariable("StorageTable"));
            
            // etrieve data
            TableQuery<ServiceTagEntity> query = new();
            TableContinuationToken token = new();            
            
            var data = await table.ExecuteQuerySegmentedAsync(query, token);

            // Return the newest record in the table
            return data.OrderByDescending(r => r.Timestamp).FirstOrDefault();
        }
    }
}
