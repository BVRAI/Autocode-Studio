using AutoCode.Engine.Agent;

namespace AutoCode.Engine.Tests;

// System-prompt assembly: the host-injected SystemAppendix must appear (and only appear) when set.
[TestClass]
public sealed class PromptBuilderTests
{
    [TestMethod]
    public void Build_WithSystemAppendix_AppendsHostBriefing()
    {
        using var temp = new TempDir();
        var context = MakeContext(temp.Root) with { SystemAppendix = "You are part of workspace group Alpha." };

        var (system, _) = PromptBuilder.Build(context, ["read_file"]);

        StringAssert.Contains(system, "# Host briefing");
        StringAssert.Contains(system, "You are part of workspace group Alpha.");
    }

    [TestMethod]
    public void Build_WithoutSystemAppendix_HasNoHostBriefingSection()
    {
        using var temp = new TempDir();

        var (system, _) = PromptBuilder.Build(MakeContext(temp.Root), ["read_file"]);

        Assert.IsFalse(system.Contains("# Host briefing"));
    }

    private static SessionContext MakeContext(string root) => new(
        "test-session",
        root,
        root,
        root,
        new ModelConfig("anthropic", "claude-test"),
        DateTimeOffset.Now,
        AgentMode.Default);
}
