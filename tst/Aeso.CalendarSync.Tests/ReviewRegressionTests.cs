using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aeso.CalendarSync;
namespace Aeso.CalendarSync.Tests;

public class ReviewRegressionTests
{
    [Theory]
    [InlineData("delete", "location")]
    [InlineData("patch", "location")]
    [InlineData("delete", "reminders")]
    [InlineData("patch", "reminders")]
    public async Task HiddenPropertyChangeAfterListAbortsBeforeMutation(string operation, string property)
    {
        var slot = Fixtures.Slots(Fixtures.Row()).Single();
        var json = JsonNode.Parse(Fixtures.Google(Fixtures.Entry(slot)))!;
        json["location"] = "original";
        json["reminders"] = new JsonObject { ["useDefault"] = false, ["overrides"] = new JsonArray(new JsonObject { ["method"] = "popup", ["minutes"] = 10 }) };
        var listed = json.ToJsonString();
        json["etag"] = "\"etag2\"";
        if (property == "location") json["location"] = "edited";
        else json["reminders"]!["overrides"]![0]!["minutes"] = 20;
        var writes = 0;
        using var http = new HttpClient(new StubHandler(request =>
        {
            if (request.Method != HttpMethod.Get) { writes++; return Task.FromResult(StubHandler.Json("{}", HttpStatusCode.NoContent)); }
            return Task.FromResult(StubHandler.Json(request.RequestUri!.Query.Contains("timeMin=") ? "{\"items\":[" + listed + "]}" : json.ToJsonString()));
        }));
        var calendar = new GoogleCalendar(http, "calendar", _ => Task.FromResult("token"), new FixedClock());
        var before = Assert.Single(await calendar.List(new(2026, 10, 12), default));
        var error = await Assert.ThrowsAsync<SyncException>(() => operation == "delete" ? calendar.Delete(before, default) : calendar.Replace(before, slot, default));
        Assert.Contains("changed after planning", error.Message);
        Assert.Equal(0, writes);
    }

    [Fact]
    public async Task ManagedListEntryWithoutEtagFailsCompleteRead()
    {
        var slot = Fixtures.Slots(Fixtures.Row()).Single();
        var json = JsonNode.Parse(Fixtures.Google(Fixtures.Entry(slot)))!.AsObject(); json.Remove("etag");
        using var http = new HttpClient(new StubHandler(_ => Task.FromResult(StubHandler.Json("{\"items\":[" + json.ToJsonString() + "]}"))));
        await Assert.ThrowsAsync<SyncException>(() => new GoogleCalendar(http, "calendar", _ => Task.FromResult("token"), new FixedClock()).List(new(2026, 10, 12), default));
    }

    [Fact]
    public async Task KnownProtectedChainCannotExtendInsertionBeyondPlannedCandidateBound()
    {
        var slots = Fixtures.Slots(Fixtures.Row()); var config = new SyncConfig(); var today = new DateOnly(2026, 10, 12);
        var primary = Planner.Build(slots, [], today, config.Scope).Actions.Single().Id;
        var ids = new List<string> { primary };
        for (var i = 1; i <= 100; i++) ids.Add(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ids[^1] + ":occupied"))));
        var protectedEntries = ids.Take(99).Select(id => Fixtures.Entry(slots[0], id) with { Summary = "Protected" }).ToArray();
        var posts = new List<string>();
        using var http = new HttpClient(new StubHandler(async request =>
        {
            if (request.Method == HttpMethod.Get)
                return request.RequestUri!.Query.Contains("timeMin=")
                    ? StubHandler.Json("{\"items\":[" + string.Join(',', protectedEntries.Select(e => Fixtures.Google(e))) + "]}")
                    : StubHandler.Json(JsonSerializer.Serialize(new { id = ids[99], status = "cancelled" }));
            Assert.Equal(HttpMethod.Post, request.Method);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var id = body.RootElement.GetProperty("id").GetString()!; posts.Add(id);
            return StubHandler.Json("{}", id == ids[99] ? HttpStatusCode.Conflict : HttpStatusCode.OK);
        }));
        var calendar = new GoogleCalendar(http, "calendar", _ => Task.FromResult("token"), new FixedClock());
        await Assert.ThrowsAsync<SyncException>(() => new SyncRunner(new School(slots), calendar, config, new FixedClock()).Run(true, default));
        Assert.Equal(new[] { ids[99] }, posts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnplannedActiveCollisionNeverAuthorizesBlindPatch(bool chained)
    {
        var slots = Fixtures.Slots(Fixtures.Row()); var config = new SyncConfig();
        var primary = Planner.Build(slots, [], new(2026, 10, 12), config.Scope).Actions.Single().Id;
        var posts = 0;
        using var http = new HttpClient(new StubHandler(request =>
        {
            if (request.Method == HttpMethod.Post) { posts++; return Task.FromResult(StubHandler.Json("{}", HttpStatusCode.Conflict)); }
            Assert.Equal(HttpMethod.Get, request.Method);
            if (request.RequestUri!.Query.Contains("timeMin=")) return Task.FromResult(StubHandler.Json("{\"items\":[]}"));
            var id = request.RequestUri.Segments.Last();
            return Task.FromResult(chained && id == primary
                ? StubHandler.Json(JsonSerializer.Serialize(new { id, status = "cancelled" }))
                : StubHandler.Json(Fixtures.Google(Fixtures.Entry(slots[0], id) with { Description = "unplanned edit" })));
        }));
        var calendar = new GoogleCalendar(http, "calendar", _ => Task.FromResult("token"), new FixedClock());
        var error = await Assert.ThrowsAsync<SyncException>(() => new SyncRunner(new School(slots), calendar, config, new FixedClock()).Run(true, default));
        Assert.Contains("conflict does not match", error.Message);
        Assert.Equal(chained ? 2 : 1, posts);
    }

    private sealed class School(List<Slot> slots) : ISchoolSource
    { public Task<List<Slot>> Read(CancellationToken ct) => Task.FromResult(slots); }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("past")]
    [InlineData("protected")]
    [InlineData("mixed")]
    public async Task CreatedCollisionChainManualEditIsPatchedAndNextPlanIsEmpty(string reason)
    {
        var slots = Fixtures.Slots(Fixtures.Row()); var slot = slots.Single(); var config = new SyncConfig();
        var today = new DateOnly(2026, 10, 12);
        var primary = Planner.Build(slots, [], today, config.Scope).Actions.Single().Id;
        var original = Fixtures.Entry(slot, primary);
        if (reason == "past") original = original with { Start = slot.Start.AddYears(-1), End = slot.End.AddYears(-1) };
        if (reason == "protected") original = original with { Summary = "Protected" };
        var secondary = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(primary + ":occupied")));
        var secondaryPast = Fixtures.Entry(slot, secondary) with { Start = slot.Start.AddYears(-1), End = slot.End.AddYears(-1) };
        var events = new Dictionary<string, CalendarEntry>(); var writes = new List<(HttpMethod Method, string Id)>();
        using var http = new HttpClient(new StubHandler(async request =>
        {
            var id = request.RequestUri!.Segments.Last();
            if (request.Method == HttpMethod.Get)
            {
                if (request.RequestUri.Query.Contains("timeMin="))
                {
                    var listed = events.Values.ToList();
                    if (reason == "protected") listed.Add(original);
                    return StubHandler.Json("{\"items\":[" + string.Join(',', listed.Select(e => Fixtures.Google(e))) + "]}");
                }
                if (id == primary) return reason is "cancelled" or "mixed" ? StubHandler.Json(JsonSerializer.Serialize(new { id, status = "cancelled" })) : StubHandler.Json(Fixtures.Google(original));
                if (reason == "mixed" && id == secondary) return StubHandler.Json(Fixtures.Google(secondaryPast));
                return events.TryGetValue(id, out var entry) ? StubHandler.Json(Fixtures.Google(entry)) : StubHandler.Json("{}", HttpStatusCode.NotFound);
            }
            Assert.True(request.Method == HttpMethod.Post || request.Method == HttpMethod.Patch, "Restoration must not delete the edited chain event.");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            id = body.RootElement.GetProperty("id").GetString()!; writes.Add((request.Method, id));
            if (id == primary || reason == "mixed" && id == secondary) return StubHandler.Json("{}", HttpStatusCode.Conflict);
            if (request.Method == HttpMethod.Post && events.ContainsKey(id)) return StubHandler.Json("{}", HttpStatusCode.Conflict);
            if (request.Method == HttpMethod.Patch) Assert.Equal("\"etag1\"", request.Headers.GetValues("If-Match").Single());
            events[id] = Fixtures.Entry(slot, id); return StubHandler.Json("{}");
        }));
        var calendar = new GoogleCalendar(http, "calendar", _ => Task.FromResult("token"), new FixedClock());
        var runner = new SyncRunner(new School(slots), calendar, config, new FixedClock());
        await runner.Run(true, default);
        var active = Assert.Single(events.Values);
        events[active.Id] = active with { Summary = "[AESO] edited", Description = "manual edit", Start = active.Start!.Value.AddDays(1), End = active.End!.Value.AddDays(1) };
        writes.Clear();
        var preview = await runner.Run(false, default);
        Assert.Equal(new[] { "delete", "create" }, preview.Actions.Select(x => x.Kind));
        Assert.All(preview.Actions, action => Assert.Equal(active.Id, action.Id));
        await runner.Run(true, default);
        Assert.Equal(new[] { (HttpMethod.Patch, active.Id) }, writes);
        Assert.Empty((await runner.Run(false, default)).Actions);
    }
}
