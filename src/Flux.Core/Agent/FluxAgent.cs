namespace Flux.Core.Agent;

public sealed class FluxAgent(IAiProvider provider, IToolRegistry tools, ILogService log)
{
    private const int MaxTurns = 6;

    public async Task<AiAgentResult> RunAsync(
        string request,
        string relevantContext,
        IProgress<string>? textProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (!provider.IsConfigured)
        {
            return new AiAgentResult(
                "This needs AI interpretation, but no AI provider is configured. Open Settings to choose Cloud or Local mode.",
                Array.Empty<PendingToolCall>(),
                Array.Empty<ToolResult>());
        }

        var executed = new List<ToolResult>();
        var pending = new List<PendingToolCall>();
        string? previousResponseId = null;
        IReadOnlyList<ToolResult>? toolResults = null;
        var finalText = string.Empty;

        for (var turn = 0; turn < MaxTurns; turn++)
        {
            var turnRequest = new AiTurnRequest(
                request,
                relevantContext,
                tools.Definitions,
                previousResponseId,
                toolResults);
            var response = textProgress is not null && !LooksLikeActionRequest(request) && provider is IStreamingAiProvider streaming
                ? await streaming.CompleteStreamingAsync(turnRequest, textProgress, cancellationToken)
                : await provider.CompleteAsync(turnRequest, cancellationToken);

            previousResponseId = response.ResponseId;
            finalText = response.Text;

            if (response.ToolCalls.Count == 0)
            {
                break;
            }

            var automaticResults = new List<ToolResult>();
            foreach (var call in response.ToolCalls)
            {
                if (!tools.TryGet(call.Name, out var tool) || tool is null)
                {
                    var unknownResult = new ToolResult(call.Id, call.Name, false, "No action was performed.");
                    automaticResults.Add(unknownResult);
                    executed.Add(unknownResult);
                    continue;
                }

                if (tool.Definition.Permission >= PermissionLevel.Disruptive)
                {
                    pending.Add(new PendingToolCall(
                        call,
                        tool.Definition,
                        $"Allow Flux to run {tool.Definition.Name}? {tool.Definition.Description}"));
                    continue;
                }

                try
                {
                    var result = await tool.ExecuteAsync(call, cancellationToken);
                    automaticResults.Add(result);
                    executed.Add(result);
                }
                catch (Exception exception)
                {
                    log.Error($"Tool {call.Name} failed.", exception);
                    var failedResult = new ToolResult(call.Id, call.Name, false, "The action could not be completed.");
                    automaticResults.Add(failedResult);
                    executed.Add(failedResult);
                }
            }

            if (pending.Count > 0)
            {
                finalText = string.IsNullOrWhiteSpace(finalText)
                    ? "I’m ready to perform the following action after you confirm."
                    : finalText;
                break;
            }

            toolResults = automaticResults;
        }

        if (pending.Count == 0 && LooksLikeActionRequest(request) &&
            !executed.Any(result => result.Success && IsActionTool(result.Name)))
        {
            finalText = executed.FirstOrDefault(result => IsActionTool(result.Name))?.Output
                ?? "No action was performed.";
        }

        return new AiAgentResult(finalText, pending, executed);
    }

    private bool IsActionTool(string toolName) =>
        tools.TryGet(toolName, out var tool) && tool is not null && tool.Definition.Permission > PermissionLevel.ReadOnly;

    private static bool LooksLikeActionRequest(string request)
    {
        return System.Text.RegularExpressions.Regex.IsMatch(
            request,
            "^\\s*(?:please\\s+)?(?:(?:can|could|would|will)\\s+you\\s+)?(?:open|launch|start|run|close|kill|terminate|restart|create|make)\\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }
}
