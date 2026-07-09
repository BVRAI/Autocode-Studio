using AutoCode.Engine.Agent;
using AutoCode.Engine.Llm;
using AutoCode.Engine.Session;
using AutoCode.Engine.Tools;

namespace AutoCode.Engine.Tests;

// Parity checks for the agentic improvements ported from the AutoCode TUI
// (verify-coverage, pricing, context-window/compaction thresholds, loop detection).
[TestClass]
public sealed class ParityTests
{
    // ---------- Verification.Infer ----------

    [TestMethod]
    public void Infer_Maven_UsesMvn()
    {
        using var t = new TempDir();
        File.WriteAllText(Path.Combine(t.Root, "pom.xml"), "<project/>");
        Assert.AreEqual("mvn -q test", Verification.Infer(t.Root));
    }

    [TestMethod]
    public void Infer_Gradle_UsesGradlew()
    {
        using var t = new TempDir();
        File.WriteAllText(Path.Combine(t.Root, "gradlew"), "#!/bin/sh");
        File.WriteAllText(Path.Combine(t.Root, "build.gradle"), "");
        Assert.AreEqual("./gradlew test", Verification.Infer(t.Root));
    }

    [TestMethod]
    public void Infer_Cpp_UsesCmake()
    {
        using var t = new TempDir();
        File.WriteAllText(Path.Combine(t.Root, "CMakeLists.txt"), "cmake_minimum_required(VERSION 3.10)");
        Assert.AreEqual("cmake -B build && cmake --build build", Verification.Infer(t.Root));
    }

    [TestMethod]
    public void Infer_PythonTestsDirWithoutTestFiles_IsNull()
    {
        using var t = new TempDir();
        File.WriteAllText(Path.Combine(t.Root, "main.py"), "print('hi')");
        Directory.CreateDirectory(Path.Combine(t.Root, "tests")); // empty -> not enough evidence
        Assert.IsNull(Verification.Infer(t.Root));
    }

    [TestMethod]
    public void Infer_PythonWithTestFile_UsesPytest()
    {
        using var t = new TempDir();
        Directory.CreateDirectory(Path.Combine(t.Root, "tests"));
        File.WriteAllText(Path.Combine(t.Root, "tests", "test_main.py"), "def test_x(): assert True");
        Assert.AreEqual("pytest", Verification.Infer(t.Root));
    }

    [TestMethod]
    public void Infer_Rust_ConditionalOnTestsDir()
    {
        using var noTests = new TempDir();
        File.WriteAllText(Path.Combine(noTests.Root, "Cargo.toml"), "[package]");
        Assert.AreEqual("cargo check", Verification.Infer(noTests.Root));

        using var withTests = new TempDir();
        File.WriteAllText(Path.Combine(withTests.Root, "Cargo.toml"), "[package]");
        Directory.CreateDirectory(Path.Combine(withTests.Root, "tests"));
        Assert.AreEqual("cargo test", Verification.Infer(withTests.Root));
    }

    [TestMethod]
    public void Infer_Go_ConditionalOnTestFiles()
    {
        using var noTests = new TempDir();
        File.WriteAllText(Path.Combine(noTests.Root, "go.mod"), "module x");
        Assert.AreEqual("go build ./...", Verification.Infer(noTests.Root));

        using var withTests = new TempDir();
        File.WriteAllText(Path.Combine(withTests.Root, "go.mod"), "module x");
        File.WriteAllText(Path.Combine(withTests.Root, "main_test.go"), "package x");
        Assert.AreEqual("go test ./...", Verification.Infer(withTests.Root));
    }

    // ---------- Pricing ----------

    [TestMethod]
    public void EstimateCost_Grok_InputOutput()
    {
        var cost = Pricing.EstimateCost(new CompletionUsage(1_000_000, 1_000_000), "xai", "grok-code-fast-1");
        Assert.AreEqual(1.7, cost, 1e-9); // 0.2 in + 1.5 out
    }

    [TestMethod]
    public void RateFor_LongestPrefixWins()
    {
        var rate = Pricing.RateFor("anthropic", "claude-opus-4-7-20251001");
        Assert.IsNotNull(rate);
        Assert.AreEqual(15, rate!.InputPerM); // the 4-7 row, not bare claude-opus-4
        Assert.AreEqual(1.5, rate.CacheReadPerM);
    }

    [TestMethod]
    public void EstimateCost_UnknownModel_IsZero()
        => Assert.AreEqual(0, Pricing.EstimateCost(new CompletionUsage(1000, 1000), "nope", "mystery"));

    // ---------- ContextWindow ----------

    [TestMethod]
    public void ContextWindow_LongContextVariant_IsOneMillion()
        => Assert.AreEqual(1_000_000, ContextWindow.ContextWindowFor("anthropic", "claude-opus-4-8[1m]"));

    [TestMethod]
    public void ContextWindow_Grok_IsTwoHundredK()
        => Assert.AreEqual(200_000, ContextWindow.ContextWindowFor("xai", "grok-code-fast-1"));

    [TestMethod]
    public void ShouldAutoCompact_AtThreshold()
    {
        Assert.IsTrue(ContextWindow.ShouldAutoCompact(170_000, "xai", "grok-code-fast-1")); // >= 160k
        Assert.IsFalse(ContextWindow.ShouldAutoCompact(150_000, "xai", "grok-code-fast-1"));
        Assert.IsFalse(ContextWindow.ShouldAutoCompact(0, "xai", "grok-code-fast-1"));
    }

    [TestMethod]
    public void ShouldMaskObservations_AtEarlierThreshold()
    {
        Assert.IsTrue(ContextWindow.ShouldMaskObservations(130_000, "xai", "grok-code-fast-1")); // >= 120k
        Assert.IsFalse(ContextWindow.ShouldMaskObservations(110_000, "xai", "grok-code-fast-1"));
    }

    // ---------- Loop detection ----------

    [TestMethod]
    public void DetectLoop_ReturnsToolAtThreshold()
    {
        Assert.AreEqual("grep", AgentLoop.DetectLoop(["grep:{}", "grep:{}", "grep:{}"], 3));
        Assert.IsNull(AgentLoop.DetectLoop(["grep:{}", "grep:{}"], 3));
    }

    [TestMethod]
    public void StableStringify_OrderAndWhitespaceInsensitive()
    {
        var a = AgentLoop.StableStringify(new Dictionary<string, object?> { ["b"] = 1, ["a"] = "  x   y  " });
        var b = AgentLoop.StableStringify(new Dictionary<string, object?> { ["a"] = "x y", ["b"] = 1 });
        Assert.AreEqual(a, b);
    }

    [TestMethod]
    public void MaskOldToolResults_MasksOnlyOldLargeResults()
    {
        var old = new string('x', 2500);
        var recent = new string('y', 2500);
        var conversation = new List<AgentMessage>
        {
            AgentMessage.User("first"),
            AgentMessage.User(new ContentBlock[] { new ToolResultBlock("t1", old) }),
            AgentMessage.User("middle"),
            AgentMessage.User(new ContentBlock[] { new ToolResultBlock("t2", recent), new ThinkingBlock("keep") }),
            AgentMessage.User("latest")
        };

        var masked = AgentLoop.MaskOldToolResults(conversation, keepPairs: 2);

        Assert.AreEqual(1, masked);
        Assert.AreNotEqual(old, ((ToolResultBlock)conversation[1].Blocks![0]).Content);
        Assert.AreEqual(recent, ((ToolResultBlock)conversation[3].Blocks![0]).Content);
        Assert.IsInstanceOfType(conversation[3].Blocks![1], typeof(ThinkingBlock));
    }

    // ---------- RunShell output trimming ----------

    [TestMethod]
    public void RunShellTrimOutput_PreservesTailAndStderr()
    {
        var stdoutText = new string('x', 80_000) + "TAIL-SENTINEL";
        var stdout = new CapturedStream(stdoutText, "", stdoutText.Length, stdoutText.Length);
        var stderr = new CapturedStream("ERR-SENTINEL", "", "ERR-SENTINEL".Length, "ERR-SENTINEL".Length);

        var trimmed = RunShellTool.TrimOutput(stdout, stderr);

        StringAssert.Contains(trimmed.Content, "chars omitted");
        StringAssert.Contains(trimmed.Content, "TAIL-SENTINEL");
        StringAssert.Contains(trimmed.Content, "ERR-SENTINEL");
        Assert.IsTrue(trimmed.StdoutTruncated);
        Assert.IsFalse(trimmed.StderrTruncated);
    }

    // ---------- Scoped verification ----------

    [TestMethod]
    public void ScopeInferredCommand_VitestMapsSrcToTestMirror()
    {
        using var t = new TempDir();
        File.WriteAllText(Path.Combine(t.Root, "package.json"), """{"scripts":{"test":"vitest run"},"devDependencies":{"vitest":"^1"}}""");
        Directory.CreateDirectory(Path.Combine(t.Root, "test", "agent"));
        File.WriteAllText(Path.Combine(t.Root, "test", "agent", "Verify.test.ts"), "");

        var scoped = Verification.ScopeInferredCommand(t.Root, "npm test", ["src/agent/Verify.ts"]);

        Assert.IsTrue(scoped.IsScoped);
        Assert.AreEqual("npx vitest run test/agent/Verify.test.ts", scoped.Command);
    }

    [TestMethod]
    public void ResolvePlan_RetainsFullCommandWhenScoped()
    {
        using var t = new TempDir();
        File.WriteAllText(Path.Combine(t.Root, "go.mod"), "module x");
        File.WriteAllText(Path.Combine(t.Root, "main_test.go"), "package main");

        var plan = Verification.ResolvePlan(t.Root, null, [], ["pkg/a/x.go"]);

        Assert.IsNotNull(plan);
        Assert.AreEqual("go test ./pkg/a/...", plan!.Command);
        Assert.AreEqual("go test ./...", plan.FullCommand);
        Assert.AreEqual("inferred-scoped", plan.Source);
    }

    // ---------- Import graph / file_deps ----------

    [TestMethod]
    public void ImportGraph_ResolvesNodeNextImport()
    {
        var files = new HashSet<string>(StringComparer.Ordinal) { "src/a.ts", "src/core.ts" };
        Assert.AreEqual("src/core.ts", ImportGraphBuilder.ResolveImport("./core.js", "src/a.ts", files));
    }

    [TestMethod]
    public async Task FileDepsTool_ListsImportersAndImports()
    {
        using var t = new TempDir();
        Directory.CreateDirectory(Path.Combine(t.Root, "src"));
        File.WriteAllText(Path.Combine(t.Root, "src", "core.ts"), "export const core = 1;");
        File.WriteAllText(Path.Combine(t.Root, "src", "a.ts"), "import { core } from './core.js';\nexport const a = core;");
        var tool = new FileDepsTool();

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["path"] = "src/core.ts" },
            Context(t),
            CancellationToken.None);

        Assert.IsFalse(result.IsError);
        StringAssert.Contains(result.Content, "imported by (1):");
        StringAssert.Contains(result.Content, "src/a.ts");
    }

    // ---------- Syntax gate ----------

    [TestMethod]
    public async Task SyntaxGate_RevertsBrokenJsonCreate()
    {
        using var t = new TempDir();
        SyntaxGate.ResetForTests();
        var tool = new WriteFileTool();

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["path"] = "broken.json", ["content"] = "{ nope" },
            Context(t),
            CancellationToken.None);

        Assert.IsTrue(result.IsError);
        Assert.IsFalse(File.Exists(Path.Combine(t.Root, "broken.json")));
    }

    private static ToolExecutionContext Context(TempDir t) =>
        new()
        {
            Session = new SessionContext(
                "test",
                t.Root,
                t.Root,
                t.Root,
                new ModelConfig("xai", "grok-code-fast-1"),
                DateTimeOffset.Now,
                AgentMode.Autocode),
            Checkpoint = new CheckpointStore(t.Root),
            ConfirmAsync = (_, _) => Task.FromResult(true)
        };
}

internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Root = Path.Combine(Path.GetTempPath(), "autocode-parity-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { /* ignore */ }
    }
}
