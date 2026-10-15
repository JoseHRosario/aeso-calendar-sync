using System.Text.Json;
using Aeso.CalendarSync;
namespace Aeso.CalendarSync.Tests;

public class RunnerTests
{
    private sealed class School(List<Slot>? slots, bool fail = false) : ISchoolSource
    { public Task<List<Slot>> Read(CancellationToken ct) => fail ? throw new SyncException("Source failed.") : Task.FromResult(slots!); }
    private sealed class Calendar(List<CalendarEntry> entries) : ICalendar
    {
        public int Lists; public List<string> Writes = []; public bool FailCreate;
        public Task<List<CalendarEntry>> List(DateOnly today, CancellationToken ct) { Lists++; return Task.FromResult(entries); }
        public Task Create(string id, Slot slot, CancellationToken ct, int candidateLimit = EventIdentity.CandidateLimit) { Writes.Add("create"); if (FailCreate) throw new SyncException("Write failed."); entries.Add(Fixtures.Entry(slot, id)); return Task.CompletedTask; }
        public Task Replace(CalendarEntry before, Slot slot, CancellationToken ct) { Writes.Add("patch"); entries.Remove(before); entries.Add(Fixtures.Entry(slot, before.Id)); return Task.CompletedTask; }
        public Task Delete(CalendarEntry before, CancellationToken ct) { Writes.Add("delete"); entries.Remove(before); return Task.CompletedTask; }
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedOrEmptySourceNeverEvenListsGoogle(bool fail)
    {
        var calendar = new Calendar([]); var runner = new SyncRunner(new School([], fail), calendar, new(), new FixedClock());
        await Assert.ThrowsAsync<SyncException>(() => runner.Run(true, default)); Assert.Equal(0, calendar.Lists); Assert.Empty(calendar.Writes);
    }
    [Fact]
    public async Task DryRunMakesNoWritesAndApplyIsNoopOnNextRun()
    {
        var slots = Fixtures.Slots(Fixtures.Row()); var calendar = new Calendar([]); var runner = new SyncRunner(new School(slots), calendar, new(), new FixedClock());
        var preview = await runner.Run(false, default); Assert.Single(preview.Actions); Assert.Empty(calendar.Writes);
        var applied = await runner.Run(true, default); Assert.Equal(new[] { "create" }, calendar.Writes); Assert.Single(applied.Actions);
        var repeated = await runner.Run(true, default); Assert.Empty(repeated.Actions); Assert.Equal(1, repeated.Kept); Assert.Single(calendar.Writes);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(applied.Report)); var root = json.RootElement;
        Assert.Equal("success", root.GetProperty("status").GetString()); Assert.Equal(1, root.GetProperty("sourceCount").GetInt32()); Assert.Equal(1, root.GetProperty("futureCount").GetInt32()); Assert.Equal(1, root.GetProperty("created").GetInt32()); Assert.Equal(0, root.GetProperty("deleted").GetInt32()); Assert.Equal(0, root.GetProperty("kept").GetInt32());
        Assert.Equal("create", root.GetProperty("actions")[0].GetProperty("kind").GetString());
    }
    [Fact]
    public async Task PartialCreateFailureNeverDeletesOldEventAndRerunCompletes()
    {
        var slots = Fixtures.Slots(Fixtures.Row()); var calendar = new Calendar([Fixtures.Entry(slots[0]) with { Summary = "[AESO] old" }]) { FailCreate = true }; var runner = new SyncRunner(new School(slots), calendar, new(), new FixedClock());
        await Assert.ThrowsAsync<SyncException>(() => runner.Run(true, default)); Assert.Equal(new[] { "create" }, calendar.Writes);
        calendar.FailCreate = false; await runner.Run(true, default); Assert.Equal(new[] { "create", "create", "delete" }, calendar.Writes);
        Assert.Empty((await runner.Run(true, default)).Actions);
    }
    [Fact]
    public async Task ChangesAtExistingDeterministicIdUsePatch()
    {
        var slots = Fixtures.Slots(Fixtures.Row()); var id = Planner.Build(slots, [], new(2026, 10, 12), new SyncConfig().Scope).Actions[0].Id;
        var calendar = new Calendar([Fixtures.Entry(slots[0], id) with { Summary = "[AESO] edited" }]);
        var plan = await new SyncRunner(new School(slots), calendar, new(), new FixedClock()).Run(true, default);
        Assert.Equal(new[] { "patch" }, calendar.Writes); Assert.Equal(2, plan.Actions.Count);
    }
}
public class CommandTests
{
    [Theory]
    [InlineData("--source-file", "--apply")]
    [InlineData("--validate-source", "--apply")]
    public async Task OfflineApplyAlwaysRejected(string sourceOption, string mode)
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        var code = await Command.Run([sourceOption, "/does/not/exist", mode], output, error, new FixedClock());
        Assert.Equal(1, code); Assert.Equal("", output.ToString()); Assert.Contains("offline", error.ToString());
    }
    [Theory]
    [InlineData("--config", "relative.json")]
    [InlineData("--unknown", "SECRET")]
    [InlineData("--config")]
    public async Task InvalidArgumentsOnlyEmitSanitizedStderr(params string[] args)
    {
        using var output = new StringWriter(); using var error = new StringWriter(); var code = await Command.Run(args, output, error, new FixedClock());
        Assert.Equal(1, code); Assert.Empty(output.ToString()); Assert.DoesNotContain("SECRET", error.ToString()); Assert.NotEmpty(error.ToString());
    }
    [Fact]
    public async Task OfflineValidationHasCountsAndTimesButNoInventedGooglePlan()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "aeso-test-" + Guid.NewGuid())); var file = Path.Combine(dir.FullName, "source.json");
        try
        {
            await File.WriteAllTextAsync(file, "[" + Fixtures.Row() + "," + Fixtures.Row("11-10-2026", "2026-10-11T00:00:00") + "]");
            using var output = new StringWriter(); using var error = new StringWriter(); var code = await Command.Run(["--validate-source", file], output, error, new FixedClock());
            Assert.Equal(0, code); Assert.Empty(error.ToString()); using var json = JsonDocument.Parse(output.ToString());
            Assert.Equal("offline-validation", json.RootElement.GetProperty("mode").GetString()); Assert.Equal(2, json.RootElement.GetProperty("sourceCount").GetInt32()); Assert.Equal(1, json.RootElement.GetProperty("futureCount").GetInt32());
            Assert.False(json.RootElement.TryGetProperty("created", out _)); Assert.False(json.RootElement.TryGetProperty("actions", out _)); Assert.Single(json.RootElement.GetProperty("slots").EnumerateArray());
        }
        finally { dir.Delete(true); }
    }
}
