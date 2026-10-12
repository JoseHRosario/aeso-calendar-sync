using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aeso.CalendarSync;

public sealed class SyncException(string message) : Exception(message);
public sealed record SchoolEntry(string RealDate, string PrettyDate, string Professor, string Disciplina, string Evento);
public sealed record Slot(string Summary, string Description, DateTimeOffset Start, DateTimeOffset End)
{
    public string Canonical => JsonSerializer.Serialize(new[] { Summary, Description, Start.ToString("O"), End.ToString("O") });
}
public static class SourceParser
{
    public static readonly TimeZoneInfo Lisbon = TimeZoneInfo.FindSystemTimeZoneById("Europe/Lisbon");
    public static List<Slot> Parse(string json, DateOnly yearStart, DateOnly yearEnd)
    {
        try
        {
            var rows = JsonSerializer.Deserialize<List<SchoolEntry>>(json) ?? throw new SyncException("Source is not a list.");
            var result = new List<Slot>();
            foreach (var row in rows)
            {
                var match = Regex.Match(row.PrettyDate ?? "", @"\A(\d{2}-\d{2}-\d{4}) \((\d{2}:\d{2})-(\d{2}:\d{2})\)\z");
                if (!match.Success || string.IsNullOrWhiteSpace(row.Evento) || row.Professor is null || row.Disciplina is null) throw new SyncException("Invalid source fields.");
                var date = DateOnly.ParseExact(match.Groups[1].Value, "dd-MM-yyyy", CultureInfo.InvariantCulture);
                if (!DateTime.TryParseExact(row.RealDate, ["yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ssZ", "yyyy-MM-ddTHH:mm:ss.fff"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var real) || DateOnly.FromDateTime(real) != date) throw new SyncException("Source dates disagree.");
                if (date < yearStart || date > yearEnd) throw new SyncException("Source outside configured school year.");
                DateTimeOffset Convert(string time)
                {
                    var local = date.ToDateTime(TimeOnly.ParseExact(time, "HH:mm", CultureInfo.InvariantCulture), DateTimeKind.Unspecified);
                    if (Lisbon.IsInvalidTime(local) || Lisbon.IsAmbiguousTime(local)) throw new SyncException("Invalid or ambiguous Lisbon time.");
                    return new DateTimeOffset(local, Lisbon.GetUtcOffset(local));
                }
                var start = Convert(match.Groups[2].Value); var end = Convert(match.Groups[3].Value);
                if (end <= start) throw new SyncException("Invalid source time interval.");
                result.Add(new Slot("[AESO] " + row.Evento, $"Disciplina: {row.Disciplina}\nProfessor: {row.Professor}\n{row.Evento}", start, end));
            }
            return result;
        }
        catch (SyncException) { throw; }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException or NullReferenceException) { throw new SyncException("Malformed source list."); }
    }
}

public sealed record CalendarEntry(string Id, string Summary, string Description, DateTimeOffset? Start, DateTimeOffset? End, DateOnly? AllDayStart, bool DefaultReminders, DateOnly? AllDayEnd = null, string? ETag = null)
{
    public bool Matches(Slot slot) => Summary == slot.Summary && Description == slot.Description && Start == slot.Start && End == slot.End && AllDayStart is null && DefaultReminders;
    public bool IsFuture(DateOnly today) => AllDayStart is { } day ? day >= today : Start is { } time && DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time, SourceParser.Lisbon).DateTime) >= today;
}
public sealed record Desired(string Id, Slot Slot);
public sealed record Change(string Kind, string Id, Slot? Slot, CalendarEntry? Existing, int CandidateLimit = EventIdentity.CandidateLimit)
{
    public object Report => new { kind = Kind, summary = Slot?.Summary ?? Existing!.Summary, start = Slot?.Start.ToString("O") ?? Existing!.Start?.ToString("O") ?? Existing!.AllDayStart?.ToString("yyyy-MM-dd"), end = Slot?.End.ToString("O") ?? Existing!.End?.ToString("O") ?? Existing!.AllDayEnd?.ToString("yyyy-MM-dd") };
}
public sealed record Plan(int SourceCount, int FutureCount, int Kept, List<Change> Actions)
{
    public object Report => new { status = "success", sourceCount = SourceCount, futureCount = FutureCount, created = Actions.Count(x => x.Kind == "create"), deleted = Actions.Count(x => x.Kind == "delete"), kept = Kept, actions = Actions.Select(x => x.Report).ToArray() };
}
public static class EventIdentity
{
    public const int CandidateLimit = 100;
    public static string Next(string id) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id + ":occupied")));
    public static IEnumerable<string> Candidates(string primary)
    {
        var id = primary;
        for (var attempt = 0; attempt < CandidateLimit; attempt++, id = Next(id)) yield return id;
    }
}
public static class Planner
{
    public static Plan Build(IReadOnlyList<Slot> source, IReadOnlyList<CalendarEntry> existing, DateOnly today, string scope)
    {
        if (source.Count == 0) throw new SyncException("Empty source cannot authorize reconciliation.");
        var future = source.Where(x => DateOnly.FromDateTime(x.Start.DateTime) >= today).OrderBy(x => x.Canonical, StringComparer.Ordinal).ToList();
        var occurrences = new Dictionary<string, int>(); var desired = new List<Desired>();
        foreach (var slot in future)
        {
            occurrences.TryGetValue(slot.Canonical, out var occurrence); occurrences[slot.Canonical] = occurrence + 1;
            var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { scope, slot = slot.Canonical, occurrence }))));
            desired.Add(new Desired(id, slot));
        }
        var managed = existing.Where(x => x.Summary.StartsWith("[AESO]", StringComparison.Ordinal) && x.IsFuture(today)).ToList();
        var unmatched = new List<CalendarEntry>(managed); var actions = new List<Change>(); var kept = 0;
        foreach (var wanted in desired)
        {
            var match = unmatched.FirstOrDefault(x => x.Matches(wanted.Slot));
            if (match is not null) { unmatched.Remove(match); kept++; continue; }
            // Predict the whole bounded chain before allocating: its primary may be
            // a tombstone or a past entry absent from Google's timeMin listing.
            var candidates = EventIdentity.Candidates(wanted.Id).ToArray();
            var collision = candidates.Select(id => unmatched.FirstOrDefault(x => x.Id == id)).FirstOrDefault(x => x is not null);
            var id = collision?.Id ?? candidates.FirstOrDefault(candidate => !existing.Any(x => x.Id == candidate))
                ?? throw new SyncException("Cannot allocate safe event ID.");
            // Same deterministic ID edited manually: represent replacement in the plan,
            // but apply as PATCH, avoiding Google's deleted-ID tombstones.
            if (collision is not null) { unmatched.Remove(collision); actions.Add(new Change("delete", id, null, collision)); }
            actions.Add(new Change("create", id, wanted.Slot, null, candidates.Length - Array.IndexOf(candidates, id)));
        }
        actions.AddRange(unmatched.Select(x => new Change("delete", x.Id, null, x)));
        return new Plan(source.Count, future.Count, kept, actions);
    }
}
