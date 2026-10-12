using System.Net;
using System.Text.Json;
using Aeso.CalendarSync;
namespace Aeso.CalendarSync.Tests;

public sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    public static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = new StringContent(body) };
}
public sealed class FixedClock : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
}
public static class Fixtures
{
    public static string Row(string date = "01-06-2027", string real = "2027-06-01T00:00:00", string time = "08:15-09:05", string title = "Teste de Matemática") => JsonSerializer.Serialize(new SchoolEntry(real, $"{date} ({time})", "Joana Brito", "Matemática", title));
    public static List<Slot> Slots(params string[] rows) => SourceParser.Parse("[" + string.Join(',', rows) + "]", new(2026, 9, 1), new(2027, 8, 31));
    public static CalendarEntry Entry(Slot slot, string id = "manual") => new(id, slot.Summary, slot.Description, slot.Start, slot.End, null, true);
    public static string Google(CalendarEntry e, string status = "confirmed") => JsonSerializer.Serialize(new { id = e.Id, summary = e.Summary, description = e.Description, status, etag = "\"etag1\"", start = new { dateTime = e.Start?.ToString("O") }, end = new { dateTime = e.End?.ToString("O") }, reminders = new { useDefault = e.DefaultReminders } });
}
public class SchoolTests
{
    [Fact]
    public async Task ExpandsAndConfirmsEntireSourceWithVendorAuthentication()
    {
        var paths = new List<string>(); string? session = null;
        using var http = new HttpClient(new StubHandler(async request =>
        {
            var path = request.RequestUri!.AbsolutePath; paths.Add(path);
            Assert.Equal("tq6lE/zMtXBeLTLA01ICRfyv4D7bXYjcnJEDt2Q0Cq8=", request.Headers.GetValues("X-FESTMANI").Single());
            if (path.EndsWith("isUserBurned"))
            {
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()); session = json.RootElement.GetProperty("UserEmail").GetString(); Assert.True(Guid.TryParse(session, out _));
                return StubHandler.Json("{\"BurnedUser\":false}");
            }
            if (path.EndsWith("loginFU/"))
            {
                Assert.Equal("Basic am9zw6k6c2VncsOqZG8=", request.Headers.GetValues("Authorization").Single());
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                Assert.Equal("am9zw6k=", json.RootElement.GetProperty("username").GetString());
                Assert.Equal("c2VncsOqZG8=", json.RootElement.GetProperty("password").GetString());
                Assert.Equal(session, System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(json.RootElement.GetProperty("sessionId").GetString()!)));
                return StubHandler.Json("{\"IsValid\":true,\"token\":\"fake-token\"}");
            }
            Assert.Equal("Bearerfake-token", request.Headers.GetValues("Authorization").Single());
            if (path.Contains("studentPicked")) return StubHandler.Json("{\"Matriculas\":[{\"MatriculaId\":43850,\"TipoEnsino\":1,\"AnoLectivo\":2026}]}");
            var count = path.EndsWith("/2") ? 2 : 3;
            return StubHandler.Json("[" + string.Join(',', Enumerable.Repeat(Fixtures.Row(), count)) + "]");
        }));
        var config = new SyncConfig { SourceInitialLimit = 2, SourceLimitStep = 2, SourceMaximumLimit = 6 };
        var rows = await new SchoolClient(http, config, "josé", "segrêdo", new FixedClock()).Read(default);
        Assert.Equal(3, rows.Count);
        Assert.Equal(new[] { "/consulta/api/login/isUserBurned", "/consulta/api/loginFU/", "/consulta/api/studentPicked/8937", "/consulta/api/agendaProf/43850/1/2", "/consulta/api/agendaProf/43850/1/4", "/consulta/api/agendaProf/43850/1/4" }, paths);
    }
    [Theory]
    [InlineData("empty")]
    [InlineData("cap")]
    [InlineData("changed")]
    [InlineData("repeat")]
    [InlineData("malformed")]
    [InlineData("year")]
    [InlineData("real-date")]
    [InlineData("over-limit")]
    [InlineData("shrink")]
    public async Task UnsafeSourceOrScopeFails(string mode)
    {
        var read = 0;
        using var http = new HttpClient(new StubHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = path.EndsWith("isUserBurned") ? "{\"BurnedUser\":false}" : path.EndsWith("loginFU/") ? "{\"IsValid\":true,\"token\":\"fake\"}" : path.Contains("studentPicked") ? "{\"Matriculas\":[{\"MatriculaId\":43850,\"TipoEnsino\":1,\"AnoLectivo\":" + (mode == "year" ? "2027" : "2026") + "}]}" : Source();
            return Task.FromResult(StubHandler.Json(body));
            string Source()
            {
                read++;
                if (mode == "empty") return "[]";
                if (mode == "malformed") return "{}";
                if (mode == "over-limit") return "[" + string.Join(',', Enumerable.Repeat(Fixtures.Row(), 5)) + "]";
                if (mode == "shrink" && read > 1) return "[" + Fixtures.Row() + "]";
                var count = mode == "cap" ? int.Parse(path.Split('/').Last()) : read == 1 ? 2 : 3;
                var title = mode == "changed" && read > 1 || mode == "repeat" && read > 2 ? "changed" : "original";
                var real = mode == "real-date" && read > 2 ? "2027-06-01T12:00:00" : "2027-06-01T00:00:00";
                return "[" + string.Join(',', Enumerable.Repeat(Fixtures.Row(title: title, real: real), count)) + "]";
            }
        }));
        await Assert.ThrowsAsync<SyncException>(() => new SchoolClient(http, new SyncConfig { SourceInitialLimit = 2, SourceLimitStep = 2, SourceMaximumLimit = 4 }, "u", "p", new FixedClock()).Read(default));
    }
    [Theory]
    [InlineData(true, 200)]
    [InlineData(false, 401)]
    public async Task CaptchaAndUnauthorizedNeverRetryLogin(bool captcha, int code)
    {
        var calls = 0;
        using var http = new HttpClient(new StubHandler(request =>
        {
            calls++;
            return Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("isUserBurned") ? StubHandler.Json("{\"BurnedUser\":" + (captcha ? "true" : "false") + "}") : StubHandler.Json("SECRET response", (HttpStatusCode)code));
        }));
        var error = await Assert.ThrowsAsync<SyncException>(() => new SchoolClient(http, new(), "u", "p", new FixedClock()).Read(default));
        Assert.DoesNotContain("SECRET", error.Message); Assert.Equal(captcha ? 1 : 2, calls);
    }
}
