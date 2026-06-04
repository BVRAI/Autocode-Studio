using AutoCode.Engine.Agent;
using AutoCode.Engine.Auth;
using AutoCode.Engine.Safety;
using AutoCode.Engine.Session;
using AutoCode.Engine.Tools;

namespace AutoCode.Engine.Tests;

[TestClass]
public sealed class EngineTests
{
    [TestMethod]
    public void ResolveInsideRoot_BlocksEscapes()
    {
        using var temp = new TempProject();
        Assert.ThrowsException<PathSafetyException>(() => PathSafety.ResolveInsideRoot(temp.Root, "..\\outside.txt"));
    }

    [TestMethod]
    public void SafetyPolicy_BlocksDestructiveOutOfRootPath()
    {
        using var temp = new TempProject();
        var verdict = SafetyPolicy.Classify("del ..\\outside.txt", temp.Root);
        Assert.AreEqual(SafetyKind.Block, verdict.Kind);
    }

    [TestMethod]
    public async Task EditFile_ReplacesUniqueText()
    {
        using var temp = new TempProject();
        var file = Path.Combine(temp.Root, "hello.txt");
        File.WriteAllText(file, "hello old value");
        var tool = new EditFileTool();
        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["path"] = "hello.txt",
                ["old_text"] = "old",
                ["new_text"] = "new"
            },
            temp.Context(),
            CancellationToken.None);

        Assert.IsFalse(result.IsError);
        Assert.AreEqual("hello new value", File.ReadAllText(file));
    }

    [TestMethod]
    public async Task WriteFile_CreateOnlyRefusesExistingFile()
    {
        using var temp = new TempProject();
        File.WriteAllText(Path.Combine(temp.Root, "existing.txt"), "first");
        var tool = new WriteFileTool();
        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["path"] = "existing.txt",
                ["content"] = "second"
            },
            temp.Context(),
            CancellationToken.None);

        Assert.IsTrue(result.IsError);
        Assert.AreEqual("first", File.ReadAllText(Path.Combine(temp.Root, "existing.txt")));
    }

    [TestMethod]
    public async Task GlobTool_FindsNestedFiles()
    {
        using var temp = new TempProject();
        Directory.CreateDirectory(Path.Combine(temp.Root, "src"));
        File.WriteAllText(Path.Combine(temp.Root, "src", "Program.cs"), "class Program {}");
        var tool = new GlobTool();
        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["pattern"] = "src/**/*.cs" },
            temp.Context(),
            CancellationToken.None);

        Assert.IsFalse(result.IsError);
        StringAssert.Contains(result.Content, "src/Program.cs");
    }
}

internal sealed class TempProject : IDisposable
{
    public TempProject()
    {
        Root = Path.Combine(Path.GetTempPath(), "autocode-gui-tests", Guid.NewGuid().ToString("N"));
        Session = Path.Combine(Root, ".session");
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Session);
    }

    public string Root { get; }

    public string Session { get; }

    public ToolExecutionContext Context() =>
        new()
        {
            Session = new SessionContext(
                "test",
                Root,
                Session,
                Session,
                new ModelConfig("openai", "gpt-5.1"),
                DateTimeOffset.Now,
                AgentMode.Autocode),
            Checkpoint = new CheckpointStore(Session),
            ConfirmAsync = (_, _) => Task.FromResult(true)
        };

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch
        {
            // Ignore cleanup failures.
        }
    }
}
