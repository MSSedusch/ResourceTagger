using System;
using System.Linq;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Logging;
using Microsoft.Azure.Management.ResourceManager.Fluent.Authentication;
using Microsoft.Azure.Management.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent;
using System.Threading.Tasks;

namespace ResourceTagger
{
    public static class ResourceTaggerFunction
    {
        [FunctionName("ResourceTaggerFunction")]
        public static async Task Run([TimerTrigger("* * * * *")] TimerInfo myTimer, ILogger log)
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
                    if (group.Tags == null || group.Tags.Where(tag => tag.Key.Equals(ownerTagName, StringComparison.InvariantCultureIgnoreCase)).Count() == 0)
                    {
                        log.LogInformation($"Resource group {group.Name} does not contain owner tag...looking in activity log");
                        //TODO: insightsClient.TenantEvents.ListWithHttpMessagesAsync();
                    }
                }
            }
        }
    }
}
