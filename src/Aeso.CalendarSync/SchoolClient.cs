using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Aeso.CalendarSync;

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
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password)) throw new SyncException("Missing school credentials.");
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
