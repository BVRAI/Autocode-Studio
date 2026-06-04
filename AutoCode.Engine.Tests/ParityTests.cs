using AutoCode.Engine.Agent;
using AutoCode.Engine.Llm;

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
