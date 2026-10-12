using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Google.Apis.Auth.OAuth2;
namespace Aeso.CalendarSync;

public static class SecretPayloadParser
{
    public static SecretSnapshot Parse(string schoolJson, string googleJson)
    {
        try
        {
            using var schoolDocument = JsonDocument.Parse(schoolJson);
            ObjectWithoutDuplicates(schoolDocument.RootElement, StringComparer.OrdinalIgnoreCase);
            var school = JsonSerializer.Deserialize<SchoolCredentials>(schoolJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
            });
            if (school is null || string.IsNullOrWhiteSpace(school.Username) || string.IsNullOrWhiteSpace(school.Password)) throw new FormatException();
            using var document = JsonDocument.Parse(googleJson);
            var google = document.RootElement;
            ObjectWithoutDuplicates(google, StringComparer.OrdinalIgnoreCase);
            string Required(string name)
            {
                var value = google.GetProperty(name);
                if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())) throw new FormatException();
                return value.GetString()!;
            }
            if (Required("type") != "service_account" || Required("token_uri") != "https://oauth2.googleapis.com/token") throw new FormatException();
            var email = Required("client_email");
            if (!Regex.IsMatch(email, @"\A[a-zA-Z0-9._-]+@[a-zA-Z0-9-]+\.iam\.gserviceaccount\.com\z", RegexOptions.CultureInvariant)) throw new FormatException();
            if (google.TryGetProperty("universe_domain", out var universe) && (universe.ValueKind != JsonValueKind.String || universe.GetString() != "googleapis.com")) throw new FormatException();
            // Use only validated fields. Ignore ancillary SA metadata, never credential-source,
            // delegation or alternate-universe fields that could redirect credential material.
            var serviceAccount = CredentialFactory.FromJsonParameters<ServiceAccountCredential>(new JsonCredentialParameters
            {
                Type = "service_account", ClientEmail = email, PrivateKey = Required("private_key"),
                TokenUri = "https://oauth2.googleapis.com/token", UniverseDomain = "googleapis.com"
            });
            return new SecretSnapshot(school, serviceAccount.ToGoogleCredential().CreateScoped("https://www.googleapis.com/auth/calendar.events"));
        }
        catch (Exception) { throw new SyncException("SSM secret payload is invalid."); }
    }

    private static void ObjectWithoutDuplicates(JsonElement element, StringComparer comparer)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new FormatException();
        var names = new HashSet<string>(comparer);
        foreach (var property in element.EnumerateObject()) if (!names.Add(property.Name)) throw new FormatException();
    }
}
