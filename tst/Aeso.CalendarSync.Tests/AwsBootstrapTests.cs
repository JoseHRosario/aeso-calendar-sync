using Amazon.Runtime;
using Amazon.Runtime.CredentialManagement;
using Aeso.CalendarSync;
namespace Aeso.CalendarSync.Tests;

public class AwsBootstrapTests
{
    [Fact]
    public void MissingDedicatedProfileFailsWithoutAmbientFallback()
    {
        var calls = 0;
        var error = Assert.Throws<SyncException>(() => AwsSsmClientFactory.Create(new(), name => { calls++; Assert.Equal("aeso-calendar-sync", name); return null; }));
        Assert.Equal(1, calls); Assert.Equal("AWS runtime profile is unavailable.", error.Message); Assert.Null(error.InnerException);
    }
    [Fact]
    public void NativeProcessProfileBuildsClientWithoutRunningCredentialProcess()
    {
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".ini");
        try
        {
            File.WriteAllText(file, "[aeso-calendar-sync]\ncredential_process = /never-executed-aeso-test\n[default]\ncredential_process = /never-executed-default-test\n");
            var missing = Assert.Throws<SyncException>(() => AwsSsmClientFactory.Create(new(), _ =>
                new CredentialProfileStoreChain(file).TryGetAWSCredentials("absent-aeso-test", out var credentials) ? credentials : null));
            Assert.Equal("AWS runtime profile is unavailable.", missing.Message);
            var chain = new CredentialProfileStoreChain(file);
            using var client = AwsSsmClientFactory.Create(new(), name =>
            {
                Assert.True(chain.TryGetAWSCredentials(name, out var credentials));
                Assert.IsType<ProcessAWSCredentials>(credentials); return credentials;
            });
            Assert.Equal("eu-west-1", client.Config.RegionEndpoint.SystemName);
        }
        finally { File.Delete(file); }
    }
    [Fact]
    public void ProfileResolverErrorsNeverExposeRawDiagnostics()
    {
        var error = Assert.Throws<SyncException>(() => AwsSsmClientFactory.Create(new(), _ => throw new AmazonClientException("PRIVATE VALUE")));
        Assert.Equal("AWS runtime profile is unavailable.", error.Message); Assert.Null(error.InnerException);
    }
}
