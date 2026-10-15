using System.Net;
using System.Text.Json;
using Aeso.CalendarSync;
namespace Aeso.CalendarSync.Tests;

public class RetryAndBoundaryTests
{
    [Fact]
    public async Task GooglePaginationCapFailsRatherThanReturningPartialSuccess()
    {
        var calls = 0;
        using var http = new HttpClient(new StubHandler(_ => Task.FromResult(StubHandler.Json("{\"items\":[],\"nextPageToken\":\"page" + (++calls) + "\"}"))));
        await Assert.ThrowsAsync<SyncException>(() => new GoogleCalendar(http, "calendar", _ => Task.FromResult("token"), new FixedClock()).List(new(2026, 10, 12), default));
        Assert.Equal(1000, calls);
    }
    [Fact]
    public async Task TransientGetRetriesOnlyThreeTimesAndDoesNotExposeBody()
    {
        var calls = 0; using var http = new HttpClient(new StubHandler(_ => { calls++; return Task.FromResult(StubHandler.Json("PRIVATE BODY", HttpStatusCode.ServiceUnavailable)); }));
        var error = await Assert.ThrowsAsync<SyncException>(() => new GoogleCalendar(http, "calendar", _ => Task.FromResult("token"), new FixedClock()).List(new(2026, 10, 12), default));
        Assert.Equal(3, calls); Assert.DoesNotContain("PRIVATE BODY", error.Message);
    }
    [Fact]
    public async Task RetriedPatchWithStaleEtagSucceedsOnlyIfReadbackProvesFirstWriteSucceeded()
    {
        var slot = Fixtures.Slots(Fixtures.Row()).Single(); var before = Fixtures.Entry(slot) with { Summary = "[AESO] Edited" }; var current = before; var patches = 0; var reads = 0;
        using var http = new HttpClient(new StubHandler(request =>
        {
            if (request.Method == HttpMethod.Get) { reads++; return Task.FromResult(StubHandler.Json(Fixtures.Google(current))); }
            Assert.Equal(HttpMethod.Patch, request.Method); Assert.Equal("\"etag1\"", request.Headers.GetValues("If-Match").Single());
            current = Fixtures.Entry(slot); return Task.FromResult(StubHandler.Json("{}", ++patches == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.PreconditionFailed));
        }));
        await new GoogleCalendar(http, "calendar", _ => Task.FromResult("token"), new FixedClock()).Replace(before, slot, default);
        Assert.Equal(2, patches); Assert.Equal(2, reads);
    }
    [Fact]
    public async Task DeletePreconditionConflictCannotClaimSuccessIfEventStillExists()
    {
        var slot = Fixtures.Slots(Fixtures.Row()).Single(); var before = Fixtures.Entry(slot);
        using var http = new HttpClient(new StubHandler(request => Task.FromResult(request.Method == HttpMethod.Get ? StubHandler.Json(Fixtures.Google(before)) : StubHandler.Json("{}", HttpStatusCode.PreconditionFailed))));
        await Assert.ThrowsAsync<SyncException>(() => new GoogleCalendar(http, "calendar", _ => Task.FromResult("token"), new FixedClock()).Delete(before, default));
    }
    [Fact]
    public async Task AllDayGetAndDeleteReadbackKeepExclusiveEndInPlan()
    {
        using var document = JsonDocument.Parse("{\"id\":\"day\",\"summary\":\"[AESO] Manual\",\"start\":{\"date\":\"2026-10-12\"},\"end\":{\"date\":\"2026-10-13\"},\"etag\":\"etag-day\",\"reminders\":{\"useDefault\":true}}");
        var before = GoogleCalendar.Parse(document.RootElement); Assert.Equal(new DateOnly(2026, 10, 13), before.AllDayEnd); var deleted = false; var gets = 0;
        using var http = new HttpClient(new StubHandler(request =>
        {
            if (request.Method == HttpMethod.Delete) { Assert.Equal("etag-day", request.Headers.GetValues("If-Match").Single()); deleted = true; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)); }
            gets++; return Task.FromResult(deleted ? StubHandler.Json("{\"id\":\"day\",\"status\":\"cancelled\"}") : StubHandler.Json(document.RootElement.GetRawText()));
        }));
        await new GoogleCalendar(http, "calendar", _ => Task.FromResult("token"), new FixedClock()).Delete(before, default); Assert.True(deleted); Assert.Equal(2, gets);
    }
    [Fact]
    public void ManuallyMovedToPastDeterministicEventIsNeverPatchedOrDeleted()
    {
        var slots = Fixtures.Slots(Fixtures.Row()); var today = new DateOnly(2026, 10, 12); var id = Planner.Build(slots, [], today, "scope").Actions[0].Id;
        var past = Fixtures.Entry(slots[0], id) with { Start = new DateTimeOffset(2026, 10, 11, 8, 15, 0, TimeSpan.FromHours(1)), End = new DateTimeOffset(2026, 10, 11, 9, 5, 0, TimeSpan.FromHours(1)) };
        var plan = Planner.Build(slots, [past], today, "scope"); Assert.Single(plan.Actions); Assert.Equal("create", plan.Actions[0].Kind); Assert.NotEqual(id, plan.Actions[0].Id);
    }
}
