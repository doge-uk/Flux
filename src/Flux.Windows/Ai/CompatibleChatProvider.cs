using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flux.Core;

namespace Flux.Windows.Ai;

public sealed class CompatibleChatProvider : IAiProvider, IDisposable
{
    private const string SystemInstructions = """
        You are the action-planning component inside Flux, a Windows command launcher.
        Be concise and utility-like, not conversational. Use only the tools provided.
        Never invent paths, PIDs, applications, files, or tool results.
        Inspect before acting. Prefer a clean close over force termination.
        Never request termination of a protected process.
        Destructive and disruptive tools are always confirmed by the Flux host, not by you.
        If the request is ambiguous or unsafe, explain what information is needed.
        """;

    private readonly Uri _endpoint;
    private readonly string _model;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, List<JsonObject>> _conversations = new();

    public CompatibleChatProvider(string name, string endpoint, string model, string? apiKey)
    {
        Name = name;
        _model = model;
        _endpoint = BuildEndpoint(endpoint);
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }
    }

    public string Name { get; }
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_model) && _endpoint.IsAbsoluteUri;

    public async Task<AiTurnResponse> CompleteAsync(
        AiTurnRequest request,
        CancellationToken cancellationToken = default)
    {
        var messages = BuildMessages(request);
        var payload = new JsonObject
        {
            ["model"] = _model,
            ["messages"] = new JsonArray(messages.Select(message => message.DeepClone()).ToArray()),
            ["tools"] = BuildTools(request.Tools),
            ["tool_choice"] = "auto",
            ["stream"] = false
        };

        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsync(_endpoint, content, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new InvalidOperationException(
                $"Could not reach {Name} at {_endpoint.GetLeftPart(UriPartial.Authority)}. " +
                "Start the local model service or update the endpoint in Settings.", exception);
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"{Name} returned {(int)response.StatusCode}: {ReadError(responseBody)}");
        }

        using var document = JsonDocument.Parse(responseBody);
        var root = document.RootElement;
        var message = root.GetProperty("choices")[0].GetProperty("message");
        var text = message.TryGetProperty("content", out var contentElement) && contentElement.ValueKind == JsonValueKind.String
            ? contentElement.GetString() ?? string.Empty
            : string.Empty;
        var calls = ParseToolCalls(message);

        var responseId = root.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
            ? idElement.GetString()!
            : $"flux-{Guid.NewGuid():N}";

        var assistantMessage = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = text
        };
        if (calls.Count > 0 && message.TryGetProperty("tool_calls", out var callsElement))
        {
            assistantMessage["tool_calls"] = JsonNode.Parse(callsElement.GetRawText());
        }

        messages.Add(assistantMessage);
        _conversations[responseId] = messages;
        PruneConversations();

        return new AiTurnResponse(responseId, text, calls);
    }

    public void Dispose() => _http.Dispose();

    private List<JsonObject> BuildMessages(AiTurnRequest request)
    {
        List<JsonObject> messages;
        if (request.PreviousResponseId is not null &&
            _conversations.TryRemove(request.PreviousResponseId, out var previous))
        {
            messages = previous.Select(item => (JsonObject)item.DeepClone()).ToList();
            foreach (var result in request.ToolResults ?? Array.Empty<ToolResult>())
            {
                messages.Add(new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = result.CallId,
                    ["name"] = result.Name,
                    ["content"] = result.Output
                });
            }
        }
        else
        {
            messages =
            [
                new JsonObject { ["role"] = "system", ["content"] = SystemInstructions },
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = $"Request:\n{request.UserRequest}\n\nRelevant Flux context:\n{request.Context}"
                }
            ];
        }

        return messages;
    }

    private static JsonArray BuildTools(IReadOnlyList<ToolDefinition> definitions)
    {
        var tools = new JsonArray();
        foreach (var definition in definitions)
        {
            tools.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = definition.Name,
                    ["description"] = definition.Description,
                    ["parameters"] = definition.Parameters.DeepClone()
                }
            });
        }

        return tools;
    }

    private static IReadOnlyList<ToolCall> ParseToolCalls(JsonElement message)
    {
        if (!message.TryGetProperty("tool_calls", out var callsElement) || callsElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<ToolCall>();
        }

        var calls = new List<ToolCall>();
        foreach (var callElement in callsElement.EnumerateArray())
        {
            var function = callElement.GetProperty("function");
            var name = function.GetProperty("name").GetString() ?? string.Empty;
            var id = callElement.TryGetProperty("id", out var idElement)
                ? idElement.GetString() ?? $"call-{Guid.NewGuid():N}"
                : $"call-{Guid.NewGuid():N}";
            var argumentsElement = function.GetProperty("arguments");
            JsonElement arguments;
            if (argumentsElement.ValueKind == JsonValueKind.String)
            {
                using var argsDocument = JsonDocument.Parse(argumentsElement.GetString() ?? "{}");
                arguments = argsDocument.RootElement.Clone();
            }
            else
            {
                arguments = argumentsElement.Clone();
            }

            calls.Add(new ToolCall(id, name, arguments));
        }

        return calls;
    }

    private static Uri BuildEndpoint(string endpoint)
    {
        endpoint = endpoint.TrimEnd('/');
        if (endpoint.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            return new Uri(endpoint + "/chat/completions");
        }

        return new Uri(endpoint + "/v1/chat/completions");
    }

    private static string ReadError(string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var message))
                {
                    return message.GetString() ?? responseBody;
                }

                return error.ToString();
            }
        }
        catch
        {
        }

        return responseBody.Length > 500 ? responseBody[..500] : responseBody;
    }

    private void PruneConversations()
    {
        if (_conversations.Count <= 20)
        {
            return;
        }

        foreach (var key in _conversations.Keys.Take(_conversations.Count - 20))
        {
            _conversations.TryRemove(key, out _);
        }
    }
}
