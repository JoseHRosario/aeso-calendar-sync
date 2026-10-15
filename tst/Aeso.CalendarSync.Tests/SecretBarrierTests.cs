using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Amazon.SimpleSystemsManagement.Model;
using Amazon.SimpleSystemsManagement;
using Aeso.CalendarSync;
namespace Aeso.CalendarSync.Tests;

public class SecretBarrierTests
{
    [Theory]
    [InlineData("denied")]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("school-payload")]
    [InlineData("google-payload")]
    public async Task FailedSecretSnapshotBlocksBothHttpClients(string fault)
    {
        var config = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            await File.WriteAllTextAsync(config, "{}");
            var response = new GetParametersResponse { HttpStatusCode = HttpStatusCode.OK, Parameters = [] };
            if (fault is "school-payload" or "google-payload")
            {
                Parameter P(string name, string value) => new() { Name = name, Value = value, Type = ParameterType.SecureString, ARN = "arn:aws:ssm:eu-west-1:497087877832:parameter" + name };
                response.Parameters = [
                    P("/aeso-calendar-sync/prod/school-credentials", fault == "school-payload" ? "PRIVATE VALUE" : "{\"Username\":\"user\",\"Password\":\"password\"}"),
                    P("/aeso-calendar-sync/prod/google-service-account", "PRIVATE VALUE")];
            }
            using var fake = new FakeSsm(response);
            if (fault == "denied") fake.Failure = new InvalidOperationException("PRIVATE VALUE");
            if (fault == "malformed") fake.Failure = new JsonException("PRIVATE VALUE");
            var httpClients = 0; var ssmClients = 0;
            HttpClient Never() { httpClients++; throw new InvalidOperationException("Must not construct downstream client"); }
            using var output = new StringWriter(); using var error = new StringWriter();
            var result = await Command.Run(["--config", config, "--apply"], output, error, new FixedClock(), _ => { ssmClients++; return fake; }, Never, Never);
            Assert.Equal(1, result); Assert.Equal(1, ssmClients); Assert.Equal(1, fake.Calls); Assert.Equal(0, httpClients);
            Assert.Empty(output.ToString()); Assert.StartsWith("SSM secret", error.ToString()); Assert.DoesNotContain("PRIVATE VALUE", error.ToString());
        }
        finally { File.Delete(config); }
    }
    [Fact]
    public async Task LiveBootstrapUsesInMemoryCredentialsBeforeSchoolAndCalendar()
    {
        var config = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            await File.WriteAllTextAsync(config, "{}");
            using var rsa = RSA.Create(2048);
            string Google = JsonSerializer.Serialize(new { type = "service_account", client_email = "test@example.iam.gserviceaccount.com", private_key = rsa.ExportPkcs8PrivateKeyPem(), token_uri = "https://oauth2.googleapis.com/token" });
            Parameter P(string name, string value) => new() { Name = name, Value = value, Type = ParameterType.SecureString, ARN = "arn:aws:ssm:eu-west-1:497087877832:parameter" + name };
            using var fake = new FakeSsm(new GetParametersResponse { HttpStatusCode = HttpStatusCode.OK, Parameters = [P("/aeso-calendar-sync/prod/school-credentials", "{\"Username\":\"synthetic\",\"Password\":\"password\"}"), P("/aeso-calendar-sync/prod/google-service-account", Google)] });
            var schoolCalls = 0;
            HttpClient SchoolHttp() => new(new StubHandler(async request =>
            {
                Assert.Equal(1, fake.Calls); schoolCalls++;
                var path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith("isUserBurned")) return StubHandler.Json("{\"BurnedUser\":false}");
                Assert.EndsWith("loginFU/", path);
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                Assert.Equal(SchoolClient.Encode("synthetic"), body.RootElement.GetProperty("username").GetString());
                Assert.Equal(SchoolClient.Encode("password"), body.RootElement.GetProperty("password").GetString());
                // Stop before OAuth; the credential factory and source production seam are exercised offline.
                return StubHandler.Json("{}", HttpStatusCode.Unauthorized);
            }));
            HttpClient GoogleHttp() => new(new StubHandler(_ => throw new InvalidOperationException("Google must not be requested")));
            using var output = new StringWriter(); using var error = new StringWriter();
            Assert.Equal(1, await Command.Run(["--config", config], output, error, new FixedClock(), _ => fake, SchoolHttp, GoogleHttp));
            Assert.Equal(2, schoolCalls); Assert.Empty(output.ToString()); Assert.Contains("School request failed (HTTP 401)", error.ToString());
        }
        finally { File.Delete(config); }
    }
    [Fact]
    public async Task OfflineSourceNeverConstructsAwsOrDownstreamClients()
    {
        var source = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            await File.WriteAllTextAsync(source, "[" + Fixtures.Row() + "]");
            using var output = new StringWriter(); using var error = new StringWriter();
            Assert.Equal(0, await Command.Run(["--validate-source", source], output, error, new FixedClock(), _ => throw new InvalidOperationException("AWS forbidden"), () => throw new InvalidOperationException(), () => throw new InvalidOperationException()));
            Assert.Empty(error.ToString()); Assert.Contains("offline-validation", output.ToString());
        }
        finally { File.Delete(source); }
    }
}
