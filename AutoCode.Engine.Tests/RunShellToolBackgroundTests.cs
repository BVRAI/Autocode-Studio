using System.Diagnostics;
using AutoCode.Engine.Agent;
using AutoCode.Engine.Tools;

namespace AutoCode.Engine.Tests;

// The background-process registry is keyed by session id so concurrent workspaces don't share
// (or kill) each other's dev servers. StopBackgroundProcesses(sessionId) must stop only that
// session's processes; StopBackgroundProcesses() stops everything (app exit).
[TestClass]
public sealed class RunShellToolBackgroundTests
{
    [TestMethod]
    public async Task StopBackgroundProcesses_IsScopedToTheSession()
    {
        using var t = new TempDir();
        var tool = new RunShellTool();

        var pidA = await StartBackgroundPingAsync(tool, t.Root, "session-a");
        var pidB = await StartBackgroundPingAsync(tool, t.Root, "session-b");

        try
        {
            RunShellTool.StopBackgroundProcesses("session-a");
            Assert.IsTrue(HasExited(pidA), "session-a's background process should be stopped");
            Assert.IsFalse(HasExited(pidB), "session-b's background process must survive session-a's stop");

            RunShellTool.StopBackgroundProcesses();
            Assert.IsTrue(HasExited(pidB), "stop-all should stop the remaining background process");
        }
        finally
        {
            RunShellTool.StopBackgroundProcesses();
        }
    }

    private static async Task<int> StartBackgroundPingAsync(RunShellTool tool, string root, string sessionId)
    {
        var context = new ToolExecutionContext
        {
            Session = new SessionContext(
                sessionId,
                root,
                root,
                root,
                new ModelConfig("anthropic", "test"),
                DateTimeOffset.Now,
                AgentMode.Autocode),
        };

        var command = OperatingSystem.IsWindows() ? "ping -n 60 127.0.0.1" : "sleep 60";
        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["command"] = command, ["background"] = true },
            context,
            CancellationToken.None);

        Assert.IsFalse(result.IsError, $"background start failed: {result.Content}");
        var pid = Convert.ToInt32(result.Metadata!["pid"]);
        Assert.IsFalse(HasExited(pid), "background process should be running after start");
        return pid;
    }

    private static bool HasExited(int pid)
    {
        try
        {
            return Process.GetProcessById(pid).HasExited;
        }
        catch (ArgumentException)
        {
            return true; // no such process — already gone
        }
    }
}
