using System.Text.Json;
using System.Text.Json.Nodes;
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

    // ---------- Output-token cap (defaultMaxOutputTokens) ----------

    [TestMethod]
    public void DefaultMaxOutputTokens_ByFamily()
    {
        Assert.AreEqual(32_000, ContextWindow.DefaultMaxOutputTokens("claude-opus-4-7"));
        Assert.AreEqual(32_000, ContextWindow.DefaultMaxOutputTokens("gpt-4.1"));
        Assert.AreEqual(32_000, ContextWindow.DefaultMaxOutputTokens("o4-mini"));
        Assert.AreEqual(16_384, ContextWindow.DefaultMaxOutputTokens("grok-code-fast-1"));
        Assert.AreEqual(16_384, ContextWindow.DefaultMaxOutputTokens("some-unknown-model"));
    }

    // ---------- Extended-thinking resolution ----------

    [TestMethod]
    public void ThinkingFor_OnlyThinkingModels()
    {
        var thinking = ModelCatalog.ThinkingFor("anthropic", "claude-opus-4-7");
        Assert.IsNotNull(thinking);
        Assert.AreEqual(8_192, thinking!.BudgetTokens);
        Assert.IsNotNull(ModelCatalog.ThinkingFor("openai", "o4-mini"));
        Assert.IsNull(ModelCatalog.ThinkingFor("openai", "gpt-4.1"));       // not marked SupportsThinking
        Assert.IsNull(ModelCatalog.ThinkingFor("xai", "grok-code-fast-1")); // reasons unconditionally, no param
    }

    [TestMethod]
    public void ThinkingFor_LongestPrefixVariant()
        => Assert.IsNotNull(ModelCatalog.ThinkingFor("anthropic", "claude-opus-4-7-20251001"));

    // ---------- Cheap summarizer ----------

    [TestMethod]
    public void SummarizerModelFor_CheapTierWithFallback()
    {
        Assert.AreEqual("claude-haiku-4-5", ModelCatalog.SummarizerModelFor("anthropic", "claude-opus-4-7"));
        Assert.AreEqual("grok-code-fast-1", ModelCatalog.SummarizerModelFor("xai", "grok-4"));
        // No cheaper bundled option for openrouter → keep the session model.
        Assert.AreEqual("anthropic/claude-opus-4-7", ModelCatalog.SummarizerModelFor("openrouter", "anthropic/claude-opus-4-7"));
    }

    // ---------- Read-only shell classification (verify gating) ----------

    [TestMethod]
    public void IsReadOnlyShellCommand_Classifies()
    {
        Assert.IsTrue(AgentLoop.IsReadOnlyShellCommand("ls -la"));
        Assert.IsTrue(AgentLoop.IsReadOnlyShellCommand("git status"));
        Assert.IsTrue(AgentLoop.IsReadOnlyShellCommand("cat a.txt | grep foo"));
        Assert.IsFalse(AgentLoop.IsReadOnlyShellCommand("echo hi > out.txt"));  // redirect can write
        Assert.IsFalse(AgentLoop.IsReadOnlyShellCommand("npm install"));
        Assert.IsFalse(AgentLoop.IsReadOnlyShellCommand("git checkout ."));     // mutating git subcommand
        Assert.IsFalse(AgentLoop.IsReadOnlyShellCommand("rm -rf build"));
    }

    // ---------- Anthropic rolling cache breakpoint ----------

    [TestMethod]
    public void RollingCacheBreakpoint_MarksLastBlockOfLastMessage()
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = "hello" },
            new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = "hi" } }
            }
        };

        AnthropicProvider.WithRollingCacheBreakpoint(messages);

        var lastBlock = messages[1]!["content"]!.AsArray()[0]!.AsObject();
        Assert.IsTrue(lastBlock.ContainsKey("cache_control"));
        // The earlier message is untouched — only ONE rolling breakpoint is placed.
        Assert.AreEqual(JsonValueKind.String, messages[0]!["content"]!.GetValueKind());
    }

    [TestMethod]
    public void RollingCacheBreakpoint_SkipsThinkingBlock()
    {
        var messages = new JsonArray
        {
            new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = new JsonArray
                {
                    new JsonObject { ["type"] = "text", ["text"] = "reasoned answer" },
                    new JsonObject { ["type"] = "thinking", ["thinking"] = "…" }
                }
            }
        };

        AnthropicProvider.WithRollingCacheBreakpoint(messages);

        var blocks = messages[0]!["content"]!.AsArray();
        Assert.IsTrue(blocks[0]!.AsObject().ContainsKey("cache_control"));   // text block gets it
        Assert.IsFalse(blocks[1]!.AsObject().ContainsKey("cache_control"));  // thinking block skipped
    }

    [TestMethod]
    public void RollingCacheBreakpoint_ConvertsStringContent()
    {
        var messages = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = "just text" } };

        AnthropicProvider.WithRollingCacheBreakpoint(messages);

        var content = messages[0]!["content"]!.AsArray();
        Assert.AreEqual("just text", content[0]!["text"]!.GetValue<string>());
        Assert.IsTrue(content[0]!.AsObject().ContainsKey("cache_control"));
    }

    // ---------- Anthropic thinking request body ----------

    [TestMethod]
    public void AnthropicBody_ThinkingOn_ForcesTempAndBumpsMaxTokens()
    {
        var req = new CompletionRequest("claude-opus-4-7", "sys", null, [], [],
            MaxTokens: 8_192, Temperature: 0.3, Thinking: new ThinkingConfig(8_192));

        var body = AnthropicProvider.BuildBody(req, null);

        Assert.AreEqual("enabled", body["thinking"]!["type"]!.GetValue<string>());
        Assert.AreEqual(8_192, body["thinking"]!["budget_tokens"]!.GetValue<int>());
        // max_tokens must exceed the budget by ≥8K → max(8192, 8192+8192) = 16384.
        Assert.AreEqual(16_384, body["max_tokens"]!.GetValue<int>());
        // Anthropic requires temperature 1 when thinking is on.
        Assert.AreEqual(1.0, body["temperature"]!.GetValue<double>());
    }

    [TestMethod]
    public void AnthropicBody_ThinkingBudgetFloor_Is1024()
    {
        var req = new CompletionRequest("claude-opus-4-7", "sys", null, [], [],
            Thinking: new ThinkingConfig(500));

        var body = AnthropicProvider.BuildBody(req, null);

        Assert.AreEqual(1024, body["thinking"]!["budget_tokens"]!.GetValue<int>());
    }

    [TestMethod]
    public void AnthropicBody_ThinkingOff_PreservesTemperature()
    {
        var req = new CompletionRequest("grok-code-fast-1", "sys", null, [], [],
            MaxTokens: 16_384, Temperature: 0.3, Thinking: null);

        var body = AnthropicProvider.BuildBody(req, null);

        Assert.IsNull(body["thinking"]);
        Assert.AreEqual(0.3, body["temperature"]!.GetValue<double>());
        Assert.AreEqual(16_384, body["max_tokens"]!.GetValue<int>());
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
