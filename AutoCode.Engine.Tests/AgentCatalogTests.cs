using AutoCode.Engine.Agent;
using AutoCode.Engine.Backends;

namespace AutoCode.Engine.Tests;

// Harness metadata: mode/model sets per agent, and the wires the shell relies on.
[TestClass]
public sealed class AgentCatalogTests
{
    [TestMethod]
    public void BuiltinModes_MatchAgentModeWires()
    {
        var wires = AgentCatalog.ModesFor("builtin").Select(m => m.Wire).ToList();
        CollectionAssert.AreEquivalent(new[] { "default", "autocode", "planning", "admin" }, wires.ToArray());
        // Every builtin wire round-trips through the engine's mode parser.
        foreach (var wire in wires)
        {
            Assert.AreEqual(wire, AgentModeExtensions.Parse(wire).WireName());
        }
    }

    [TestMethod]
    public void ExternalHarnesses_HaveModes_AndDefaultsPreserveOriginalBehavior()
    {
        Assert.AreEqual(3, AgentCatalog.ModesFor("claude-code").Count);
        Assert.AreEqual(3, AgentCatalog.ModesFor("codex").Count);
        // Fresh sessions keep the pre-catalog fully-autonomous behavior.
        Assert.AreEqual("auto", AgentCatalog.DefaultWireFor("claude-code"));
        Assert.AreEqual("full-access", AgentCatalog.DefaultWireFor("codex"));
        Assert.AreEqual("default", AgentCatalog.DefaultWireFor("builtin"));
        // Manager dispatch overrides map to each harness's autonomous wire.
        Assert.AreEqual("autocode", AgentCatalog.AutoWireFor("builtin"));
        Assert.AreEqual("auto", AgentCatalog.AutoWireFor("claude-code"));
        Assert.AreEqual("full-access", AgentCatalog.AutoWireFor("codex"));
    }

    [TestMethod]
    public void Models_DefaultFirst_ForExternal_EmptyForBuiltin()
    {
        Assert.AreEqual(0, AgentCatalog.ModelsFor("builtin").Count);   // builtin uses ModelCatalog
        Assert.AreEqual("default", AgentCatalog.ModelsFor("claude-code")[0].Id);
        Assert.AreEqual("default", AgentCatalog.ModelsFor("codex")[0].Id);
    }
}
