using System.Text.Json;
using System.Net.Http;

namespace Flux.Windows.Ai;

public sealed class OllamaDiscoveryService
{
    public async Task<(bool Available, IReadOnlyList<string> Models, string Message)> ProbeAsync(
        string endpoint,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var uri = new Uri(endpoint.TrimEnd('/') + "/api/tags");
            using var response = await client.GetAsync(uri, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return (false, Array.Empty<string>(), $"Local server returned {(int)response.StatusCode}.");
            }

            using var document = JsonDocument.Parse(body);
            var models = document.RootElement.GetProperty("models")
                .EnumerateArray()
                .Select(item => item.TryGetProperty("name", out var name) ? name.GetString() : null)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Cast<string>()
                .ToArray();
            return (true, models, models.Length == 0 ? "Connected, but no models are installed." : $"Connected — {models.Length} model(s) available.");
        }
        catch (Exception exception)
        {
            return (false, Array.Empty<string>(), $"Local model unavailable: {exception.Message}");
        }
    }
}
