using Microsoft.Azure.Insights;
using Microsoft.Azure.Insights.Models;
using Microsoft.Azure.Management.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent.Authentication;
using Microsoft.Rest.Azure.OData;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Threading.Tasks;

namespace ResourceTaggerConsole
{
    class Program
    {
        private static string OPERATION_RESOURCEGROUP_WRITE = "Microsoft.Resources/subscriptions/resourceGroups/write";

        static void Main(string[] args)
        {
            Run().Wait();
        }
        public static async Task Run()
        {
            string ownerTagName = ConfigurationManager.AppSettings["ownerTagName"];
            if (String.IsNullOrEmpty(ownerTagName))
            {
                Console.WriteLine("Please set ownerTagName in app.config");
                return;
            }
            string subscriptionIds = ConfigurationManager.AppSettings["subscriptionIds"];
            if (String.IsNullOrEmpty(subscriptionIds))
            {
                Console.WriteLine("Please set subscriptionIds in app.config");
                return;
            }
            string clientId = ConfigurationManager.AppSettings["clientId"];
            if (String.IsNullOrEmpty(clientId))
            {
                Console.WriteLine("Please set clientId in app.config");
                return;
            }
            string clientSecret = ConfigurationManager.AppSettings["clientSecret"];
            if (String.IsNullOrEmpty(clientSecret))
            {
                Console.WriteLine("Please set clientSecret in app.config");
                return;
            }
            string tenantId = ConfigurationManager.AppSettings["tenantId"];
            if (String.IsNullOrEmpty(tenantId))
            {
                Console.WriteLine("Please set tenantId in app.config");
                return;
            }

            AzureCredentialsFactory factory = new AzureCredentialsFactory();
            AzureCredentials azureCreds = factory.FromServicePrincipal(clientId, clientSecret, tenantId,
                    AzureEnvironment.AzureGlobalCloud);
            Azure.IAuthenticated azure = Azure.Configure().Authenticate(azureCreds);

            foreach (var subscriptionId in subscriptionIds.Split(",", StringSplitOptions.RemoveEmptyEntries))
            {
                Console.WriteLine($"Looking for new resources without an owner tag in subscription {subscriptionId}");

                var azureSub = azure.WithSubscription(subscriptionId);
                var insightsClient = new InsightsClient(azureCreds);
                insightsClient.SubscriptionId = subscriptionId;

                var resourceGroups = azureSub.ResourceGroups.List();
                foreach (var group in resourceGroups)
                {
                    try
                    {
                        var defaultKeyValuePair = default(KeyValuePair<String, String>);
                        var ownerTag = defaultKeyValuePair;
                        if (group.Tags != null)
                        {
                            ownerTag = group.Tags.Where(tag => tag.Key.Equals(ownerTagName, StringComparison.InvariantCultureIgnoreCase)).FirstOrDefault();
                        }
                            
                        if (ownerTag.Equals(defaultKeyValuePair))
                        {
                            Console.WriteLine($"Resource group {group.Name} does not contain owner tag...looking in activity log");
                            String startTime = DateTime.Now.ToUniversalTime().AddHours(-2).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                            String endTime = DateTime.Now.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                            String resourceId = group.Id;

                            string newOwner = "unknown";
                            var resourceGroupCreateLogs = await GetCreationLogs(startTime, endTime, resourceId, OPERATION_RESOURCEGROUP_WRITE, insightsClient);
                            if (resourceGroupCreateLogs.Length == 0)                            
                            {
                                startTime = DateTime.Now.ToUniversalTime().AddDays(-90).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                                resourceGroupCreateLogs = await GetCreationLogs(startTime, endTime, resourceId, OPERATION_RESOURCEGROUP_WRITE, insightsClient);                                
                            }
                            if (resourceGroupCreateLogs.Length != 0)
                            {
                                newOwner = resourceGroupCreateLogs[0].Caller;
                            }
                            await group.Update().WithTag(ownerTagName, newOwner).ApplyAsync();
                            Console.WriteLine($"Resource group {group.Name} tagged with owner {newOwner}");
                        }
                        else
                        {
                            Console.WriteLine($"Resource group {group.Name} is already owned by {ownerTag.Value}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("Exception: " + ex);
                    }
                }
            }

            Console.WriteLine("Done Tagging");
        }

        private static async Task<EventData[]> GetCreationLogs(String startTime, String endTime, String resourceId, String operation, InsightsClient client)
        {
            ODataQuery<EventData> query = new ODataQuery<EventData>($"eventTimestamp ge '{startTime}' and eventTimestamp le '{endTime}' and resourceUri eq '{resourceId}'");
            var logs = await client.Events.ListAsync(query);
            return logs.Where(log => log.OperationName.Value.Equals(operation)).ToArray();
        }
    }
}
