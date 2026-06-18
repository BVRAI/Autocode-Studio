using AutoCode.Engine.Tools;

namespace AutoCode.Engine.Session;

/// <summary>Outcome of a git command — Ok plus the combined stdout/stderr for surfacing to the user.</summary>
public sealed record GitResult(bool Ok, string Message);

/// <summary>One changed file for the review surface. Status is a single letter: A | M | D | R | C.</summary>
public sealed record ChangedFile(string Status, string Path);

/// <summary>
/// Thin git wrapper used for per-workspace isolation (worktree + branch). Portable: shells out via
/// <see cref="ToolArgs.RunProcessAsync"/>, no platform/UI dependency. All commands use `git -C &lt;dir&gt;`
/// so they target the right repo regardless of process working directory.
/// </summary>
public static class GitService
{
    private const int TimeoutMs = 30_000;

    /// <summary>Absolute path of the repo's top level, or null when <paramref name="root"/> isn't a git repo.</summary>
    public static async Task<string?> RepoRootAsync(string root, CancellationToken cancellationToken = default)
    {
        var r = await RunGitAsync(root, "rev-parse --show-toplevel", cancellationToken).ConfigureAwait(false);
        if (!r.Ok || string.IsNullOrWhiteSpace(r.Message))
        {
            return null;
        }

        try { return Path.GetFullPath(r.Message.Trim()); }
        catch { return null; }
    }

    public static async Task<string?> CurrentBranchAsync(string repoRoot, CancellationToken cancellationToken = default)
    {
        var r = await RunGitAsync(repoRoot, "rev-parse --abbrev-ref HEAD", cancellationToken).ConfigureAwait(false);
        return r.Ok ? r.Message.Trim() : null;
    }

    public static Task<GitResult> CreateWorktreeAsync(string repoRoot, string worktreePath, string branch, CancellationToken cancellationToken = default)
        => RunGitAsync(repoRoot, $"worktree add -b {Quote(branch)} {Quote(worktreePath)}", cancellationToken);

    /// <summary>Stage everything and commit. "nothing to commit" is treated as a benign no-op.</summary>
    public static async Task<GitResult> CommitAllAsync(string worktreePath, string message, CancellationToken cancellationToken = default)
    {
        var add = await RunGitAsync(worktreePath, "add -A", cancellationToken).ConfigureAwait(false);
        if (!add.Ok)
        {
            return add;
        }

        var commit = await RunGitAsync(worktreePath, $"commit -m {Quote(message)}", cancellationToken).ConfigureAwait(false);
        if (!commit.Ok && commit.Message.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase))
        {
            return new GitResult(true, "no changes to commit");
        }

        return commit;
    }

    /// <summary>Merge <paramref name="branch"/> into the main working tree's current branch.</summary>
    public static async Task<GitResult> MergeAsync(string repoRoot, string branch, CancellationToken cancellationToken = default)
    {
        var r = await RunGitAsync(repoRoot, $"merge --no-ff {Quote(branch)}", cancellationToken).ConfigureAwait(false);
        if (!r.Ok && r.Message.Contains("CONFLICT", StringComparison.OrdinalIgnoreCase))
        {
            await RunGitAsync(repoRoot, "merge --abort", cancellationToken).ConfigureAwait(false);
            return new GitResult(false, "Merge conflict — left your tree clean (merge aborted). Resolve manually:\n" + r.Message);
        }

        return r;
    }

    public static async Task<GitResult> RemoveWorktreeAsync(string repoRoot, string worktreePath, string? branch, CancellationToken cancellationToken = default)
    {
        var rm = await RunGitAsync(repoRoot, $"worktree remove --force {Quote(worktreePath)}", cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(branch))
        {
            await RunGitAsync(repoRoot, $"branch -D {Quote(branch)}", cancellationToken).ConfigureAwait(false);
        }

        return rm;
    }

    /// <summary>
    /// Files changed in this session's working dir. For a worktree session pass its
    /// <paramref name="baseBranch"/> → returns net changes vs base (committed-on-branch + uncommitted);
    /// otherwise returns the uncommitted working-tree changes. Non-git dirs return an empty list.
    /// </summary>
    public static async Task<IReadOnlyList<ChangedFile>> ChangedFilesAsync(string dir, string? baseBranch, CancellationToken cancellationToken = default)
    {
        var useDiff = !string.IsNullOrWhiteSpace(baseBranch);
        var r = useDiff
            ? await RunGitAsync(dir, $"diff --name-status {Quote(baseBranch!)}", cancellationToken).ConfigureAwait(false)
            : await RunGitAsync(dir, "status --porcelain", cancellationToken).ConfigureAwait(false);
        if (!r.Ok)
        {
            return Array.Empty<ChangedFile>();
        }

        var list = new List<ChangedFile>();
        foreach (var raw in r.Message.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (useDiff)
            {
                // name-status: "M\tpath"  |  "R100\told\tnew"
                var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    list.Add(new ChangedFile(NormalizeStatus(parts[0]), parts[^1].Trim()));
                }
            }
            else
            {
                // porcelain "<XY> <path>" | "R <old> -> <new>" — split on the first whitespace run so
                // it survives the leading-space trim RunGitAsync applies to the combined output.
                var parts = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2)
                {
                    var path = parts[1].Trim();
                    var arrow = path.IndexOf(" -> ", StringComparison.Ordinal);
                    if (arrow >= 0)
                    {
                        path = path[(arrow + 4)..];
                    }

                    list.Add(new ChangedFile(NormalizeStatus(parts[0]), path));
                }
            }
        }

        return list;
    }

    private static string NormalizeStatus(string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return "M";
        }

        return char.ToUpperInvariant(s[0]) switch
        {
            'A' => "A",
            'D' => "D",
            'R' => "R",
            'C' => "C",
            '?' => "A", // untracked → shown as added
            _ => "M",
        };
    }

    private static async Task<GitResult> RunGitAsync(string dir, string args, CancellationToken cancellationToken)
    {
        var res = await ToolArgs.RunProcessAsync($"git -C {Quote(dir)} {args}", dir, TimeoutMs, cancellationToken).ConfigureAwait(false);
        var ok = !res.TimedOut && res.ExitCode == 0;
        var message = string.IsNullOrWhiteSpace(res.Stdout)
            ? res.Stderr
            : string.IsNullOrWhiteSpace(res.Stderr) ? res.Stdout : $"{res.Stdout}\n{res.Stderr}";
        return new GitResult(ok, (message ?? "").Trim());
    }

    private static string Quote(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";
}
