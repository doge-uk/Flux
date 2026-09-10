using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Flux.Core;
using Flux.Windows.Configuration;

namespace Flux.Windows.Ai;

public enum AiModelStage
{
    Loading,
    Ready,
    Active,
    Upgraded,
    Unloaded,
    Failed
}

public sealed record AiModelStatus(string Model, string Provider, AiModelStage Stage, bool IsLarger);

public sealed class AiProviderRouter(AppSettings settings) : IStreamingAiProvider, IDisposable
{
    private const string EscalationToolName = "request_larger_model";
    private static readonly ToolDefinition EscalationTool = new(
        EscalationToolName,
        "Hand this request to the configured larger model. Use only when the request needs multi-step planning, substantial reasoning, or is too ambiguous for you. Do not use it for simple application open, close, kill, list, search, or close-everything-except commands.",
        new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["reason"] = new JsonObject { ["type"] = "string" }
            },
            ["additionalProperties"] = false
        },
        PermissionLevel.ReadOnly);

    private readonly ConcurrentDictionary<string, CompatibleChatProvider> _providers = new();
    private readonly ConcurrentDictionary<string, string> _responseOwners = new();
    private readonly object _warmupGate = new();
    private string? _warmupKey;
    private Task? _warmupTask;
    private readonly object _unloadGate = new();
    private CancellationTokenSource? _unloadCancellation;
    private int _activeRequests;

    public event Action<AiModelStatus>? ModelStatusChanged;

    public string Name => settings.AiMode switch
    {
        AiMode.Local => "Local model",
        AiMode.Cloud => "OpenAI",
        AiMode.Automatic => "Automatic",
        _ => "Disabled"
    };

    public bool IsConfigured => settings.AiMode switch
    {
        AiMode.Local => HasFastLocalConfiguration,
        AiMode.Cloud => HasCloudConfiguration,
        AiMode.Automatic => HasFastLocalConfiguration || HasCloudConfiguration,
        _ => false
    };

    private bool HasFastLocalConfiguration => HasLocalModel(settings.LocalModel);
    private bool HasLargeLocalConfiguration => HasLocalModel(settings.LocalLargeModel);
    private bool HasLocalModel(string model) =>
        Uri.TryCreate(settings.LocalEndpoint, UriKind.Absolute, out _) && !string.IsNullOrWhiteSpace(model);

    private bool HasCloudConfiguration =>
        Uri.TryCreate(settings.CloudEndpoint, UriKind.Absolute, out _) &&
        !string.IsNullOrWhiteSpace(settings.CloudModel) &&
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY"));

    public Task<AiTurnResponse> CompleteAsync(AiTurnRequest request, CancellationToken cancellationToken = default) =>
        CompleteTrackedAsync(request, null, cancellationToken);

    public Task<AiTurnResponse> CompleteStreamingAsync(
        AiTurnRequest request,
        IProgress<string> textProgress,
        CancellationToken cancellationToken = default) =>
        CompleteTrackedAsync(request, textProgress, cancellationToken);

    public Task BeginLocalModelSessionAsync(CancellationToken cancellationToken = default)
    {
        CancelScheduledUnload();
        return WarmUpFastModelAsync(cancellationToken);
    }

    public void ScheduleLocalModelUnload(TimeSpan delay)
    {
        CancelScheduledUnload();
        if (settings.AiMode is AiMode.Disabled or AiMode.Cloud || !HasFastLocalConfiguration)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        lock (_unloadGate)
        {
            _unloadCancellation = cancellation;
        }
        _ = UnloadAfterDelayAsync(delay, cancellation);
    }

    private async Task<AiTurnResponse> CompleteTrackedAsync(
        AiTurnRequest request,
        IProgress<string>? textProgress,
        CancellationToken cancellationToken)
    {
        CancelScheduledUnload();
        Interlocked.Increment(ref _activeRequests);
        try
        {
            return await CompleteCoreAsync(request, textProgress, cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _activeRequests);
        }
    }

    public Task WarmUpFastModelAsync(CancellationToken cancellationToken = default)
    {
        if (settings.AiMode is AiMode.Disabled or AiMode.Cloud || !HasFastLocalConfiguration)
        {
            return Task.CompletedTask;
        }

        var fast = GetLocalProvider(settings.LocalModel, "Fast local model");
        Task warmup;
        lock (_warmupGate)
        {
            if (_warmupKey != fast.Key || _warmupTask is null || _warmupTask.IsFaulted || _warmupTask.IsCanceled)
            {
                _warmupKey = fast.Key;
                _warmupTask = WarmUpCoreAsync(fast.Provider);
            }
            warmup = _warmupTask;
        }

        return cancellationToken.CanBeCanceled ? warmup.WaitAsync(cancellationToken) : warmup;
    }

    private async Task WarmUpCoreAsync(CompatibleChatProvider provider)
    {
        PublishStatus(provider, AiModelStage.Loading, isLarger: false);
        try
        {
            await provider.WarmUpAsync();
            PublishStatus(provider, AiModelStage.Ready, isLarger: false);
        }
        catch
        {
            PublishStatus(provider, AiModelStage.Failed, isLarger: false);
            throw;
        }
    }

    private async Task UnloadAfterDelayAsync(TimeSpan delay, CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(delay, cancellation.Token);
            while (Volatile.Read(ref _activeRequests) > 0)
            {
                await Task.Delay(250, cancellation.Token);
            }

            var localProviders = _providers.Values
                .Where(provider => provider.IsOllama)
                .Distinct()
                .ToArray();
            foreach (var provider in localProviders)
            {
                await provider.UnloadAsync(cancellation.Token);
            }

            lock (_warmupGate)
            {
                _warmupKey = null;
                _warmupTask = null;
            }

            var fast = localProviders.FirstOrDefault(provider =>
                string.Equals(provider.Model, settings.LocalModel, StringComparison.OrdinalIgnoreCase));
            if (fast is not null)
            {
                PublishStatus(fast, AiModelStage.Unloaded, isLarger: false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lock (_unloadGate)
            {
                if (ReferenceEquals(_unloadCancellation, cancellation))
                {
                    _unloadCancellation = null;
                }
            }
            cancellation.Dispose();
        }
    }

    private void CancelScheduledUnload()
    {
        CancellationTokenSource? cancellation;
        lock (_unloadGate)
        {
            cancellation = _unloadCancellation;
            _unloadCancellation = null;
        }
        cancellation?.Cancel();
    }

    private async Task<AiTurnResponse> CompleteCoreAsync(
        AiTurnRequest request,
        IProgress<string>? textProgress,
        CancellationToken cancellationToken)
    {
        if (settings.AiMode == AiMode.Disabled)
        {
            throw new InvalidOperationException("AI is disabled in Flux Settings.");
        }

        if (request.PreviousResponseId is not null &&
            _responseOwners.TryRemove(request.PreviousResponseId, out var owner) &&
            _providers.TryGetValue(owner, out var continuingProvider))
        {
            return await CompleteWithAsync(continuingProvider, owner, request, textProgress, cancellationToken);
        }

        if (settings.AiMode == AiMode.Cloud || !HasFastLocalConfiguration)
        {
            var cloud = GetCloudProvider();
            return await CompleteWithAsync(cloud.Provider, cloud.Key, request, textProgress, cancellationToken);
        }

        var fast = GetLocalProvider(settings.LocalModel, "Fast local model");
        try
        {
            await WarmUpFastModelAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // The real request below reports the useful provider error if the warmup failed.
        }
        PublishStatus(fast.Provider, AiModelStage.Active, isLarger: false);
        var routingRequest = request with
        {
            Context = request.Context +
                "\nInternal routing: solve simple commands directly. If this genuinely requires deeper reasoning, call request_larger_model and emit no user-facing text.",
            Tools = request.Tools.Concat([EscalationTool]).ToArray()
        };
        var first = await CompleteWithAsync(fast.Provider, fast.Key, routingRequest, textProgress, cancellationToken);
        if (!first.ToolCalls.Any(call => string.Equals(call.Name, EscalationToolName, StringComparison.OrdinalIgnoreCase)))
        {
            return first with
            {
                ToolCalls = first.ToolCalls
                    .Where(call => !string.Equals(call.Name, EscalationToolName, StringComparison.OrdinalIgnoreCase))
                    .ToArray()
            };
        }

        _responseOwners.TryRemove(first.ResponseId, out _);
        var larger = GetEscalationProvider();
        PublishStatus(larger.Provider, AiModelStage.Upgraded, isLarger: true);
        return await CompleteWithAsync(larger.Provider, larger.Key, request, textProgress, cancellationToken);
    }

    private async Task<AiTurnResponse> CompleteWithAsync(
        CompatibleChatProvider provider,
        string key,
        AiTurnRequest request,
        IProgress<string>? textProgress,
        CancellationToken cancellationToken)
    {
        var response = textProgress is null
            ? await provider.CompleteAsync(request, cancellationToken)
            : await provider.CompleteStreamingAsync(request, textProgress, cancellationToken);
        _responseOwners[response.ResponseId] = key;
        return response;
    }

    private (CompatibleChatProvider Provider, string Key) GetEscalationProvider()
    {
        if (settings.AiMode == AiMode.Automatic && HasCloudConfiguration)
        {
            return GetCloudProvider();
        }

        if (HasLargeLocalConfiguration)
        {
            return GetLocalProvider(settings.LocalLargeModel, "Larger local model");
        }

        throw new InvalidOperationException(
            "The fast model requested more capability, but no larger local model or cloud provider is configured.");
    }

    private (CompatibleChatProvider Provider, string Key) GetCloudProvider()
    {
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "OPENAI_API_KEY is not set. Flux never stores cloud API keys in its settings file.");
        }

        return GetProvider("OpenAI", settings.CloudEndpoint, settings.CloudModel, apiKey);
    }

    private (CompatibleChatProvider Provider, string Key) GetLocalProvider(string model, string name) =>
        GetProvider(name, settings.LocalEndpoint, model, "ollama");

    private (CompatibleChatProvider Provider, string Key) GetProvider(
        string name,
        string endpoint,
        string model,
        string apiKey)
    {
        var key = $"{name}|{endpoint}|{model}";
        var provider = _providers.GetOrAdd(key, _ => new CompatibleChatProvider(name, endpoint, model, apiKey));
        return (provider, key);
    }

    private void PublishStatus(CompatibleChatProvider provider, AiModelStage stage, bool isLarger) =>
        ModelStatusChanged?.Invoke(new AiModelStatus(provider.Model, provider.Name, stage, isLarger));

    public void Dispose()
    {
        CancelScheduledUnload();
        foreach (var provider in _providers.Values)
        {
            provider.Dispose();
        }
        _providers.Clear();
        _responseOwners.Clear();
    }
}
