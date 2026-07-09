using AutoCode.Engine.Session;
using AutoCode.Engine.Tools;

namespace AutoCode.Engine.Tests;

// Exercises the real git worktree/commit/merge flow against a throwaway repo (no app, no LLM).
[TestClass]
public sealed class GitServiceTests
{
    [TestMethod]
    public async Task Worktree_Commit_Merge_RoundTrip()
    {
        using var temp = new TempDir();
        var repo = temp.Root;

        await Git(repo, "init");
        await Git(repo, "config user.email test@example.com");
        await Git(repo, "config user.name Test");
        File.WriteAllText(Path.Combine(repo, "a.txt"), "base\n");
        await Git(repo, "add -A");
        await Git(repo, "commit -m base");

        // RepoRoot + current branch
        var root = await GitService.RepoRootAsync(repo);
        Assert.IsNotNull(root);
        var baseBranch = await GitService.CurrentBranchAsync(repo);
        Assert.IsFalse(string.IsNullOrWhiteSpace(baseBranch));

        // Create an isolated worktree on its own branch.
        var worktree = Path.Combine(Path.GetTempPath(), "autocode-wt-" + Guid.NewGuid().ToString("N"));
        var create = await GitService.CreateWorktreeAsync(repo, worktree, "autocode/test");
        Assert.IsTrue(create.Ok, create.Message);
        Assert.IsTrue(Directory.Exists(worktree));

        // Edit only inside the worktree, then auto-commit.
        File.WriteAllText(Path.Combine(worktree, "feature.txt"), "feature work\n");
        var commit = await GitService.CommitAllAsync(worktree, "add feature");
        Assert.IsTrue(commit.Ok, commit.Message);

        // The main working tree must be untouched until merge.
        Assert.IsFalse(File.Exists(Path.Combine(repo, "feature.txt")));

        // A second commit with no changes is a benign no-op.
        var noop = await GitService.CommitAllAsync(worktree, "nothing");
        Assert.IsTrue(noop.Ok, noop.Message);

        // Merge the branch back into the base; the change now lands in the main tree.
        var merge = await GitService.MergeAsync(repo, "autocode/test");
        Assert.IsTrue(merge.Ok, merge.Message);
        Assert.IsTrue(File.Exists(Path.Combine(repo, "feature.txt")));

        // Cleanup removes the worktree directory.
        var remove = await GitService.RemoveWorktreeAsync(repo, worktree, "autocode/test");
        Assert.IsTrue(remove.Ok, remove.Message);
        Assert.IsFalse(Directory.Exists(worktree));
    }

    [TestMethod]
    public async Task ChangedFiles_Porcelain_And_DiffVsBase()
    {
        using var temp = new TempDir();
        var repo = temp.Root;

        await Git(repo, "init");
        await Git(repo, "config user.email test@example.com");
        await Git(repo, "config user.name Test");
        File.WriteAllText(Path.Combine(repo, "a.txt"), "base\n");
        await Git(repo, "add -A");
        await Git(repo, "commit -m base");

        // Uncommitted working-tree changes (no base branch → porcelain).
        File.WriteAllText(Path.Combine(repo, "b.txt"), "new\n");
        File.WriteAllText(Path.Combine(repo, "a.txt"), "edited\n");
        var dirty = await GitService.ChangedFilesAsync(repo, null);
        Assert.AreEqual(2, dirty.Count, string.Join(",", dirty.Select(f => $"{f.Status}:{f.Path}")));
        Assert.IsTrue(dirty.Any(f => f.Path == "b.txt" && f.Status == "A"));
        Assert.IsTrue(dirty.Any(f => f.Path == "a.txt" && f.Status == "M"));

        // Net diff vs base on a worktree branch (committed-on-branch changes).
        var baseBranch = await GitService.CurrentBranchAsync(repo);
        await Git(repo, "checkout -- a.txt");
        File.Delete(Path.Combine(repo, "b.txt"));
        var worktree = Path.Combine(Path.GetTempPath(), "autocode-wt-" + Guid.NewGuid().ToString("N"));
        await GitService.CreateWorktreeAsync(repo, worktree, "autocode/changes");
        File.WriteAllText(Path.Combine(worktree, "feature.txt"), "feat\n");
        await GitService.CommitAllAsync(worktree, "add feature");

        var net = await GitService.ChangedFilesAsync(worktree, baseBranch);
        Assert.IsTrue(net.Any(f => f.Path == "feature.txt" && f.Status == "A"), string.Join(",", net.Select(f => f.Path)));

        await GitService.RemoveWorktreeAsync(repo, worktree, "autocode/changes");

        // A non-git directory yields an empty list, not an error.
        using var plain = new TempDir();
        var none = await GitService.ChangedFilesAsync(plain.Root, null);
        Assert.AreEqual(0, none.Count);
    }

    [TestMethod]
    public async Task Init_WithLocalIdentity_CommitSucceeds()
    {
        using var temp = new TempDir();
        var repo = temp.Root;

        // Init sets a repo-local identity so commits work even with no global git config
        // (the ecosystem manifest repo relies on this).
        var init = await GitService.InitAsync(repo, "AutoCode Studio", "autocode@local");
        Assert.IsTrue(init.Ok, init.Message);

        var name = await ToolArgs.RunProcessAsync($"git -C \"{repo}\" config user.name", repo, 30_000, default);
        Assert.AreEqual("AutoCode Studio", name.Stdout.Trim());
        var email = await ToolArgs.RunProcessAsync($"git -C \"{repo}\" config user.email", repo, 30_000, default);
        Assert.AreEqual("autocode@local", email.Stdout.Trim());

        File.WriteAllText(Path.Combine(repo, "manifest.json"), "{}");
        var commit = await GitService.CommitAllAsync(repo, "Initialize ecosystem manifest");
        Assert.IsTrue(commit.Ok, commit.Message);

        // Re-init on an existing repo is a benign no-op.
        var reinit = await GitService.InitAsync(repo);
        Assert.IsTrue(reinit.Ok, reinit.Message);
    }

    private static async Task Git(string dir, string args)
    {
        var r = await ToolArgs.RunProcessAsync($"git -C \"{dir}\" {args}", dir, 30_000, default);
        Assert.AreEqual(0, r.ExitCode, $"git {args} failed: {r.Stderr}{r.Stdout}");
    }
}
