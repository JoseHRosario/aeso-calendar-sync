using Google.Apis.Auth.OAuth2;
namespace Aeso.CalendarSync;

public sealed class SecretSnapshot(SchoolCredentials school, GoogleCredential google)
{
    public SchoolCredentials School { get; } = school;
    public GoogleCredential Google { get; } = google;
}
