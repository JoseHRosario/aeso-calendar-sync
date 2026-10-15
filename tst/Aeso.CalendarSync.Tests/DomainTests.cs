using System.Text.Json;
using Aeso.CalendarSync;
namespace Aeso.CalendarSync.Tests;

public class DomainTests
{
    private static readonly DateOnly Today = new(2026, 10, 12);
    [Theory]
    [InlineData("15-01-2027", "2027-01-15T00:00:00", "08:15-09:05", "2027-01-15T08:15:00+00:00")]
    [InlineData("01-06-2027", "2027-06-01T00:00:00", "08:15-09:05", "2027-06-01T08:15:00+01:00")]
    public void LisbonOffsetsFollowDisplayedDates(string date, string real, string time, string expected)
    { Assert.Equal(expected, Fixtures.Slots(Fixtures.Row(date, real, time)).Single().Start.ToString("yyyy-MM-ddTHH:mm:sszzz")); }
    [Theory]
    [InlineData("01-06-2027", "2027-06-02T00:00:00", "08:15-09:05")]
    [InlineData("31-02-2027", "2027-02-28T00:00:00", "08:15-09:05")]
    [InlineData("01-06-2027", "2027-06-01T00:00:00", "09:05-08:15")]
    [InlineData("01-06-2027", "2027-06-01T00:00:00", "08:15-08:15")]
    [InlineData("01-06-2027", "2027-06-01T00:00:00", "24:15-25:05")]
    [InlineData("25-10-2026", "2026-10-25T00:00:00", "01:15-02:15")]
    [InlineData("28-03-2027", "2027-03-28T00:00:00", "01:15-02:15")]
    [InlineData("01-09-2027", "2027-09-01T00:00:00", "08:15-09:05")]
    public void InvalidOrAmbiguousSourceBlocksAll(string date, string real, string time)
    { Assert.Throws<SyncException>(() => Fixtures.Slots(Fixtures.Row(date, real, time))); }
    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[null]")]
    [InlineData("[{\"PrettyDate\":\"foo\"}]")]
    public void MalformedListFails(string json) => Assert.Throws<SyncException>(() => SourceParser.Parse(json, new(2026, 9, 1), new(2027, 8, 31)));
    [Fact] public void EmptyNeverAuthorizesReconciliation() => Assert.Throws<SyncException>(() => Planner.Build([], [], Today, "scope"));
    [Fact]
    public void TitlesAndDescriptionsPreserveSourceVerbatim()
    {
        var slot = Fixtures.Slots(Fixtures.Row(title: "Visita\nà escola ")).Single();
        Assert.Equal("[AESO] Visita\nà escola ", slot.Summary);
        Assert.Equal("Disciplina: Matemática\nProfessor: Joana Brito\nVisita\nà escola ", slot.Description);
    }
    [Fact]
    public void ExactManualPrefixedEventIsKeptWithoutOwnershipMarker()
    {
        var slots = Fixtures.Slots(Fixtures.Row()); var plan = Planner.Build(slots, [Fixtures.Entry(slots[0])], Today, "scope");
        Assert.Equal(1, plan.Kept); Assert.Empty(plan.Actions);
    }
    [Fact]
    public void PrefixOnlyDeletionHonorsPastStartBoundaryAndAllDay()
    {
        var slots = Fixtures.Slots(Fixtures.Row()); var entry = Fixtures.Entry(slots[0]);
        var beforeToday = new DateTimeOffset(2026, 10, 11, 23, 50, 0, TimeSpan.FromHours(1));
        var todayEarly = new DateTimeOffset(2026, 10, 12, 0, 5, 0, TimeSpan.FromHours(1));
        var entries = new[] { entry with { Id = "manual", Summary = "[AESO] manual" }, entry with { Id = "unrelated", Summary = "Family" }, entry with { Id = "past", Summary = "[AESO] history", Start = beforeToday, End = todayEarly }, entry with { Id = "today", Summary = "[AESO] today", Start = todayEarly }, entry with { Id = "allDay", Summary = "[AESO] whole day", Start = null, End = null, AllDayStart = Today, AllDayEnd = Today.AddDays(1) }, entry with { Id = "allDayPast", Summary = "[AESO] history", Start = null, End = null, AllDayStart = Today.AddDays(-1) } };
        var plan = Planner.Build(slots, entries, Today, "scope");
        Assert.Equal(new[] { "allDay", "manual", "today" }, plan.Actions.Where(x => x.Kind == "delete").Select(x => x.Id).Order().ToArray());
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(plan.Report));
        Assert.Equal("2026-10-13", json.RootElement.GetProperty("actions").EnumerateArray().Single(x => x.GetProperty("summary").GetString() == "[AESO] whole day").GetProperty("end").GetString());
    }
    [Fact]
    public void TodayAndFutureOnlyWithSeparateDuplicateSlotsAndReorderStableIds()
    {
        var rows = new[] { Fixtures.Row("11-10-2026", "2026-10-11T00:00:00"), Fixtures.Row("12-10-2026", "2026-10-12T00:00:00"), Fixtures.Row(), Fixtures.Row(), Fixtures.Row(time: "09:15-10:05") };
        var plan = Planner.Build(Fixtures.Slots(rows), [], Today, "scope");
        Assert.Equal(5, plan.SourceCount); Assert.Equal(4, plan.FutureCount); Assert.Equal(4, plan.Actions.Select(x => x.Id).Distinct().Count());
        Assert.All(plan.Actions, x => Assert.Matches("^[a-f0-9]{64}$", x.Id));
        var reordered = Planner.Build(Fixtures.Slots(rows.Reverse().ToArray()), [], Today, "scope");
        Assert.Equal(plan.Actions.Select(x => x.Id), reordered.Actions.Select(x => x.Id));
        var existing = plan.Actions.Select(x => Fixtures.Entry(x.Slot!, x.Id)).ToArray();
        Assert.Empty(Planner.Build(Fixtures.Slots(rows), existing, Today, "scope").Actions);
    }
    [Theory]
    [InlineData("title")]
    [InlineData("date")]
    [InlineData("description")]
    [InlineData("reminders")]
    public void AuthoritativeSourceRestoresChangedGoogleFields(string field)
    {
        var slots = Fixtures.Slots(Fixtures.Row()); var create = Planner.Build(slots, [], Today, "scope").Actions.Single();
        var entry = Fixtures.Entry(slots[0], create.Id);
        var changed = field switch { "title" => entry with { Summary = "[AESO] Edited" }, "date" => entry with { Start = entry.Start!.Value.AddDays(1) }, "description" => entry with { Description = "Edited" }, _ => entry with { DefaultReminders = false } };
        var plan = Planner.Build(slots, [changed], Today, "scope");
        Assert.Equal(new[] { "delete", "create" }, plan.Actions.Select(x => x.Kind));
        Assert.All(plan.Actions, x => Assert.Equal(create.Id, x.Id));
    }
    [Fact]
    public void PrefixRemovalProtectsEntryAndRecreatesWithSafeId()
    {
        var slots = Fixtures.Slots(Fixtures.Row()); var create = Planner.Build(slots, [], Today, "scope").Actions.Single();
        var protectedEntry = Fixtures.Entry(slots[0], create.Id) with { Summary = "Protected" };
        var plan = Planner.Build(slots, [protectedEntry], Today, "scope");
        Assert.Single(plan.Actions); Assert.Equal("create", plan.Actions[0].Kind); Assert.NotEqual(create.Id, plan.Actions[0].Id);
        Assert.Empty(Planner.Build(slots, [protectedEntry, Fixtures.Entry(slots[0], plan.Actions[0].Id)], Today, "scope").Actions);
    }
    [Fact]
    public void ReducedDuplicateMultiplicityDeletesOnlyExtraCopy()
    {
        var slots = Fixtures.Slots(Fixtures.Row(), Fixtures.Row());
        var entries = Planner.Build(slots, [], Today, "scope").Actions.Select(x => Fixtures.Entry(x.Slot!, x.Id)).ToArray();
        var plan = Planner.Build(slots.Take(1).ToList(), entries, Today, "scope");
        Assert.Equal(1, plan.Kept); Assert.Single(plan.Actions); Assert.Equal("delete", plan.Actions[0].Kind);
    }
    [Fact]
    public void ConfigRefusesRolloverAndOriginChange()
    {
        Assert.Throws<SyncException>(() => new SyncConfig().Validate(new(2027, 9, 1)));
        Assert.Throws<SyncException>(() => new SyncConfig { SchoolOrigin = "https://attacker.invalid" }.Validate(Today));
    }
}
