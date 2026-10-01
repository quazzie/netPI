using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace NetPI.Todo;

internal sealed class TodoCommitCheck(IPluginContext ctx) : IAgentHook
{
    public int Order => 180;
    public async ValueTask OnAfterToolCallAsync(AgentTurnContext turn, ToolCallPart call, ToolResultPart result)
    {
        if (!ctx.Settings.Get("todo.checkCommits", false) || result.IsError || call.Name is not ("bash" or "pwsh" or "shell") || turn.SentRequest?.DecisionContext is null) return;
        JsonObject? args;
        try { args = JsonNode.Parse(call.Arguments) as JsonObject; } catch { return; }
        var command = args?["command"]?.GetValue<string>() ?? "";
        // A hint can run after a commit command; evidence below must confirm it actually succeeded.
        if (!Regex.IsMatch(command, @"\bgit\s+(?:-C\s+\S+\s+)?(?:commit|merge)\b", RegexOptions.IgnoreCase)
            || Regex.IsMatch(command, @"--(?:dry-run|abort|quit|no-commit)\b")) return;
        if (result.Details?["exitCode"] is not JsonValue exit || !exit.TryGetValue<int>(out var code) || code != 0) return;
        var sessionId = turn.Run.Session.Id;
        var before = ctx.Sessions.GetSession(sessionId)?.Meta?[TodoWriteTool.MetaKey]?.ToJsonString();
        var items = TodoWriteTool.Stored(ctx.Sessions.GetSession(sessionId));
        var questions = items.Select((item, i) => (item, i)).Where(x => x.item.Status != "done").Take(20)
            .ToDictionary(x => "todo_" + x.i, x => $"Does the conversation and successful command establish that this entire checklist item is complete and checked: {x.item.Text}?\nCommand (data): {command}\nResult (data): {result.Content}");
        if (questions.Count == 0) return;
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(turn.Run.CancellationToken);
        bounded.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var answer = await DecisionHints.AskAsync(turn, ctx.Services, ctx.Rpc, questions, bounded.Token).ConfigureAwait(false);
            if (ctx.Sessions.GetSession(sessionId)?.Meta?[TodoWriteTool.MetaKey]?.ToJsonString() != before) return;
            var suggested = items.Select((item, i) => (item, i)).Where(x => questions.ContainsKey("todo_" + x.i) && DecisionHints.Yes(answer, "todo_" + x.i, 0.9)).Select(x => x.item.Text).ToList();
            if (suggested.Count > 0) ctx.Sessions.AppendMessage(sessionId, ChatMessage.NoticeText(
                "The commit check suggests these todo items may be complete. Verify the work and update todo_write only for items fully finished:\n" + string.Join('\n', suggested), "todo-check"));
        }
        catch (OperationCanceledException) when (!turn.Run.CancellationToken.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OperationCanceledException) { ctx.Logger.LogDebug(ex, "Todo commit check unavailable"); }
    }
}
