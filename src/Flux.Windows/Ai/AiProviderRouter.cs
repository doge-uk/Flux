using Flux.Core;
using Flux.Windows.Configuration;

namespace Flux.Windows.Ai;

public sealed class AiProviderRouter(AppSettings settings) : IAiProvider, IDisposable
{
    private CompatibleChatProvider? _active;
    private string? _signature;

    public string Name => settings.AiMode switch
    {
        AiMode.Local => "Local model",
        AiMode.Cloud => "OpenAI",
        AiMode.Automatic => "Automatic",
        _ => "Disabled"
    };

    public bool IsConfigured => settings.AiMode switch
    {
        AiMode.Local => HasLocalConfiguration,
        AiMode.Cloud => HasCloudConfiguration,
        AiMode.Automatic => HasLocalConfiguration || HasCloudConfiguration,
        _ => false
    };

    private bool HasLocalConfiguration =>
        Uri.TryCreate(settings.LocalEndpoint, UriKind.Absolute, out _) &&
        !string.IsNullOrWhiteSpace(settings.LocalModel);

    private bool HasCloudConfiguration =>
        Uri.TryCreate(settings.CloudEndpoint, UriKind.Absolute, out _) &&
        !string.IsNullOrWhiteSpace(settings.CloudModel) &&
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY"));

    public Task<AiTurnResponse> CompleteAsync(AiTurnRequest request, CancellationToken cancellationToken = default)
    {
        var useLocal = settings.AiMode == AiMode.Local ||
            settings.AiMode == AiMode.Automatic && HasLocalConfiguration;

        if (settings.AiMode == AiMode.Disabled)
        {
            throw new InvalidOperationException("AI is disabled in Flux Settings.");
        }

        if (useLocal)
        {
            return GetOrCreate(
                "Local model",
                settings.LocalEndpoint,
                settings.LocalModel,
                "ollama").CompleteAsync(request, cancellationToken);
        }

        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("OPENAI_API_KEY is not set. Flux never stores cloud API keys in its settings file.");
        }

        return GetOrCreate(
            "OpenAI",
            settings.CloudEndpoint,
            settings.CloudModel,
            apiKey).CompleteAsync(request, cancellationToken);
    }

    public void Dispose() => _active?.Dispose();

    private CompatibleChatProvider GetOrCreate(string name, string endpoint, string model, string apiKey)
    {
        var signature = $"{name}|{endpoint}|{model}";
        if (_active is not null && signature == _signature)
        {
            return _active;
        }

        _active?.Dispose();
        _active = new CompatibleChatProvider(name, endpoint, model, apiKey);
        _signature = signature;
        return _active;
    }
}

