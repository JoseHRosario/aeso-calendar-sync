using Aeso.CalendarSync;
namespace Aeso.CalendarSync.Tests;

public class SourceTests
{
    [Fact]
    public void DisplayedSummerTimeIsLisbonNotUtc()
    {
        var entries = SourceParser.Parse("""[{"RealDate":"2027-06-01T00:00:00","PrettyDate":"01-06-2027 (08:15-09:05)","Professor":"Joana Brito","Disciplina":"Matemática","Evento":"Teste de Matemática"}]""", new DateOnly(2026, 9, 1), new DateOnly(2027, 8, 31));
        Assert.Equal("2027-06-01T08:15:00+01:00", entries.Single().Start.ToString("yyyy-MM-ddTHH:mm:sszzz"));
        Assert.Equal("[AESO] Teste de Matemática", entries.Single().Summary);
    }
}
