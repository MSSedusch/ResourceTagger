using Microsoft.Azure.Management.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent.Authentication;
using Microsoft.IdentityModel.Clients.ActiveDirectory;
using Microsoft.Rest.Azure.Authentication;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace CostPerOwner
{
    class Program
    {
        public static string OWNER_UNKNOWN = "unknown";

        static void Main(string[] args)
        {
            // Console.WriteLine("Hello World!");
            Run().Wait();
            //Console.WriteLine("Done");
        }

        private static async Task Run()
        {
            string subscriptionIds = ConfigurationManager.AppSettings["subscriptionIds"];
            string ownerTagName = ConfigurationManager.AppSettings["ownerTagName"];
            string startDate = DateTime.Now.ToUniversalTime().AddDays(-30).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            string endDate = DateTime.Now.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");


            string clientId = ConfigurationManager.AppSettings["clientId"];
            string clientSecret = ConfigurationManager.AppSettings["clientSecret"];
            string tenantId = ConfigurationManager.AppSettings["tenantId"];

            AzureCredentialsFactory factory = new AzureCredentialsFactory();
            AzureCredentials azureCreds = factory.FromServicePrincipal(clientId, clientSecret, tenantId,
                    AzureEnvironment.AzureGlobalCloud);
            Azure.IAuthenticated azure = Azure.Configure().Authenticate(azureCreds);

            string body = @"
{
    ""type"": ""Usage"",
    ""timeframe"": ""Custom"",
    ""timePeriod"": {
        ""from"": """ + startDate + @""",
        ""to"": """ + endDate + @""",
    },
    ""dataset"": {
        ""granularity"": ""Daily"",
        ""aggregation"": {
            ""totalCost"": {
                ""name"": ""PreTaxCost"",
                ""function"": ""Sum""
            }
        },
        ""grouping"": [
            {
            ""type"": ""Dimension"",
            ""name"": ""ResourceGroup""
            }
        ]
    }
}
";
            string currency = String.Empty;
            Dictionary<string, Double> costPerUser = new Dictionary<string, Double>();
            Dictionary<string, Double> costPerGroup = new Dictionary<string, Double>();
            Dictionary<string, string> groupOwner = new Dictionary<string, string>();
            string token = await GetOAuthTokenFromAAD();

            foreach (var subscriptionId in subscriptionIds.Split(",", StringSplitOptions.RemoveEmptyEntries))
            {
                var azureSub = azure.WithSubscription(subscriptionId);
                var resourceGroups = azureSub.ResourceGroups.List();

                string uri = $"https://management.azure.com/subscriptions/{subscriptionId}/providers/Microsoft.CostManagement/query?api-version=2019-01-01";

                while (!String.IsNullOrEmpty(uri))
                {
                    QueryResult result = null;
                    int costIndex = -1;
                    int currencyIndex = -1;
                    int groupIndex = -1;

                    var request = HttpWebRequest.CreateHttp(uri);
                    request.ContentType = "application/json";
                    request.Method = "POST";
                    request.Headers.Add("Authorization", $"Bearer {token}");
                    try
                    {
                        using (StreamWriter writer = new StreamWriter(request.GetRequestStream()))
                        {
                            writer.Write(body);
                            writer.Flush();
                        }

                        var response = await request.GetResponseAsync();
                        var responseString = String.Empty;
                        using (StreamReader reader = new StreamReader(response.GetResponseStream()))
                        {
                            responseString = reader.ReadToEnd();
                        }
                        result = JsonConvert.DeserializeObject<QueryResult>(responseString);
                        uri = result.properties.nextLink;
                        costIndex = GetColumnIndex(result.properties.columns, "PreTaxCost");
                        currencyIndex = GetColumnIndex(result.properties.columns, "Currency");
                        groupIndex = GetColumnIndex(result.properties.columns, "ResourceGroup");
                        if (costIndex < 0)
                        {
                            Console.WriteLine($"Could not find cost index for subscription {subscriptionId}");
                            continue;
                        }
                    }
                    catch (WebException wex)
                    {
                        string errorMessage = string.Empty;
                        if (wex.Response != null)
                        {
                            using (StreamReader reader = new StreamReader(wex.Response.GetResponseStream()))
                            {
                                errorMessage = reader.ReadToEnd();
                            }
                        }
                        Console.WriteLine($"Error while calculating costs for subscription {subscriptionId}: {wex} ({errorMessage})");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error while calculating costs for subscription {subscriptionId}: {ex}");
                    }

                    if (result != null)
                    {
                        foreach (var group in resourceGroups)
                        {
                            var resourceGroupOwner = OWNER_UNKNOWN;
                            var defaultKeyValuePair = default(KeyValuePair<String, String>);
                            var ownerTag = defaultKeyValuePair;
                            if (group.Tags != null)
                            {
                                ownerTag = group.Tags.Where(tag => tag.Key.Equals(ownerTagName, StringComparison.InvariantCultureIgnoreCase)).FirstOrDefault();
                            }

                            if (!ownerTag.Equals(defaultKeyValuePair))
                            {
                                resourceGroupOwner = ownerTag.Value;
                            }

                            //Console.WriteLine($"Calculating costs for resource group {group.Name} in subscription {subscriptionId} which belongs to {resourceGroupOwner}");

                            string keyNameGroup = $"{subscriptionId}/{group.Name}";
                            if (!costPerUser.ContainsKey(resourceGroupOwner))
                            {
                                costPerUser.Add(resourceGroupOwner, 0);
                            }
                            if (!costPerGroup.ContainsKey(keyNameGroup))
                            {
                                costPerGroup.Add(keyNameGroup, 0);
                            }
                            if (!groupOwner.ContainsKey(keyNameGroup))
                            {
                                groupOwner.Add(keyNameGroup, resourceGroupOwner);
                            }

                            var groupRows = result.properties.rows.Where(rTemp => rTemp[groupIndex].ToString().Equals(group.Name, 
                                StringComparison.InvariantCultureIgnoreCase)).ToArray();
                            foreach (var row in groupRows)
                            {
                                costPerUser[resourceGroupOwner] += (Double)row[costIndex];
                                costPerGroup[keyNameGroup] += (Double)row[costIndex];

                                var currencyOfRow = (string)row[currencyIndex];
                                if (String.IsNullOrEmpty(currency))
                                {
                                    currency = currencyOfRow;
                                }
                                else if (!currency.Equals(currencyOfRow))
                                {
                                    throw new Exception("There are different currencies");
                                }
                            }
                        }
                    }
                }
                
                Console.WriteLine($"##########################################");
                Console.WriteLine($"Cost between {startDate} and {endDate} per resource group for unknown owner");
                Console.WriteLine($"##########################################");
                var subscriptionRgUnknown = costPerGroup.Where(temp => temp.Key.Split("/")[0].
                    Equals(subscriptionId, StringComparison.InvariantCultureIgnoreCase));
                foreach (KeyValuePair<string, double> costEntry in subscriptionRgUnknown.OrderByDescending(temp => temp.Value))
                {
                    if (groupOwner[costEntry.Key].Equals(OWNER_UNKNOWN, StringComparison.InvariantCultureIgnoreCase))
                    {
                        Console.WriteLine($"{costEntry.Key}: {currency} {costEntry.Value}");
                    }
                }

            }

            Console.WriteLine($"##########################################");
            Console.WriteLine($"Cost between {startDate} and {endDate} per user");
            Console.WriteLine($"##########################################");
            foreach (KeyValuePair<string, double> costEntry in costPerUser.OrderByDescending(temp => temp.Value))
            {
                Console.WriteLine($"{costEntry.Key}: {currency} {costEntry.Value}");
            }
            Console.WriteLine($"##########################################");
            Console.WriteLine($"Cost between {startDate} and {endDate} per resource group");
            Console.WriteLine($"##########################################");
            foreach (KeyValuePair<string, double> costEntry in costPerGroup.OrderByDescending(temp => temp.Value))
            {
                Console.WriteLine($"{costEntry.Key}: {currency} {costEntry.Value} (owner: {groupOwner[costEntry.Key]})");
            }
        }

        private static int GetColumnIndex(List<QueryResultColumn> columns, string columnName)
        {
            int index = -1;
            for (var columnIndex = 0; columnIndex < columns.Count; columnIndex++)
            {
                if (columns[columnIndex].name.Equals(columnName))
                {
                    index = columnIndex;
                    break;
                }
            }

            return index;
        }

        public static async Task<string> GetOAuthTokenFromAAD()
        {
            var tenantId = ConfigurationManager.AppSettings["tenantId"];
            var clientId = ConfigurationManager.AppSettings["clientId"];
            var clientSecret = ConfigurationManager.AppSettings["clientSecret"];
            var adSettings = new ActiveDirectoryServiceSettings
            {
                AuthenticationEndpoint = new Uri(AzureEnvironment.AzureGlobalCloud.AuthenticationEndpoint),
                TokenAudience = new Uri(AzureEnvironment.AzureGlobalCloud.ManagementEndpoint),
                ValidateAuthority = true
            };

            await ApplicationTokenProvider.LoginSilentAsync(
                            tenantId,
                            clientId,
                            clientSecret,
                            adSettings,
                            TokenCache.DefaultShared);

            var token = TokenCache.DefaultShared.ReadItems()
                .Where(t => t.ClientId == clientId)
                .OrderByDescending(t => t.ExpiresOn)
                .First();

            return token.AccessToken;
        }
    }
}
