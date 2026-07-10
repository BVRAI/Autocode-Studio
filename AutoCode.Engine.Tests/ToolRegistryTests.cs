using AutoCode.Engine.Auth;
using AutoCode.Engine.Tools;

namespace AutoCode.Engine.Tests;

// The ToolDefinition.Mutating flag: registered tools that declare it must gate like the built-in
// mutating set (AgentLoop.GateFor consults ToolRegistry.IsMutating).
[TestClass]
public sealed class ToolRegistryTests
{
    [TestMethod]
    public void IsMutating_FlaggedInjectedTool_True()
    {
        var registry = new ToolRegistry(new AutocodeConfig());
        registry.Register(new FakeTool("fake_dispatch", mutating: true));

        Assert.IsTrue(registry.IsMutating("fake_dispatch"));
    }

    [TestMethod]
    public void IsMutating_UnflaggedAndUnknownTools_False()
    {
        var registry = new ToolRegistry(new AutocodeConfig());
        registry.Register(new FakeTool("fake_lookup", mutating: false));

        Assert.IsFalse(registry.IsMutating("fake_lookup"));
        Assert.IsFalse(registry.IsMutating("no_such_tool"));
        Assert.IsFalse(registry.IsMutating("read_file")); // built-ins don't set the flag (hardcoded set covers them)
    }

    private sealed class FakeTool(string name, bool mutating) : ITool
    {
        public ToolDefinition Definition { get; } = new(
            name,
            "test tool",
            ToolArgs.Schema("""{ "type": "object", "properties": {} }"""),
            Mutating: mutating);

        public Task<ToolResult> ExecuteAsync(Dictionary<string, object?> args, ToolExecutionContext context, CancellationToken cancellationToken)
            => Task.FromResult(new ToolResult("ok", "ok"));
    }
}
