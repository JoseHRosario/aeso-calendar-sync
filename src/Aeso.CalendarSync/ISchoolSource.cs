namespace Aeso.CalendarSync;

public interface ISchoolSource { Task<List<Slot>> Read(CancellationToken ct); }
