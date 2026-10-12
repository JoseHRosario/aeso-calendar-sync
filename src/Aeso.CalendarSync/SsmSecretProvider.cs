using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;

namespace Aeso.CalendarSync;

// Borrows the injected SDK client. Composition owns/disposes it.
public sealed class SsmSecretProvider(IAmazonSimpleSystemsManagement ssm, SyncConfig config)
{
    private Task<SecretSnapshot>? snapshot;
    private readonly object gate = new();
    public Task<SecretSnapshot> Load(CancellationToken ct)
    {
        lock (gate) return snapshot ??= Fetch(ct);
    }
    private async Task<SecretSnapshot> Fetch(CancellationToken ct)
    {
        config.ValidateSecrets();
        GetParametersResponse response;
        try
        {
            response = await ssm.GetParametersAsync(new GetParametersRequest
            {
                Names = [config.SchoolCredentialsParameter, config.GoogleServiceAccountParameter],
                WithDecryption = true
            }, ct);
        }
        catch (OperationCanceledException) { throw new OperationCanceledException("Operation cancelled.", ct); }
        catch (Exception) { throw new SyncException("SSM secret retrieval failed."); }
        var names = new HashSet<string>([config.SchoolCredentialsParameter, config.GoogleServiceAccountParameter], StringComparer.Ordinal);
        if (response is null || response.HttpStatusCode != System.Net.HttpStatusCode.OK || response.InvalidParameters is { Count: > 0 }
            || response.Parameters is null || response.Parameters.Count != 2)
            throw new SyncException("SSM secret snapshot is invalid or incomplete.");
        foreach (var parameter in response.Parameters)
        {
            if (parameter is null || parameter.Name is null || !names.Remove(parameter.Name) || parameter.Type != ParameterType.SecureString
                || parameter.ARN != $"arn:aws:ssm:{config.AwsRegion}:{config.AwsAccountId}:parameter{parameter.Name}" || string.IsNullOrWhiteSpace(parameter.Value))
                throw new SyncException("SSM secret snapshot is invalid or incomplete.");
        }
        if (names.Count != 0) throw new SyncException("SSM secret snapshot is invalid or incomplete.");
        return SecretPayloadParser.Parse(
            response.Parameters.Single(p => p.Name == config.SchoolCredentialsParameter).Value,
            response.Parameters.Single(p => p.Name == config.GoogleServiceAccountParameter).Value);
    }
}
