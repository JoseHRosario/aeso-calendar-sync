using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Aeso.CalendarSync;

public interface ICalendar
{
    Task<List<CalendarEntry>> List(DateOnly today, CancellationToken ct);
    Task Create(string id, Slot slot, CancellationToken ct);
    Task Replace(CalendarEntry before, Slot slot, CancellationToken ct);
    Task Delete(CalendarEntry before, CancellationToken ct);
}
public sealed class GoogleCalendar(HttpClient http, string calendarId, Func<CancellationToken, Task<string>> accessToken, TimeProvider? clock = null) : ICalendar
{
    private string BaseUrl => "https://www.googleapis.com/calendar/v3/calendars/" + Uri.EscapeDataString(calendarId) + "/events";
    private async Task<HttpResponseMessage> Call(HttpMethod method, string suffix, object? payload, CancellationToken ct, string? etag = null)
    {
        var token = await accessToken(ct);
        return await Transport.Send(http, () =>
        {
            var request = new HttpRequestMessage(method, BaseUrl + suffix);
            request.Headers.Authorization = new("Bearer", token);
            if (etag is not null) request.Headers.TryAddWithoutValidation("If-Match", etag);
            if (payload is not null) request.Content = JsonContent.Create(payload);
            return request;
        }, "Google", true, ct);
    }
    public async Task<List<CalendarEntry>> List(DateOnly today, CancellationToken ct)
    {
        var local = today.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var midnight = new DateTimeOffset(local, SourceParser.Lisbon.GetUtcOffset(local));
        var query = "?singleEvents=true&showDeleted=false&maxResults=2500&timeMin=" + Uri.EscapeDataString(midnight.ToString("O"));
        var entries = new List<CalendarEntry>(); var seenIds = new HashSet<string>(); var seenTokens = new HashSet<string>(); string? next = null;
        for (var page = 0; page < 1000; page++)
        {
            using var response = await Call(HttpMethod.Get, query + (next is null ? "" : "&pageToken=" + Uri.EscapeDataString(next)), null, ct);
            var json = await Transport.Read(response, "Google", ct);
            if (!json.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) throw new SyncException("Google list lacks items.");
            foreach (var item in items.EnumerateArray())
            {
                if (item.TryGetProperty("status", out var status) && status.GetString() == "cancelled") continue;
                var entry = Parse(item);
                if (!seenIds.Add(entry.Id)) throw new SyncException("Google pagination repeated event ID.");
                entries.Add(entry);
            }
            if (!json.TryGetProperty("nextPageToken", out var pageToken) || pageToken.ValueKind == JsonValueKind.Null) return entries;
            next = pageToken.GetString();
            if (string.IsNullOrEmpty(next) || !seenTokens.Add(next)) throw new SyncException("Google pagination did not advance.");
        }
        throw new SyncException("Google pagination cap reached.");
    }
    public static CalendarEntry Parse(JsonElement json)
    {
        try
        {
            var start = json.GetProperty("start"); var end = json.GetProperty("end");
            DateTimeOffset? startTime = null, endTime = null; DateOnly? day = null, endDay = null;
            if (start.TryGetProperty("date", out var date))
            {
                day = DateOnly.ParseExact(date.GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                endDay = DateOnly.ParseExact(end.GetProperty("date").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            else
            {
                static DateTimeOffset Time(JsonElement obj)
                {
                    var value = obj.GetProperty("dateTime").GetString()!;
                    if (!System.Text.RegularExpressions.Regex.IsMatch(value, @"(Z|[+-]\d{2}:\d{2})$")) throw new SyncException("Google event lacks explicit time offset.");
                    return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
                }
                startTime = Time(start); endTime = Time(end);
            }
            if (day is not null && endDay <= day || startTime is not null && endTime <= startTime) throw new SyncException("Google event has invalid interval.");
            var defaults = json.TryGetProperty("reminders", out var reminders) && reminders.TryGetProperty("useDefault", out var useDefault) && useDefault.ValueKind == JsonValueKind.True && (!reminders.TryGetProperty("overrides", out var overrides) || overrides.GetArrayLength() == 0);
            var summary = json.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "";
            var description = json.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
            var id = json.GetProperty("id").GetString();
            if (string.IsNullOrEmpty(id)) throw new SyncException("Google event lacks ID.");
            return new CalendarEntry(id, summary, description, startTime, endTime, day, defaults, endDay);
        }
        catch (SyncException) { throw; }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException) { throw new SyncException("Malformed Google event."); }
    }
    private static object Payload(string id, Slot slot) => new { id, status = "confirmed", summary = slot.Summary, description = slot.Description, start = new { dateTime = slot.Start.ToString("O"), timeZone = "Europe/Lisbon" }, end = new { dateTime = slot.End.ToString("O"), timeZone = "Europe/Lisbon" }, reminders = new { useDefault = true, overrides = Array.Empty<object>() } };
    private async Task<JsonElement?> Get(string id, CancellationToken ct)
    {
        using var response = await Call(HttpMethod.Get, "/" + Uri.EscapeDataString(id), null, ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) return null;
        return await Transport.Read(response, "Google", ct);
    }
    private static bool Cancelled(JsonElement json) => json.TryGetProperty("status", out var s) && s.GetString() == "cancelled";
    private async Task Verify(string id, Slot slot, CancellationToken ct)
    {
        var actual = await Get(id, ct);
        if (actual is null || Cancelled(actual.Value) || Parse(actual.Value).Id != id || !Parse(actual.Value).Matches(slot)) throw new SyncException("Google write readback did not match requested event.");
    }
    public async Task Create(string id, Slot slot, CancellationToken ct)
    {
        // Google retains cancelled IDs. Use a deterministic collision chain rather
        // than random IDs or claiming success from an unverified insert.
        for (var attempt = 0; attempt < 100; attempt++)
        {
            using var response = await Call(HttpMethod.Post, "?sendUpdates=none", Payload(id, slot), ct);
            if (response.StatusCode != HttpStatusCode.Conflict)
            {
                _ = await Transport.Read(response, "Google", ct);
                await Verify(id, slot, ct);
                return;
            }
            var occupied = await Get(id, ct);
            if (occupied is not null && Cancelled(occupied.Value))
            {
                id = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id + ":cancelled")));
                continue;
            }
            if (occupied is null) throw new SyncException("Google insert conflict lacks readback evidence.");
            var entry = Parse(occupied.Value);
            if (entry.Id != id) throw new SyncException("Google insert readback ID disagrees.");
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime((clock ?? TimeProvider.System).GetUtcNow(), SourceParser.Lisbon).DateTime);
            if (!entry.Summary.StartsWith("[AESO]", StringComparison.Ordinal) || !entry.IsFuture(today))
            {
                // Past entries are outside the timeMin list, but their IDs remain
                // occupied. Protect them exactly like a removed-prefix entry.
                id = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id + ":protected")));
                continue;
            }
            if (!entry.Matches(slot)) throw new SyncException("Google insert conflict does not match requested event.");
            return;
        }
        throw new SyncException("Google cancelled-ID collision cap reached.");
    }
    private async Task<string?> CheckBefore(CalendarEntry before, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime((clock ?? TimeProvider.System).GetUtcNow(), SourceParser.Lisbon).DateTime);
        if (!before.IsFuture(today) || !before.Summary.StartsWith("[AESO]", StringComparison.Ordinal)) throw new SyncException("Google event is protected from writes.");
        var actual = await Get(before.Id, ct);
        if (actual is null || Cancelled(actual.Value)) return null;
        if (Parse(actual.Value) != before || !before.Summary.StartsWith("[AESO]", StringComparison.Ordinal)) throw new SyncException("Google event changed after planning; refusing write.");
        if (!actual.Value.TryGetProperty("etag", out var etag) || string.IsNullOrEmpty(etag.GetString())) throw new SyncException("Google event lacks concurrency evidence.");
        return etag.GetString();
    }
    public async Task Replace(CalendarEntry before, Slot slot, CancellationToken ct)
    {
        var etag = await CheckBefore(before, ct);
        if (etag is null) throw new SyncException("Google replacement target disappeared; rerun preview.");
        using var response = await Call(HttpMethod.Patch, "/" + Uri.EscapeDataString(before.Id) + "?sendUpdates=none", Payload(before.Id, slot), ct, etag);
        // A lost successful PATCH may retry with an obsolete etag. Readback settles it.
        if (response.StatusCode != HttpStatusCode.PreconditionFailed) _ = await Transport.Read(response, "Google", ct);
        await Verify(before.Id, slot, ct);
    }
    public async Task Delete(CalendarEntry before, CancellationToken ct)
    {
        var etag = await CheckBefore(before, ct);
        if (etag is not null)
        {
            using var response = await Call(HttpMethod.Delete, "/" + Uri.EscapeDataString(before.Id) + "?sendUpdates=none", null, ct, etag);
            if (!response.IsSuccessStatusCode && response.StatusCode is not (HttpStatusCode.NotFound or HttpStatusCode.Gone or HttpStatusCode.PreconditionFailed)) throw new SyncException("Google delete failed (HTTP " + (int)response.StatusCode + ").");
        }
        var actual = await Get(before.Id, ct);
        if (actual is not null && !Cancelled(actual.Value)) throw new SyncException("Google delete readback still found active event.");
    }
}
public sealed class SyncRunner(ISchoolSource school, ICalendar calendar, SyncConfig config, TimeProvider clock)
{
    public async Task<Plan> Run(bool apply, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), SourceParser.Lisbon).DateTime);
        config.Validate(today);
        var source = await school.Read(ct);
        if (source.Count == 0) throw new SyncException("Empty source cannot authorize reconciliation.");
        var existing = await calendar.List(today, ct);
        var plan = Planner.Build(source, existing, today, config.Scope);
        if (!apply) return plan;
        void CheckDateBoundary()
        {
            var current = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), SourceParser.Lisbon).DateTime);
            config.Validate(current);
            if (current != today) throw new SyncException("Lisbon date changed during reconciliation; rerun preview.");
        }
        // Finish all insertions/restorations before deletions. A failed write stops the run.
        foreach (var create in plan.Actions.Where(x => x.Kind == "create"))
        {
            CheckDateBoundary();
            var replacement = plan.Actions.FirstOrDefault(x => x.Kind == "delete" && x.Id == create.Id);
            if (replacement is not null) await calendar.Replace(replacement.Existing!, create.Slot!, ct);
            else await calendar.Create(create.Id, create.Slot!, ct);
        }
        foreach (var delete in plan.Actions.Where(x => x.Kind == "delete" && !plan.Actions.Any(y => y.Kind == "create" && y.Id == x.Id)))
        {
            CheckDateBoundary();
            await calendar.Delete(delete.Existing!, ct);
        }
        return plan;
    }
}
