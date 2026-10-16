namespace NetPI.Tools.Test;

/// <summary>
/// The <c>test</c> tool: a project says in <c>.netpi/tests.json</c> how its tests run and where they leave their results
/// (<c>{ "passed", "failed", "skipped", "failures": [{ "name", "message", "evidence"? }], "log"? }</c>); the tool runs the
/// command and answers with the counts and the failures, so no chat parses a console log.
/// </summary>
[NetPiPlugin("netpi.tools.test", Name = "Test tool", Description = "test: run the project's tests (.netpi/tests.json) and read the failures from its results file", Order = 28)]
public sealed class TestPlugin : INetPiPlugin
{
    public Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        context.Tools.Register(new TestTool());
        return Task.CompletedTask;
    }
}
