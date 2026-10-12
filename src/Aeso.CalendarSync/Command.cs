using System.Text.Json;
using Amazon.SimpleSystemsManagement;
namespace Aeso.CalendarSync;

public static class Command
{
    public static async Task<int> Run(string[] args, TextWriter output, TextWriter error, TimeProvider? clock = null,
        Func<SyncConfig, IAmazonSimpleSystemsManagement>? ssmFactory = null, Func<HttpClient>? schoolHttpFactory = null, Func<HttpClient>? googleHttpFactory = null)
    {
        clock ??= TimeProvider.System;
        try
        {
            string? configPath = null, sourcePath = null; var apply = false; var dry = false; var validate = false;
            for (var i = 0; i < args.Length; i++)
            {
                string Value() { if (++i == args.Length) throw new SyncException("Missing option value."); return args[i]; }
                switch (args[i])
                {
                    case "--config": if (configPath is not null) throw new SyncException("Duplicate config option."); configPath = Value(); break;
                    case "--apply": apply = true; break;
                    case "--dry-run": dry = true; break;
                    case "--source-file": if (sourcePath is not null) throw new SyncException("Duplicate source option."); sourcePath = Value(); break;
                    case "--validate-source": if (sourcePath is not null) throw new SyncException("Duplicate source option."); validate = true; sourcePath = Value(); break;
                    default: throw new SyncException("Unknown option. Use --config PATH [--dry-run|--apply] or --validate-source PATH.");
                }
            }
            if (apply && (sourcePath is not null || dry)) throw new SyncException("Apply cannot use an offline source or dry-run.");
            if (configPath is not null && !Path.IsPathFullyQualified(configPath)) throw new SyncException("Config path must be absolute.");
            if (configPath is null && sourcePath is null) throw new SyncException("Live operation requires --config absolute PATH.");
            var config = configPath is null ? new SyncConfig() : JsonSerializer.Deserialize<SyncConfig>(await File.ReadAllTextAsync(configPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow }) ?? throw new SyncException("Invalid configuration.");
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), SourceParser.Lisbon).DateTime);
            config.Validate(today);
            if (sourcePath is not null)
            {
                var source = SourceParser.Parse(await File.ReadAllTextAsync(sourcePath), config.YearStart, config.YearEnd);
                if (source.Count == 0) throw new SyncException("Empty offline source is not valid completeness evidence.");
                await output.WriteLineAsync(JsonSerializer.Serialize(new { status = "success", mode = validate ? "offline-validation" : "offline-source-preview", sourceCount = source.Count, futureCount = source.Count(x => DateOnly.FromDateTime(x.Start.DateTime) >= today), slots = source.Where(x => DateOnly.FromDateTime(x.Start.DateTime) >= today).Select(x => new { summary = x.Summary, description = x.Description, start = x.Start.ToString("O"), end = x.End.ToString("O") }) }));
                return 0;
            }
            config.ValidateSecrets();
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            using var ssm = (ssmFactory ?? (settings => AwsSsmClientFactory.Create(settings)))(config);
            var secrets = await new SsmSecretProvider(ssm, config).Load(deadline.Token);
            // Both decrypted payloads and the in-memory Google credential are validated
            // before constructing clients or making school/calendar requests.
            static HttpClient Http() => new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
            using var schoolHttp = (schoolHttpFactory ?? Http)();
            using var googleHttp = (googleHttpFactory ?? Http)();
            var school = new SchoolClient(schoolHttp, config, secrets.School.Username, secrets.School.Password, clock);
            var google = new GoogleCalendar(googleHttp, config.CalendarId, ct => secrets.Google.UnderlyingCredential.GetAccessTokenForRequestAsync(null, ct), clock);
            var plan = await new SyncRunner(school, google, config, clock).Run(apply, deadline.Token);
            await output.WriteLineAsync(JsonSerializer.Serialize(plan.Report));
            return 0;
        }
        catch (SyncException ex) { await error.WriteLineAsync(ex.Message); return 1; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { await error.WriteLineAsync("Local configuration or credential file could not be read."); return 1; }
        catch (JsonException) { await error.WriteLineAsync("Configuration or credential JSON is invalid."); return 1; }
        catch (OperationCanceledException) { await error.WriteLineAsync("Operation timed out or was cancelled."); return 1; }
        catch (Exception) { await error.WriteLineAsync("Synchronization failed; no sensitive diagnostics emitted."); return 1; }
    }
}
