using System.Text.Json;
using Aeso.CalendarSync;

namespace Aeso.CalendarSync.Tests;

public class SecretConfigurationTests
{
    [Theory]
    [InlineData("GoogleCredentialsPath")]
    [InlineData("SchoolCredentialsPath")]
    [InlineData("SchoolUsername")]
    [InlineData("SchoolPassword")]
    [InlineData("AESO_SCHOOL_USERNAME")]
    [InlineData("AESO_SCHOOL_PASSWORD")]
    public async Task LegacyCredentialFileConfigurationIsRejectedEvenOffline(string property)
    {
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new Dictionary<string, string> { [property] = "/never/read/key.json" }));
            using var output = new StringWriter(); using var error = new StringWriter();
            var code = await Command.Run(["--config", file, "--validate-source", "/never/read/source.json"], output, error, new FixedClock());
            Assert.Equal(1, code);
            Assert.Equal("Configuration or credential JSON is invalid." + Environment.NewLine, error.ToString());
            Assert.Empty(output.ToString());
        }
        finally { File.Delete(file); }
    }
}
