using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Aeso.CalendarSync;

public sealed class SyncConfig
{
    public string CalendarId { get; set; } = "0e970019a6c7d52ea314fcbaf561159f0ce90a80f9764b7d40fbe7182973de3a@group.calendar.google.com";
    public string GoogleCredentialsPath { get; set; } = "";
    public string SchoolOrigin { get; set; } = "https://aeso.inovarmais.com";
    public int SchoolEnrolmentId { get; set; } = 43850;
    public int SchoolTipoEnsino { get; set; } = 1;
    public int SchoolStudentId { get; set; } = 8937;
    public int SchoolYear { get; set; } = 2026;
    public int SourceInitialLimit { get; set; } = 30;
    public int SourceLimitStep { get; set; } = 15;
    public int SourceMaximumLimit { get; set; } = 3000;
    public DateOnly YearStart => new(SchoolYear, 9, 1);
    public DateOnly YearEnd => new(SchoolYear + 1, 8, 31);
    public string Scope => $"{SchoolOrigin}/{SchoolEnrolmentId}/{SchoolTipoEnsino}/{SchoolYear}";
    public void Validate(DateOnly today)
    {
        if (SchoolOrigin != "https://aeso.inovarmais.com" || SchoolEnrolmentId <= 0 || SchoolTipoEnsino <= 0 || SchoolStudentId <= 0 || SchoolYear < 2000 || SchoolYear > 9000 || string.IsNullOrWhiteSpace(CalendarId)) throw new SyncException("Invalid configuration or unsupported school origin.");
        if (today < YearStart || today > YearEnd) throw new SyncException("School year is not current; reconfigure enrolment before continuing.");
        if (SourceInitialLimit < 1 || SourceLimitStep < 1 || SourceLimitStep > 10000 || SourceMaximumLimit <= SourceInitialLimit || SourceMaximumLimit > 10000) throw new SyncException("Invalid source coverage limits.");
    }
}
public static class Transport
{
    public static async Task<HttpResponseMessage> Send(HttpClient client, Func<HttpRequestMessage> factory, string service, bool retry, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var request = factory();
                var response = await client.SendAsync(request, ct);
                var transient = response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
                if (!transient || !retry || attempt == 2) return response;
                response.Dispose();
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException && !ct.IsCancellationRequested)
            {
                if (!retry || attempt == 2) throw new SyncException(service + " transport failed.");
            }
            await Task.Delay(TimeSpan.FromMilliseconds(200 * (attempt + 1)), ct);
        }
    }
    public static async Task<JsonElement> Read(HttpResponseMessage response, string service, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode) throw new SyncException(service + " request failed (HTTP " + (int)response.StatusCode + ").");
        try { using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)); return document.RootElement.Clone(); }
        catch (JsonException) { throw new SyncException(service + " returned malformed JSON."); }
    }
}
public interface ISchoolSource { Task<List<Slot>> Read(CancellationToken ct); }
public sealed class SchoolClient(HttpClient http, SyncConfig config, string username, string password, TimeProvider clock) : ISchoolSource
{
    private string? token;
    public static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    public static string Festmani(DateTimeOffset utc) => Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes("0c24b08e-d78b-4d53-96a6-68db2bf2611f"), Encoding.UTF8.GetBytes(utc.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture))));
    private HttpRequestMessage Request(HttpMethod method, string path, object? payload = null, string? auth = null)
    {
        var request = new HttpRequestMessage(method, config.SchoolOrigin + "/consulta/api/" + path);
        request.Headers.Add("X-FESTMANI", Festmani(clock.GetUtcNow()));
        if (auth is not null || token is not null) request.Headers.TryAddWithoutValidation("Authorization", auth ?? "Bearer" + token);
        if (payload is not null) request.Content = JsonContent.Create(payload, options: new JsonSerializerOptions { PropertyNamingPolicy = null });
        return request;
    }
    private async Task<JsonElement> Call(HttpMethod method, string path, object? payload, string? auth, CancellationToken ct)
    {
        using var response = await Transport.Send(http, () => Request(method, path, payload, auth), "School", method == HttpMethod.Get, ct);
        return await Transport.Read(response, "School", ct);
    }
    public async Task<List<Slot>> Read(CancellationToken ct)
    {
        token = null;
        config.Validate(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), SourceParser.Lisbon).DateTime));
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password)) throw new SyncException("Missing school environment credentials.");
        var session = Guid.NewGuid().ToString();
        var burned = await Call(HttpMethod.Post, "login/isUserBurned", new { UserEmail = session }, null, ct);
        if (!burned.TryGetProperty("BurnedUser", out var flag) || flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new SyncException("School CAPTCHA check invalid.");
        if (flag.GetBoolean()) throw new SyncException("School requires CAPTCHA; manual sign-in required.");
        var login = await Call(HttpMethod.Post, "loginFU/", new { username = Encode(username), password = Encode(password), sessionId = Encode(session) }, "Basic " + Encode(username + ":" + password), ct);
        if (!login.TryGetProperty("IsValid", out var valid) || valid.ValueKind != JsonValueKind.True || !login.TryGetProperty("token", out var key) || key.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(key.GetString())) throw new SyncException("School login rejected.");
        token = key.GetString();
        // Public vendor studentPickerFactory selects context on the server via this GET.
        // It preserves the login token; setUser does not install a different token.
        var selected = await Call(HttpMethod.Get, "studentPicked/" + config.SchoolStudentId, null, null, ct);
        ValidateEnrolment(selected);
        JsonElement? previous = null;
        for (var limit = config.SourceInitialLimit; ; limit = Math.Min(config.SourceMaximumLimit, limit + config.SourceLimitStep))
        {
            var json = await Call(HttpMethod.Get, $"agendaProf/{config.SchoolEnrolmentId}/{config.SchoolTipoEnsino}/{limit}", null, null, ct);
            var current = SourceParser.Parse(json.GetRawText(), config.YearStart, config.YearEnd);
            if (current.Count > limit) throw new SyncException("School exceeded requested source limit.");
            if (previous is { } prior && (prior.GetArrayLength() > json.GetArrayLength() || !prior.EnumerateArray().Zip(json.EnumerateArray(), JsonElement.DeepEquals).All(x => x))) throw new SyncException("Source changed during coverage expansion.");
            if (current.Count < limit)
            {
                if (current.Count == 0) throw new SyncException("Empty source cannot authorize reconciliation.");
                var repeat = await Call(HttpMethod.Get, $"agendaProf/{config.SchoolEnrolmentId}/{config.SchoolTipoEnsino}/{limit}", null, null, ct);
                _ = SourceParser.Parse(repeat.GetRawText(), config.YearStart, config.YearEnd);
                if (!JsonElement.DeepEquals(json, repeat)) throw new SyncException("Source changed during final confirmation.");
                return current;
            }
            if (limit == config.SourceMaximumLimit) throw new SyncException("Source coverage cap reached; reconciliation blocked.");
            previous = json;
        }
    }
    private void ValidateEnrolment(JsonElement selected)
    {
        if (!selected.TryGetProperty("Matriculas", out var enrolments) || enrolments.ValueKind != JsonValueKind.Array) throw new SyncException("School selection lacks enrolment evidence.");
        var found = enrolments.EnumerateArray().Any(x => x.TryGetProperty("MatriculaId", out var id) && id.TryGetInt32(out var i) && i == config.SchoolEnrolmentId && x.TryGetProperty("TipoEnsino", out var tipo) && tipo.TryGetInt32(out var t) && t == config.SchoolTipoEnsino && x.TryGetProperty("AnoLectivo", out var year) && year.TryGetInt32(out var y) && y == config.SchoolYear);
        if (!found) throw new SyncException("Configured enrolment and school year not authorized by selected student.");
    }
}
