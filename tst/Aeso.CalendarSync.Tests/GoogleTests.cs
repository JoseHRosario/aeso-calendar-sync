using System.Net;
using System.Text.Json;
using Aeso.CalendarSync;
namespace Aeso.CalendarSync.Tests;

public class GoogleTests
{
    [Fact]
    public async Task ListsEveryPageWithLisbonMidnightAndEscapedCalendarId()
    {
        var slot = Fixtures.Slots(Fixtures.Row()).Single(); var calls = 0;
        using var http = new HttpClient(new StubHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method); Assert.Equal("Bearer fake-access", request.Headers.Authorization!.ToString());
            Assert.Contains("cal%40group.calendar.google.com", request.RequestUri!.AbsoluteUri);
            var query = Uri.UnescapeDataString(request.RequestUri.Query);
            Assert.Contains("timeMin=2026-10-12T00:00:00.0000000+01:00", query);
            if (++calls == 1) return Task.FromResult(StubHandler.Json("{\"items\":[" + Fixtures.Google(Fixtures.Entry(slot, "one")) + "],\"nextPageToken\":\"next + /\"}"));
            Assert.Contains("pageToken=next + /", query);
            return Task.FromResult(StubHandler.Json("{\"items\":[" + Fixtures.Google(Fixtures.Entry(slot, "two")) + "]}"));
        }));
        var events = await new GoogleCalendar(http, "cal@group.calendar.google.com", _ => Task.FromResult("fake-access"), new FixedClock()).List(new(2026, 10, 12), default);
        Assert.Equal(new[] { "one", "two" }, events.Select(x => x.Id)); Assert.Equal(2, calls);
    }
    [Theory]
    [InlineData("repeat-token")]
    [InlineData("duplicate-id")]
    [InlineData("missing-items")]
    [InlineData("bad-time")]
    public async Task InvalidGoogleCoverageFails(string mode)
    {
        var slot = Fixtures.Slots(Fixtures.Row()).Single(); var calls = 0;
        using var http = new HttpClient(new StubHandler(_ =>
        {
            calls++;
            var entry = Fixtures.Google(Fixtures.Entry(slot, mode == "duplicate-id" ? "same" : "id" + calls));
            if (mode == "bad-time") entry = entry.Replace("+01:00", "");
            var body = mode == "missing-items" ? "{}" : "{\"items\":[" + entry + "],\"nextPageToken\":\"next\"}";
            return Task.FromResult(StubHandler.Json(body));
        }));
        await Assert.ThrowsAsync<SyncException>(() => new GoogleCalendar(http, "calendar", _ => Task.FromResult("token"), new FixedClock()).List(new(2026, 10, 12), default));
        Assert.InRange(calls, 1, 2);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeterministicInsertOr409RequiresGetReadback(bool conflict)
    {
        var slot = Fixtures.Slots(Fixtures.Row()).Single(); var verbs = new List<HttpMethod>();
        using var http = new HttpClient(new StubHandler(async request =>
        {
            verbs.Add(request.Method);
            if (request.Method == HttpMethod.Get) return StubHandler.Json(Fixtures.Google(Fixtures.Entry(slot, "id")));
            Assert.Equal(HttpMethod.Post, request.Method);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("id", json.RootElement.GetProperty("id").GetString());
            Assert.True(json.RootElement.GetProperty("reminders").GetProperty("useDefault").GetBoolean());
            Assert.Equal("Europe/Lisbon", json.RootElement.GetProperty("start").GetProperty("timeZone").GetString());
            Assert.Contains("sendUpdates=none", request.RequestUri!.Query);
            return StubHandler.Json("{}", conflict ? HttpStatusCode.Conflict : HttpStatusCode.OK);
        }));
        await new GoogleCalendar(http, "calendar", _ => Task.FromResult("token"), new FixedClock()).Create("id", slot, default);
        Assert.Equal(new[] { HttpMethod.Post, HttpMethod.Get }, verbs);
    }
    [Fact]
    public async Task InsertReadbackMismatchFails()
    {
        var slot = Fixtures.Slots(Fixtures.Row()).Single();
        using var http = new HttpClient(new StubHandler(request => Task.FromResult(StubHandler.Json(request.Method == HttpMethod.Get ? Fixtures.Google(Fixtures.Entry(slot) with { Summary = "Edited" }) : "{}"))));
        await Assert.ThrowsAsync<SyncException>(() => new GoogleCalendar(http, "calendar", _ => Task.FromResult("token"), new FixedClock()).Create("id", slot, default));
    }
    [Fact]
    public async Task CancelledIdCollisionUsesDeterministicRetrySafeChain()
    {
        var slot = Fixtures.Slots(Fixtures.Row()).Single(); var ids = new List<string>(); string? activeId = null;
        using var http = new HttpClient(new StubHandler(async request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()); var id = json.RootElement.GetProperty("id").GetString()!; ids.Add(id);
                if (id == "original") return StubHandler.Json("{}", HttpStatusCode.Conflict);
                var existed = activeId is not null; activeId = id; return StubHandler.Json("{}", existed ? HttpStatusCode.Conflict : HttpStatusCode.OK);
            }
            var eventId = request.RequestUri!.Segments.Last();
            return eventId == "original" ? StubHandler.Json("{\"id\":\"original\",\"status\":\"cancelled\"}") : StubHandler.Json(Fixtures.Google(Fixtures.Entry(slot, activeId!)));
        }));
        var calendar = new GoogleCalendar(http, "calendar", _ => Task.FromResult("token"), new FixedClock());
        await calendar.Create("original", slot, default); await calendar.Create("original", slot, default);
        Assert.Equal(4, ids.Count); Assert.Equal("original", ids[0]); Assert.Equal(ids[0], ids[2]); Assert.Equal(ids[1], ids[3]); Assert.Matches("^[a-f0-9]{64}$", ids[1]);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteChecksRaceUsesEtagAndReadsBack(bool missing)
    {
        var slot = Fixtures.Slots(Fixtures.Row()).Single(); var before = Fixtures.Entry(slot); var deleted = missing; var verbs = new List<HttpMethod>();
        using var http = new HttpClient(new StubHandler(request =>
        {
            verbs.Add(request.Method);
            if (request.Method == HttpMethod.Delete)
            { Assert.Equal("\"etag1\"", request.Headers.GetValues("If-Match").Single()); deleted = true; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)); }
            return Task.FromResult(deleted ? StubHandler.Json("{}", HttpStatusCode.NotFound) : StubHandler.Json(Fixtures.Google(before)));
        }));
        await new GoogleCalendar(http, "calendar", _ => Task.FromResult("token"), new FixedClock()).Delete(before, default);
        Assert.Equal(missing ? new[] { HttpMethod.Get, HttpMethod.Get } : new[] { HttpMethod.Get, HttpMethod.Delete, HttpMethod.Get }, verbs);
    }
    [Fact]
    public async Task PrefixRemovedAfterPreviewBlocksDelete()
    {
        var slot = Fixtures.Slots(Fixtures.Row()).Single(); var before = Fixtures.Entry(slot); var writes = 0;
        using var http = new HttpClient(new StubHandler(request =>
        { if (request.Method != HttpMethod.Get) writes++; return Task.FromResult(StubHandler.Json(Fixtures.Google(before with { Summary = "protected" }))); }));
        await Assert.ThrowsAsync<SyncException>(() => new GoogleCalendar(http, "calendar", _ => Task.FromResult("token"), new FixedClock()).Delete(before, default)); Assert.Equal(0, writes);
    }
    [Fact]
    public async Task ManualEditRestoredByPatchWithReadbackNotTombstoneDelete()
    {
        var slot = Fixtures.Slots(Fixtures.Row()).Single(); var before = Fixtures.Entry(slot) with { Description = "Edited" }; var current = before; var verbs = new List<HttpMethod>();
        using var http = new HttpClient(new StubHandler(async request =>
        {
            verbs.Add(request.Method);
            if (request.Method == HttpMethod.Patch)
            { Assert.Equal("\"etag1\"", request.Headers.GetValues("If-Match").Single()); using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()); Assert.Equal(slot.Description, body.RootElement.GetProperty("description").GetString()); current = Fixtures.Entry(slot); return StubHandler.Json("{}"); }
            return StubHandler.Json(Fixtures.Google(current));
        }));
        await new GoogleCalendar(http, "calendar", _ => Task.FromResult("token"), new FixedClock()).Replace(before, slot, default);
        Assert.Equal(new[] { HttpMethod.Get, HttpMethod.Patch, HttpMethod.Get }, verbs);
    }
    [Fact]
    public async Task TransientWritesRetryWithoutChangingIds()
    {
        var slot = Fixtures.Slots(Fixtures.Row()).Single(); var calls = 0; var ids = new List<string>();
        using var http = new HttpClient(new StubHandler(async request =>
        {
            if (request.Method == HttpMethod.Get) return StubHandler.Json(Fixtures.Google(Fixtures.Entry(slot, "id")));
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()); ids.Add(json.RootElement.GetProperty("id").GetString()!);
            return ++calls == 1 ? StubHandler.Json("sensitive", HttpStatusCode.ServiceUnavailable) : StubHandler.Json("{}");
        }));
        await new GoogleCalendar(http, "calendar", _ => Task.FromResult("token"), new FixedClock()).Create("id", slot, default); Assert.Equal(new[] { "id", "id" }, ids);
    }
    [Fact]
    public async Task UnauthorizedGetIsNotRetried()
    {
        var calls = 0; using var http = new HttpClient(new StubHandler(_ => { calls++; return Task.FromResult(StubHandler.Json("SECRET", HttpStatusCode.Unauthorized)); }));
        var error = await Assert.ThrowsAsync<SyncException>(() => new GoogleCalendar(http, "calendar", _ => Task.FromResult("token"), new FixedClock()).List(new(2026, 10, 12), default));
        Assert.Equal(1, calls); Assert.DoesNotContain("SECRET", error.Message);
    }
}
