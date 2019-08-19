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
        private static string UNKNOWN_OWNER = "unknown";


        static void Main(string[] args)
        {
            Run().Wait();
            Console.ReadLine();
        }

        public static async Task Run()
        {
            string tag_owner = GetConfigItem("tag_owner");
            string tag_deallocate = GetConfigItem("tag_deallocate");
            int tag_deallocate_days = int.Parse(GetConfigItem("tag_deallocate_days"));
            string tag_deletevm = GetConfigItem("tag_deletevm");
            int tag_deletevm_days = int.Parse(GetConfigItem("tag_deletevm_days"));
            string tag_deleterg = GetConfigItem("tag_deleterg");
            int tag_deleterg_days = int.Parse(GetConfigItem("tag_deleterg_days"));
            string subscriptionIds = GetConfigItem("subscriptionIds");
            string clientId = GetConfigItem("clientId");
            string clientSecret = GetConfigItem("clientSecret");
            string tenantId = GetConfigItem("tenantId");

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
                            ownerTag = group.Tags.Where(tag => tag.Key.Equals(tag_owner, StringComparison.InvariantCultureIgnoreCase)).FirstOrDefault();
                        }

                        String endTime = DateTime.Now.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                        String resourceId = group.Id;
                        if (ownerTag.Equals(defaultKeyValuePair))
                        {
                            //Console.WriteLine($"Resource group {group.Name} does not contain owner tag...looking in activity log");
                            String startTime = DateTime.Now.ToUniversalTime().AddHours(-25).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

                            string newOwner = UNKNOWN_OWNER;
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
                            if (!UNKNOWN_OWNER.Equals(newOwner))
                            {
                                await group.Update().WithTag(tag_owner, newOwner).ApplyAsync();
                                Console.WriteLine($"Resource group {group.Name} tagged with owner {newOwner}");
                            }
                        }
                        else if (UNKNOWN_OWNER.Equals(ownerTag.Value))
                        {
                            bool needsUpdate = false;
                            var updateGroup = group.Update();
                            if (group.Tags.Where(tag => tag.Key.Equals(tag_deallocate, StringComparison.InvariantCultureIgnoreCase)).Count() == 0)
                            {
                                needsUpdate = true;
                                updateGroup.WithTag(tag_deallocate, DateTime.Now.ToUniversalTime().AddDays(tag_deallocate_days).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
                            }
                            if (group.Tags.Where(tag => tag.Key.Equals(tag_deletevm, StringComparison.InvariantCultureIgnoreCase)).Count() == 0)
                            {
                                needsUpdate = true;
                                updateGroup.WithTag(tag_deletevm, DateTime.Now.ToUniversalTime().AddDays(tag_deletevm_days).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
                            }
                            if (group.Tags.Where(tag => tag.Key.Equals(tag_deleterg, StringComparison.InvariantCultureIgnoreCase)).Count() == 0)
                            {
                                needsUpdate = true;
                                updateGroup.WithTag(tag_deleterg, DateTime.Now.ToUniversalTime().AddDays(tag_deleterg_days).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
                            }

                            if (needsUpdate)
                            {
                                await updateGroup.ApplyAsync();
                            }
                        }
                        else
                        {
                            //Console.WriteLine($"Resource group {group.Name} is already owned by {ownerTag.Value}");
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


        private static string GetConfigItem(string key)
        {
            string configItem = ConfigurationManager.AppSettings[key];
            if (String.IsNullOrEmpty(configItem))
            {
                throw new ConfigurationErrorsException($"Please set {key} in app.config");
            }

            return configItem;
        }

        private static async Task<EventData[]> GetCreationLogs(String startTime, String endTime, String resourceId, String operation, InsightsClient client)
        {
            var logs = await GetCreationLogs(startTime, endTime, resourceId, client);
            return logs.Where(log => log.OperationName.Value.Equals(operation, StringComparison.InvariantCultureIgnoreCase)).ToArray();
        }

        private static async Task<EventData[]> GetCreationLogs(String startTime, String endTime, String resourceId, InsightsClient client)
        {
            ODataQuery<EventData> query = new ODataQuery<EventData>($"eventTimestamp ge '{startTime}' and eventTimestamp le '{endTime}' and resourceUri eq '{resourceId}'");
            var logs = await client.Events.ListAsync(query);

            return logs.ToArray();
        }
    }
}
