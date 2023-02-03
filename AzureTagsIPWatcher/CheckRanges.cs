using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Microsoft.WindowsAzure.Storage.Blob;
using Microsoft.WindowsAzure.Storage;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using HtmlAgilityPack;
using System.Text;
using System.Net.Http.Headers;

namespace AzureTagsIPWatcher
{
    public static class CheckRanges
    {
        [FunctionName("CheckRanges")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest req,
            ILogger log)
        {
            log.LogInformation("C# HTTP trigger function processed a request.");

            string ret = string.Empty;
            string fileName = "AzureServiceTags.json";

            try
            {
                string body = String.Empty;

                using (StreamReader streamReader = new StreamReader(req.Body))
                {
                    body = await streamReader.ReadToEndAsync();
                }

                if (string.IsNullOrEmpty(body))
                {
                    throw new Exception("No body found.");
                }

                dynamic data = JsonConvert.DeserializeObject(body);
                string serviceTagRegion = data.serviceTagRegion;

                var token = GetToken().Result;

                var serviceTag = GetFile(token).Result;

                // Download existing file from the blob, if exists, and compare the root changeNumber
                bool existsFile = FileExistsAsync(fileName).Result;
                ServiceTagAddresses existingAddresses = new ServiceTagAddresses();

                // If there's a file in the blob container we retrieve it and compare the changeNumber value. If it's the same there's no changes to the file.
                if (existsFile == true)
                {
                    string existingJson = await DownloadFileAsync(fileName);

                    existingAddresses = JsonConvert.DeserializeObject<ServiceTagAddresses>(existingJson);

                    if (existingAddresses.rootchangenumber == serviceTag.changeNumber)
                    {
                        // Return empty containers in the JSON file
                        AddressChanges diff = new AddressChanges();

                        diff.addedAddresses = Array.Empty<string>();
                        diff.removedAddresses = Array.Empty<string>(); ;

                        ret = JsonConvert.SerializeObject(diff);

                        log.LogInformation("The downloaded file has the same changenumber as the already existing one. No changes.");

                        return new OkObjectResult(ret);
                    }
                }

                // Process the new file
                foreach (var val in serviceTag.values)
                {
                    if (val.name.ToLower() == serviceTagRegion)
                    {
                        ServiceTagAddresses addresses = new ServiceTagAddresses();

                        addresses.rootchangenumber = serviceTag.changeNumber;
                        addresses.nodename = val.name;
                        addresses.nodechangenumber = val.properties.changeNumber;
                        addresses.addresses = val.properties.addressPrefixes;

                        if (existingAddresses.addresses is not null)
                        {
                            ret = await CompareFilesAsync(existingAddresses.addresses, addresses.addresses);

                            await ArchiveFileAsync(fileName, string.Format("ServiceTags-{0}.json", DateTime.UtcNow.ToString()));
                        }

                        // Finally upload the file with the new addresses
                        var newAddressJson = JsonConvert.SerializeObject(addresses);

                        await UploadFileAsync(fileName, newAddressJson);


                        break;
                    }
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

        internal class ServiceTagAddresses
        {
            public int rootchangenumber { get; set; }
            public string nodename { get; set; }
            public int nodechangenumber { get; set; }
            public string[] addresses { get; set; }
        }


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
        public static async Task<string> CompareFilesAsync(string[] existingAddresses, string[] newAddresses)
        {
            string[] removedIPs = existingAddresses.Where(ip => !newAddresses.Contains(ip)).ToArray();
            string[] newIPs = newAddresses.Where(ip => !existingAddresses.Contains(ip)).ToArray();
            string ret = "";

            if (removedIPs.Length > 0 || newIPs.Length > 0)
            {
                AddressChanges diff = new AddressChanges();

                diff.addedAddresses = newIPs;
                diff.removedAddresses = removedIPs;

                string diffFileName = String.Format("diff_{0}", DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture));
                ret = JsonConvert.SerializeObject(diff);

                await UploadFileAsync(diffFileName, ret);
            }

            return ret;
        }

        // Moves a file from source to destination
        public static async Task ArchiveFileAsync(string fileName, string archiveFileName)
        {
            var blobSource = GetBlob(Environment.GetEnvironmentVariable("ContainerConn"), Environment.GetEnvironmentVariable("ContainerName"), fileName);
            var blobDestination = GetBlob(Environment.GetEnvironmentVariable("ContainerConn"), String.Format("{0}/archive", Environment.GetEnvironmentVariable("ContainerName")), fileName);

            blobSource.Properties.ContentType = "application/json";
            blobDestination.Properties.ContentType = "application/json";

            await blobDestination.StartCopyAsync(blobSource);
        }

        // Check if file exists
        public static async Task<bool> FileExistsAsync(string fileName)
        {
            var blob = GetBlob(Environment.GetEnvironmentVariable("ContainerConn"), Environment.GetEnvironmentVariable("ContainerName"), fileName);

            return await blob.ExistsAsync();
        }

        // Uploads a file to the blob storage account configured in the environment variables
        public static async Task UploadFileAsync(string fileName, string content)
        {
            var blob = GetBlob(Environment.GetEnvironmentVariable("ContainerConn"), Environment.GetEnvironmentVariable("ContainerName"), fileName);

            using (Stream stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content)))
            {
                await blob.UploadFromStreamAsync(stream);
            }

            
        }

        // Downloads a file from the blob storage account configured in the environment variables
        public static async Task<string> DownloadFileAsync(string fileName)
        {
            var blob = GetBlob(Environment.GetEnvironmentVariable("ContainerConn"), Environment.GetEnvironmentVariable("ContainerName"), fileName);

            var memoryStream = new MemoryStream();

            await blob.DownloadToStreamAsync(memoryStream);

            return System.Text.Encoding.UTF8.GetString(memoryStream.ToArray());
        }

        public static CloudBlockBlob GetBlob(string containerConnection, string containerName, string fileName)
        {
            CloudStorageAccount storageAccount = CloudStorageAccount.Parse(containerConnection);
            CloudBlobClient blobClient = storageAccount.CreateCloudBlobClient();
            CloudBlobContainer container = blobClient.GetContainerReference(containerName);
            CloudBlockBlob blob = container.GetBlockBlobReference(fileName);
                        
            blob.Properties.ContentType = "application/json";

            return blob;
        }
    }
}
