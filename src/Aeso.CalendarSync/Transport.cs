using System.Net;
using System.Text.Json;

namespace Aeso.CalendarSync;

public static class Transport
{
    public static async Task<HttpResponseMessage> Send(HttpClient client, Func<HttpRequestMessage> factory, string service, bool retry, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var request = factory();
                var response = await client.SendAsync(request, ct);
                var transient = response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
                if (!transient || !retry || attempt == 2) return response;
                response.Dispose();
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException && !ct.IsCancellationRequested)
            {
                if (!retry || attempt == 2) throw new SyncException(service + " transport failed.");
            }
            await Task.Delay(TimeSpan.FromMilliseconds(200 * (attempt + 1)), ct);
        }
    }
    public static async Task<JsonElement> Read(HttpResponseMessage response, string service, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode) throw new SyncException(service + " request failed (HTTP " + (int)response.StatusCode + ").");
        try { using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)); return document.RootElement.Clone(); }
        catch (JsonException) { throw new SyncException(service + " returned malformed JSON."); }
    }
}
