using Amazon.Runtime;
using Amazon.Runtime.CredentialManagement;
using Amazon.SimpleSystemsManagement;
namespace Aeso.CalendarSync;

public static class AwsSsmClientFactory
{
    public static IAmazonSimpleSystemsManagement Create(SyncConfig config, Func<string, AWSCredentials?>? resolveProfile = null)
    {
        config.ValidateSecrets();
        try
        {
            resolveProfile ??= name => new CredentialProfileStoreChain().TryGetAWSCredentials(name, out var credentials) ? credentials : null;
            var credentials = resolveProfile(config.AwsProfile);
            if (credentials is null) throw new InvalidOperationException();
            // Explicit credentials constructor: never use the ambient SDK fallback chain.
            return new AmazonSimpleSystemsManagementClient(credentials, new AmazonSimpleSystemsManagementConfig
            {
                RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(config.AwsRegion),
                Timeout = TimeSpan.FromSeconds(30), MaxErrorRetry = 2,
                LogResponse = false, LogMetrics = false, DisableLogging = true
            });
        }
        catch (Exception) { throw new SyncException("AWS runtime profile is unavailable."); }
    }
}
