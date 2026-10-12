using System.Net;
using System.Text.Json;
using Aeso.CalendarSync;
namespace Aeso.CalendarSync.Tests;

public class EdgeWriteTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InsertConflictWithHiddenPastOrRemovedPrefixProtectsOldEntry(bool past)
    {
        var slot = Fixtures.Slots(Fixtures.Row()).Single();
        var old = Fixtures.Entry(slot, "original");
        old = past ? old with { Start = new DateTimeOffset(2026, 10, 11, 8, 15, 0, TimeSpan.FromHours(1)), End = new DateTimeOffset(2026, 10, 11, 9, 5, 0, TimeSpan.FromHours(1)) } : old with { Summary = "Protected" };
        var inserted = new List<string>(); var mutations = new List<HttpMethod>();
        using var http = new HttpClient(new StubHandler(async request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                var id = request.RequestUri!.Segments.Last(); return StubHandler.Json(Fixtures.Google(id == "original" ? old : Fixtures.Entry(slot, id)));
            }
            mutations.Add(request.Method); Assert.Equal(HttpMethod.Post, request.Method);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()); var newId = json.RootElement.GetProperty("id").GetString()!; inserted.Add(newId);
            return StubHandler.Json("{}", newId == "original" ? HttpStatusCode.Conflict : HttpStatusCode.OK);
        }));
        await new GoogleCalendar(http, "calendar", _ => Task.FromResult("token"), new FixedClock()).Create("original", slot, default);
        Assert.Equal(2, inserted.Count); Assert.Equal("original", inserted[0]); Assert.NotEqual(inserted[0], inserted[1]); Assert.All(mutations, x => Assert.Equal(HttpMethod.Post, x));
    }
    [Fact]
    public async Task ReadbackCannotVerifyWrongEventIdEvenIfFieldsMatch()
    {
        var slot = Fixtures.Slots(Fixtures.Row()).Single();
        using var http = new HttpClient(new StubHandler(request => Task.FromResult(StubHandler.Json(request.Method == HttpMethod.Get ? Fixtures.Google(Fixtures.Entry(slot, "other")) : "{}"))));
        await Assert.ThrowsAsync<SyncException>(() => new GoogleCalendar(http, "calendar", _ => Task.FromResult("token"), new FixedClock()).Create("expected", slot, default));
    }
    private sealed class AdvancingClock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 12, 22, 59, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class School(List<Slot> slots) : ISchoolSource
    { public Task<List<Slot>> Read(CancellationToken ct) => Task.FromResult(slots); }
    private sealed class Calendar(AdvancingClock clock) : ICalendar
    {
        public int Writes;
        public Task<List<CalendarEntry>> List(DateOnly today, CancellationToken ct) { clock.Now = clock.Now.AddMinutes(2); return Task.FromResult(new List<CalendarEntry>()); }
        public Task Create(string id, Slot slot, CancellationToken ct) { Writes++; return Task.CompletedTask; }
        public Task Replace(CalendarEntry before, Slot slot, CancellationToken ct) { Writes++; return Task.CompletedTask; }
        public Task Delete(CalendarEntry before, CancellationToken ct) { Writes++; return Task.CompletedTask; }
    }
    [Fact]
    public async Task MidnightDuringCompleteReadsStopsApplyBeforeAnyWrite()
    {
        var clock = new AdvancingClock(); var calendar = new Calendar(clock); var source = Fixtures.Slots(Fixtures.Row());
        await Assert.ThrowsAsync<SyncException>(() => new SyncRunner(new School(source), calendar, new(), clock).Run(true, default)); Assert.Equal(0, calendar.Writes);
    }
}
