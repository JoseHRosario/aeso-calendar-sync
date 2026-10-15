namespace Aeso.CalendarSync;

public sealed class SyncConfig
{
    public string CalendarId { get; set; } = "0e970019a6c7d52ea314fcbaf561159f0ce90a80f9764b7d40fbe7182973de3a@group.calendar.google.com";
    public string AwsRegion { get; set; } = "eu-west-1";
    public string AwsProfile { get; set; } = "aeso-calendar-sync";
    public string AwsAccountId { get; set; } = "497087877832";
    public string SchoolCredentialsParameter { get; set; } = "/aeso-calendar-sync/prod/school-credentials";
    public string GoogleServiceAccountParameter { get; set; } = "/aeso-calendar-sync/prod/google-service-account";
    public string SchoolOrigin { get; set; } = "https://aeso.inovarmais.com";
    public int SchoolEnrolmentId { get; set; } = 43850;
    public int SchoolTipoEnsino { get; set; } = 1;
    public int SchoolStudentId { get; set; } = 8937;
    public int SchoolYear { get; set; } = 2026;
    public int SourceInitialLimit { get; set; } = 30;
    public int SourceLimitStep { get; set; } = 15;
    public int SourceMaximumLimit { get; set; } = 3000;
    public DateOnly YearStart => new(SchoolYear, 9, 1);
    public DateOnly YearEnd => new(SchoolYear + 1, 8, 31);
    public string Scope => $"{SchoolOrigin}/{SchoolEnrolmentId}/{SchoolTipoEnsino}/{SchoolYear}";
    public void ValidateSecrets()
    {
        if (AwsRegion != "eu-west-1" || AwsAccountId != "497087877832" || AwsProfile != "aeso-calendar-sync"
            || SchoolCredentialsParameter != "/aeso-calendar-sync/prod/school-credentials"
            || GoogleServiceAccountParameter != "/aeso-calendar-sync/prod/google-service-account")
            throw new SyncException("Invalid AWS secret configuration.");
    }
    public void Validate(DateOnly today)
    {
        if (SchoolOrigin != "https://aeso.inovarmais.com" || SchoolEnrolmentId <= 0 || SchoolTipoEnsino <= 0 || SchoolStudentId <= 0 || SchoolYear < 2000 || SchoolYear > 9000 || string.IsNullOrWhiteSpace(CalendarId)) throw new SyncException("Invalid configuration or unsupported school origin.");
        if (today < YearStart || today > YearEnd) throw new SyncException("School year is not current; reconfigure enrolment before continuing.");
        if (SourceInitialLimit < 1 || SourceLimitStep < 1 || SourceLimitStep > 10000 || SourceMaximumLimit <= SourceInitialLimit || SourceMaximumLimit > 10000) throw new SyncException("Invalid source coverage limits.");
    }
}
