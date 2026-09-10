namespace Flux.Core.Agent;

public sealed class FluxAgent(IAiProvider provider, IToolRegistry tools, ILogService log)
{
    private const int MaxTurns = 6;

    public async Task<AiAgentResult> RunAsync(
        string request,
        string relevantContext,
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
            var response = await provider.CompleteAsync(new AiTurnRequest(
                request,
                relevantContext,
                tools.Definitions,
                previousResponseId,
                toolResults), cancellationToken);

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
                    automaticResults.Add(new ToolResult(call.Id, call.Name, false, "Flux refused an unknown tool."));
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
                    automaticResults.Add(new ToolResult(call.Id, call.Name, false, exception.Message));
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

        return new AiAgentResult(finalText, pending, executed);
    }
}

