using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flux.Core;

namespace Flux.Windows.Ai;

public sealed class CompatibleChatProvider : IStreamingAiProvider, IDisposable
{
    internal const string SystemInstructions = """
        You are the action-planning component inside Flux, a Windows command launcher.
        Be concise and utility-like, not conversational. Use only the tools provided.
        Use lightweight Markdown only when it materially improves readability. Flux renders headings, short lists, emphasis, inline code, links, quotes and code blocks. Avoid tables and decorative formatting.
        Preserve normal sentences and real line breaks. Never emit escaped newline text such as backslash-n.
        Never invent paths, PIDs, applications, files, or tool results.
        For action requests, call the appropriate tool and do not claim the action succeeded in your text.
        Use close_applications for multiple named apps and close_applications_except for 'everything except' requests.
        For semantic requests such as closing unproductive, unnecessary, background, or unused apps, call list_processes first. Select only visible user apps that clearly match the request, then pass their exact display names to close_applications. Never pass the user's descriptive phrase as an application name. Ask one short clarification when the category is subjective and cannot be inferred safely.
        To close open folder windows, pass File Explorer to close_application or close_applications. This never terminates the Windows shell.
        Application tools resolve live identities internally; pass the application names from the user's request exactly.
        Prefer a clean close over force termination.
        Never request termination of a protected process.
        Destructive and disruptive tools are always confirmed by the Flux host, not by you.
        If the request is ambiguous or unsafe, explain what information is needed.
        """;

    private readonly Uri _endpoint;
    private readonly string _model;
    private readonly Uri? _ollamaGenerateEndpoint;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, List<JsonObject>> _conversations = new();

    public CompatibleChatProvider(string name, string endpoint, string model, string? apiKey)
    {
        Name = name;
        _model = model;
        _endpoint = BuildEndpoint(endpoint);
        _ollamaGenerateEndpoint = string.Equals(apiKey, "ollama", StringComparison.OrdinalIgnoreCase)
            ? BuildOllamaGenerateEndpoint(_endpoint)
            : null;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }
    }

    public string Name { get; }
    public string Model => _model;
    public bool IsOllama => _ollamaGenerateEndpoint is not null;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_model) && _endpoint.IsAbsoluteUri;

    public async Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        if (_ollamaGenerateEndpoint is not null)
        {
            await SendOllamaLifecycleRequestAsync(JsonValue.Create(-1)!, cancellationToken).ConfigureAwait(false);
            return;
        }

        var payload = new JsonObject
        {
            ["model"] = _model,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "user", ["content"] = "Ready" }),
            ["max_tokens"] = 1,
            ["temperature"] = 0,
            ["stream"] = false
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"{Name} warmup returned {(int)response.StatusCode}: {ReadError(errorBody)}");
        }
    }

    public Task UnloadAsync(CancellationToken cancellationToken = default) =>
        _ollamaGenerateEndpoint is null
            ? Task.CompletedTask
            : SendOllamaLifecycleRequestAsync(JsonValue.Create(0)!, cancellationToken);

    private async Task SendOllamaLifecycleRequestAsync(JsonNode keepAlive, CancellationToken cancellationToken)
    {
        var payload = new JsonObject
        {
            ["model"] = _model,
            ["prompt"] = string.Empty,
            ["stream"] = false,
            ["keep_alive"] = keepAlive
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, _ollamaGenerateEndpoint)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"Ollama returned {(int)response.StatusCode}: {ReadError(errorBody)}");
        }
    }

    public Task<AiTurnResponse> CompleteAsync(
        AiTurnRequest request,
        CancellationToken cancellationToken = default) =>
        CompleteCoreAsync(request, null, cancellationToken);

    public Task<AiTurnResponse> CompleteStreamingAsync(
        AiTurnRequest request,
        IProgress<string> textProgress,
        CancellationToken cancellationToken = default) =>
        CompleteCoreAsync(request, textProgress, cancellationToken);

    private async Task<AiTurnResponse> CompleteCoreAsync(
        AiTurnRequest request,
        IProgress<string>? textProgress,
        CancellationToken cancellationToken)
    {
        var messages = BuildMessages(request);
        var payload = new JsonObject
        {
            ["model"] = _model,
            ["messages"] = new JsonArray(messages.Select(message => message.DeepClone()).ToArray()),
            ["tools"] = BuildTools(request.Tools),
            ["tool_choice"] = "auto",
            ["stream"] = textProgress is not null
        };

        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        HttpResponseMessage? response = null;
        try
        {
            response = await _http.SendAsync(
                requestMessage,
                textProgress is null ? HttpCompletionOption.ResponseContentRead : HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new InvalidOperationException(
                $"Could not reach {Name} at {_endpoint.GetLeftPart(UriPartial.Authority)}. " +
                "Start the local model service or update the endpoint in Settings.", exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new InvalidOperationException($"{Name} returned {(int)response.StatusCode}: {ReadError(errorBody)}");
            }

            string responseId;
            string text;
            IReadOnlyList<ToolCall> calls;
            if (textProgress is not null)
            {
                (responseId, text, calls) = await ReadStreamingResponseAsync(response, textProgress, cancellationToken);
            }
            else
            {
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                using var document = JsonDocument.Parse(responseBody);
                var root = document.RootElement;
                var message = root.GetProperty("choices")[0].GetProperty("message");
                text = ReadText(message);
                calls = ParseToolCalls(message);
                responseId = root.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
                    ? idElement.GetString()!
                    : $"flux-{Guid.NewGuid():N}";
            }

            StoreConversation(messages, responseId, text, calls);
            return new AiTurnResponse(responseId, text, calls);
        }
    }

    private async Task<(string ResponseId, string Text, IReadOnlyList<ToolCall> Calls)> ReadStreamingResponseAsync(
        HttpResponseMessage response,
        IProgress<string> textProgress,
        CancellationToken cancellationToken)
    {
        var responseId = $"flux-{Guid.NewGuid():N}";
        var text = new StringBuilder();
        var calls = new Dictionary<int, StreamingToolCall>();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var data = line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                ? line[5..].TrimStart()
                : line.Trim();
            if (data == "[DONE]")
            {
                break;
            }

            if (!data.StartsWith('{'))
            {
                continue;
            }

            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;
            if (root.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String)
            {
                responseId = idElement.GetString() ?? responseId;
            }

            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            {
                continue;
            }

            var choice = choices[0];
            if (!choice.TryGetProperty("delta", out var delta))
            {
                continue;
            }

            if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
            {
                var chunk = content.GetString();
                if (!string.IsNullOrEmpty(chunk))
                {
                    text.Append(chunk);
                    textProgress.Report(chunk);
                }
            }

            if (!delta.TryGetProperty("tool_calls", out var toolCalls) || toolCalls.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var toolCall in toolCalls.EnumerateArray())
            {
                var index = toolCall.TryGetProperty("index", out var indexElement) ? indexElement.GetInt32() : calls.Count;
                if (!calls.TryGetValue(index, out var accumulator))
                {
                    accumulator = new StreamingToolCall();
                    calls[index] = accumulator;
                }

                if (toolCall.TryGetProperty("id", out var callId) && callId.ValueKind == JsonValueKind.String)
                {
                    accumulator.Id = callId.GetString() ?? accumulator.Id;
                }

                if (toolCall.TryGetProperty("function", out var function))
                {
                    if (function.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                    {
                        var nameChunk = name.GetString() ?? string.Empty;
                        if (nameChunk.StartsWith(accumulator.Name, StringComparison.Ordinal))
                        {
                            accumulator.Name = nameChunk;
                        }
                        else if (!accumulator.Name.EndsWith(nameChunk, StringComparison.Ordinal))
                        {
                            accumulator.Name += nameChunk;
                        }
                    }
                    if (function.TryGetProperty("arguments", out var arguments))
                    {
                        accumulator.Arguments.Append(arguments.ValueKind == JsonValueKind.String
                            ? arguments.GetString()
                            : arguments.GetRawText());
                    }
                }
            }
        }

        var parsedCalls = calls.OrderBy(item => item.Key).Select(item => item.Value.ToToolCall()).ToArray();
        return (responseId, NormalizePlainText(text.ToString()), parsedCalls);
    }

    private void StoreConversation(
        List<JsonObject> messages,
        string responseId,
        string text,
        IReadOnlyList<ToolCall> calls)
    {
        var assistantMessage = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = text
        };
        if (calls.Count > 0)
        {
            assistantMessage["tool_calls"] = new JsonArray(calls.Select(call => new JsonObject
            {
                ["id"] = call.Id,
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = call.Name,
                    ["arguments"] = call.Arguments.GetRawText()
                }
            }).ToArray());
        }

        messages.Add(assistantMessage);
        _conversations[responseId] = messages;
        PruneConversations();
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

    internal static IReadOnlyList<ToolCall> ParseToolCalls(JsonElement message)
    {
        if (!message.TryGetProperty("tool_calls", out var callsElement) || callsElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<ToolCall>();
        }

        var calls = new List<ToolCall>();
        foreach (var callElement in callsElement.EnumerateArray())
        {
            if (callElement.ValueKind != JsonValueKind.Object ||
                !callElement.TryGetProperty("function", out var function) ||
                function.ValueKind != JsonValueKind.Object ||
                !function.TryGetProperty("name", out var nameElement) ||
                nameElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(nameElement.GetString()))
            {
                continue;
            }

            var name = nameElement.GetString()!;
            var id = callElement.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
                ? idElement.GetString() ?? $"call-{Guid.NewGuid():N}"
                : $"call-{Guid.NewGuid():N}";
            var arguments = function.TryGetProperty("arguments", out var argumentsElement)
                ? ParseToolArguments(argumentsElement)
                : EmptyToolArguments();

            calls.Add(new ToolCall(id, name, arguments));
        }

        return calls;
    }

    private static JsonElement ParseToolArguments(JsonElement arguments)
    {
        if (arguments.ValueKind == JsonValueKind.Object)
        {
            return arguments.Clone();
        }

        if (arguments.ValueKind == JsonValueKind.String)
        {
            try
            {
                using var document = JsonDocument.Parse(arguments.GetString() ?? "{}");
                return document.RootElement.ValueKind == JsonValueKind.Object
                    ? document.RootElement.Clone()
                    : EmptyToolArguments();
            }
            catch (JsonException)
            {
                return EmptyToolArguments();
            }
        }

        return EmptyToolArguments();
    }

    private static JsonElement EmptyToolArguments()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    private static string ReadText(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content) || content.ValueKind == JsonValueKind.Null)
        {
            return string.Empty;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return NormalizePlainText(content.GetString() ?? string.Empty);
        }

        if (content.ValueKind == JsonValueKind.Array)
        {
            var parts = content.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.Object &&
                    item.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                .Select(item => item.GetProperty("text").GetString());
            return NormalizePlainText(string.Concat(parts));
        }

        return string.Empty;
    }

    private static string NormalizePlainText(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();

    private static Uri BuildEndpoint(string endpoint)
    {
        endpoint = endpoint.TrimEnd('/');
        if (endpoint.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            return new Uri(endpoint + "/chat/completions");
        }

        return new Uri(endpoint + "/v1/chat/completions");
    }

    private static Uri BuildOllamaGenerateEndpoint(Uri chatEndpoint)
    {
        var builder = new UriBuilder(chatEndpoint)
        {
            Path = "/api/generate",
            Query = string.Empty,
            Fragment = string.Empty
        };
        return builder.Uri;
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

    private sealed class StreamingToolCall
    {
        public string Id { get; set; } = $"call-{Guid.NewGuid():N}";
        public string Name { get; set; } = string.Empty;
        public StringBuilder Arguments { get; } = new();

        public ToolCall ToToolCall()
        {
            var json = Arguments.Length == 0 ? "{}" : Arguments.ToString();
            try
            {
                using var document = JsonDocument.Parse(json);
                return new ToolCall(Id, Name, document.RootElement.Clone());
            }
            catch (JsonException)
            {
                using var empty = JsonDocument.Parse("{}");
                return new ToolCall(Id, Name, empty.RootElement.Clone());
            }
        }
    }
}
