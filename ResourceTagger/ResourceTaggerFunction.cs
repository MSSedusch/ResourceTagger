using System;
using System.Linq;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Logging;
using Microsoft.Azure.Management.ResourceManager.Fluent.Authentication;
using Microsoft.Azure.Management.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.Azure.Insights.Models;
using Microsoft.Rest.Azure.OData;
using Microsoft.Azure.Insights;

namespace ResourceTagger
{
    public static class ResourceTaggerFunction
    {
        private static string OPERATION_RESOURCEGROUP_WRITE = "Microsoft.Resources/subscriptions/resourceGroups/write";

        [FunctionName("ResourceTaggerFunction")]
        public static async Task Run([TimerTrigger("0 0 9 * * *")] TimerInfo myTimer, ILogger log)
        {
            log.LogInformation("Running Resource Tagger...");
            string ownerTagName = Environment.GetEnvironmentVariable("OwnerTag");
            if (String.IsNullOrEmpty(ownerTagName))
            {
                log.LogCritical("Please set the OwnerTag environment variables");
                return;
            }
            string subscriptionIds = Environment.GetEnvironmentVariable("SubscriptionIds");
            if (String.IsNullOrEmpty(subscriptionIds))
            {
                log.LogCritical("Please set the SubscriptionIds environment variables");
                return;
            }

            Azure.IAuthenticated azure;
            AzureCredentials azureCreds;
            if (Environment.GetEnvironmentVariable("UseManagedIdendity") == "true")
            {
                log.LogInformation("Using Managed Identity");
                AzureCredentialsFactory factory = new AzureCredentialsFactory();
                MSILoginInformation msi = new MSILoginInformation(MSIResourceType.AppService);
                azureCreds = factory.FromMSI(msi, AzureEnvironment.AzureGlobalCloud);
            }
            else
            {
                log.LogInformation("Using Service Principal");
                string clientId = Environment.GetEnvironmentVariable("ClientId");
                string clientSecret = Environment.GetEnvironmentVariable("ClientSecret");
                string tenantId = Environment.GetEnvironmentVariable("TenantId");

                AzureCredentialsFactory factory = new AzureCredentialsFactory();
                azureCreds = factory.FromServicePrincipal(clientId, clientSecret, tenantId,
                    AzureEnvironment.AzureGlobalCloud);
            }
            azure = Azure.Configure().Authenticate(azureCreds);

            foreach (var subscriptionId in subscriptionIds.Split(",", StringSplitOptions.RemoveEmptyEntries))
            {
                log.LogInformation($"Looking for new resources without an owner tag in subscription {subscriptionId}");

                var azureSub = azure.WithSubscription(subscriptionId);
                var insightsClient = new Microsoft.Azure.Insights.InsightsClient(azureCreds);
                insightsClient.SubscriptionId = subscriptionId;

                var resourceGroups = azureSub.ResourceGroups.List();
                foreach (var group in resourceGroups)
                {
                    log.LogTrace($"Looking at resource group {group.Name}");
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
                            String startTime = DateTime.Now.ToUniversalTime().AddHours(-25).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                            String endTime = DateTime.Now.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                            String resourceId = group.Id;

                            string unknownOwner = "unknown";
                            string newOwner = unknownOwner;
                            var resourceGroupCreateLogs = await GetCreationLogs(startTime, endTime, resourceId, OPERATION_RESOURCEGROUP_WRITE, insightsClient);
                            if (resourceGroupCreateLogs.Length == 0)
                            {
                                log.LogInformation($"Resource group {group.Name}: did not find create operation - trying again");
                                startTime = DateTime.Now.ToUniversalTime().AddDays(-90).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                                resourceGroupCreateLogs = await GetCreationLogs(startTime, endTime, resourceId, OPERATION_RESOURCEGROUP_WRITE, insightsClient);
                            }
                            if (resourceGroupCreateLogs.Length != 0)
                            {
                                newOwner = resourceGroupCreateLogs[0].Caller;
                            }

                            if (!unknownOwner.Equals(newOwner))
                            {
                                await group.Update().WithTag(ownerTagName, newOwner).ApplyAsync();
                                log.LogInformation($"Resource group {group.Name} tagged with owner {newOwner}");
                            }
                            else
                            {
                                log.LogInformation($"Resource group {group.Name}: did not find create operation, please tag manually");
                            }
                        }
                        else
                        {
                            log.LogTrace($"Resource group {group.Name} is already owned by {ownerTag.Value}");
                        }
                    }
                    catch (Exception ex)
                    {
                        log.LogError("Exception: " + ex);
                    }
                }
            }
        }

        private static async Task<EventData[]> GetCreationLogs(String startTime, String endTime, String resourceId, String operation, InsightsClient client)
        {
            ODataQuery<EventData> query = new ODataQuery<EventData>($"eventTimestamp ge '{startTime}' and eventTimestamp le '{endTime}' and resourceUri eq '{resourceId}'");
            var logs = await client.Events.ListAsync(query);
            return logs.Where(log => log.OperationName.Value.Equals(operation, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        }
    }
}
