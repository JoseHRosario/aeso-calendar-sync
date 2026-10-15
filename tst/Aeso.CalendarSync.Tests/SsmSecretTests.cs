using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using Aeso.CalendarSync;
namespace Aeso.CalendarSync.Tests;

public class SsmSecretTests
{
    private static readonly string Key = RSA.Create(2048).ExportPkcs8PrivateKeyPem();
    private static string GoogleJson() => JsonSerializer.Serialize(new { type = "service_account", client_email = "test@example.iam.gserviceaccount.com", private_key = Key, token_uri = "https://oauth2.googleapis.com/token" });
    private static Parameter Parameter(string name, string value) => new() { Name = name, Value = value, Type = ParameterType.SecureString, ARN = "arn:aws:ssm:eu-west-1:497087877832:parameter" + name };
    private static GetParametersResponse Response() => new() { HttpStatusCode = HttpStatusCode.OK, Parameters = [Parameter("/aeso-calendar-sync/prod/school-credentials", "{\"username\":\"synthetic-user\",\"Password\":\"synthetic-password\"}"), Parameter("/aeso-calendar-sync/prod/google-service-account", GoogleJson())] };

    [Theory]
    [InlineData("invalid")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("unexpected")]
    [InlineData("type")]
    [InlineData("account")]
    [InlineData("region")]
    [InlineData("arn-path")]
    [InlineData("null-parameters")]
    [InlineData("http")]
    public async Task UntrustedOrIncompleteResponseFailsClosed(string fault)
    {
        var response = Response();
        switch (fault)
        {
            case "invalid": response.InvalidParameters = ["PRIVATE VALUE"]; break;
            case "missing": response.Parameters.RemoveAt(1); break;
            case "duplicate": response.Parameters.Add(response.Parameters[0]); break;
            case "unexpected": response.Parameters.Add(Parameter("/unexpected", "PRIVATE VALUE")); break;
            case "type": response.Parameters[0].Type = ParameterType.String; break;
            case "account": response.Parameters[0].ARN = response.Parameters[0].ARN.Replace("497087877832", "111111111111"); break;
            case "region": response.Parameters[0].ARN = response.Parameters[0].ARN.Replace("eu-west-1", "eu-west-2"); break;
            case "arn-path": response.Parameters[0].ARN += "/other"; break;
            case "null-parameters": response.Parameters = null; break;
            case "http": response.HttpStatusCode = HttpStatusCode.Forbidden; break;
        }
        using var fake = new FakeSsm(response);
        var error = await Assert.ThrowsAsync<SyncException>(() => new SsmSecretProvider(fake, new SyncConfig()).Load(default));
        Assert.Equal("SSM secret snapshot is invalid or incomplete.", error.Message);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("school", "not-json PRIVATE VALUE")]
    [InlineData("school", "null")]
    [InlineData("school", "[]")]
    [InlineData("school", "{}")]
    [InlineData("school", "{\"username\":\"user\",\"password\":\" \"}")]
    [InlineData("school", "{\"username\":\"user\",\"Username\":\"other\",\"password\":\"PRIVATE VALUE\"}")]
    [InlineData("school", "{\"username\":\"user\",\"password\":123}")]
    [InlineData("school", "{\"username\":\"user\",\"password\":\"PRIVATE VALUE\",\"path\":\"no\"}")]
    [InlineData("google", "not-json PRIVATE VALUE")]
    [InlineData("google", "null")]
    [InlineData("google", "[]")]
    [InlineData("google", "{}")]
    public async Task MalformedSecretPayloadNeverEscapes(string target, string value)
    {
        var response = Response(); response.Parameters[target == "school" ? 0 : 1].Value = value;
        using var fake = new FakeSsm(response);
        var error = await Assert.ThrowsAsync<SyncException>(() => new SsmSecretProvider(fake, new SyncConfig()).Load(default));
        Assert.Equal("SSM secret payload is invalid.", error.Message); Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("type", "authorized_user")]
    [InlineData("token_uri", "https://attacker.invalid/token")]
    [InlineData("token_uri", "https://oauth2.googleapis.com/token?other=yes")]
    [InlineData("client_email", "not-an-email")]
    [InlineData("client_email", "user@example.com")]
    [InlineData("private_key", "PRIVATE VALUE")]
    [InlineData("universe_domain", "attacker.invalid")]
    public async Task GoogleCredentialRejectsWrongTypeEndpointEmailOrKey(string property, string value)
    {
        var payload = JsonSerializer.Deserialize<Dictionary<string, object>>(GoogleJson())!; payload[property] = value;
        var response = Response(); response.Parameters[1].Value = JsonSerializer.Serialize(payload);
        using var fake = new FakeSsm(response);
        var error = await Assert.ThrowsAsync<SyncException>(() => new SsmSecretProvider(fake, new SyncConfig()).Load(default));
        Assert.Equal("SSM secret payload is invalid.", error.Message); Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("sdk")]
    [InlineData("denied")]
    [InlineData("unexpected")]
    public async Task ProviderFailuresAreSanitizedAndNotRetriedByApplication(string failure)
    {
        using var fake = new FakeSsm(Response()) { Failure = failure switch {
            "sdk" => new AmazonClientException("PRIVATE VALUE"),
            "denied" => new AmazonSimpleSystemsManagementException("PRIVATE VALUE"),
            _ => new InvalidOperationException("PRIVATE VALUE") } };
        var provider = new SsmSecretProvider(fake, new SyncConfig());
        for (var i = 0; i < 2; i++)
        {
            var error = await Assert.ThrowsAsync<SyncException>(() => provider.Load(default));
            Assert.Equal("SSM secret retrieval failed.", error.Message); Assert.Null(error.InnerException);
        }
        Assert.Equal(1, fake.Calls);
    }

    [Theory]
    [InlineData("AwsRegion", "us-east-1")]
    [InlineData("AwsAccountId", "111111111111")]
    [InlineData("AwsProfile", "minerva-agent")]
    [InlineData("AwsProfile", "")]
    [InlineData("SchoolCredentialsParameter", "/wrong")]
    [InlineData("GoogleServiceAccountParameter", "/wrong")]
    public async Task UnsupportedSecretConfigurationFailsBeforeSsm(string property, string value)
    {
        var config = new SyncConfig(); typeof(SyncConfig).GetProperty(property)!.SetValue(config, value);
        using var fake = new FakeSsm(Response());
        var error = await Assert.ThrowsAsync<SyncException>(() => new SsmSecretProvider(fake, config).Load(default));
        Assert.Equal("Invalid AWS secret configuration.", error.Message); Assert.Equal(0, fake.Calls);
    }

    [Fact]
    public async Task CompleteSnapshotDecryptsExactlyTheTwoApprovedParametersOnce()
    {
        using var fake = new FakeSsm(Response());
        var provider = new SsmSecretProvider(fake, new SyncConfig());
        var first = await provider.Load(default);
        var second = await provider.Load(default);
        Assert.Same(first, second);
        Assert.Equal("synthetic-user", first.School.Username);
        Assert.Equal("synthetic-password", first.School.Password);
        var credential = Assert.IsType<Google.Apis.Auth.OAuth2.ServiceAccountCredential>(first.Google.UnderlyingCredential);
        Assert.Equal("https://oauth2.googleapis.com/token", credential.TokenServerUrl);
        Assert.Equal(new[] { "https://www.googleapis.com/auth/calendar.events" }, credential.Scopes);
        Assert.Equal(1, fake.Calls);
        Assert.True(fake.Request!.WithDecryption);
        Assert.Equal(new[] { "/aeso-calendar-sync/prod/school-credentials", "/aeso-calendar-sync/prod/google-service-account" }, fake.Request.Names);
    }
}
