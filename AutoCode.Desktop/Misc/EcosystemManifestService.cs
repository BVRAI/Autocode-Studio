using System.IO;
using System.Text.Json;
using AutoCode.Engine.Session;

namespace AutoCode.Desktop.Misc;

/// <summary>
/// Creates and maintains an ecosystem's on-disk manifest git repo — the agent-facing artifact:
/// manifest.json (membership, always rewritten), checklist.md / contract/ / design-tokens/
/// (templates written once, never clobbered), all committed with a repo-local identity so commits
/// work on machines with no global git config. Everything is best-effort: the registry
/// (EcosystemIndex) is the UI's source of truth and never depends on this repo or git succeeding.
/// </summary>
public static class EcosystemManifestService
{
    private const string CommitName = "AutoCode Studio";
    private const string CommitEmail = "autocode@local";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Idempotent: creates the repo + template files on first call, rewrites manifest.json
    /// (and commits) on membership changes, and recreates everything if the user deleted the dir.</summary>
    public static async Task EnsureRepoAsync(EcosystemRecord eco)
    {
        bool firstTime;
        try
        {
            Directory.CreateDirectory(eco.ManifestRoot);
            firstTime = !Directory.Exists(Path.Combine(eco.ManifestRoot, ".git"));

            WriteManifest(eco);
            WriteIfMissing(Path.Combine(eco.ManifestRoot, "checklist.md"), ChecklistTemplate(eco.Name));
            WriteIfMissing(Path.Combine(eco.ManifestRoot, "contract", "data-contract.md"), ContractTemplate(eco.Name));
            WriteIfMissing(Path.Combine(eco.ManifestRoot, "design-tokens", "tokens.json"), TokensTemplate);
        }
        catch
        {
            return; // Filesystem trouble -> the registry still holds the truth; retry on next change.
        }

        try
        {
            if (firstTime)
            {
                await GitService.InitAsync(eco.ManifestRoot, CommitName, CommitEmail);
            }

            await GitService.CommitAllAsync(
                eco.ManifestRoot,
                firstTime ? "Initialize ecosystem manifest" : "Update ecosystem members");
        }
        catch
        {
            // Git is best-effort here (e.g. git not installed) — the files above still exist.
        }
    }

    /// <summary>Append a member agent's milestone report to the manifest repo's reports.md — the
    /// durable log behind the live feed (the future manager agent reads it for history). Best-effort,
    /// no per-report commit: the repo's next membership commit picks the file up.</summary>
    public static async Task AppendReportAsync(EcosystemRecord eco, string member, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(eco.ManifestRoot);
            var path = Path.Combine(eco.ManifestRoot, "reports.md");
            var header = File.Exists(path)
                ? ""
                : $"# {eco.Name} — reports\n\nMilestones reported by member agents (newest last).\n\n";
            var line = $"- [{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm}Z] {member}: {message.Replace('\r', ' ').Replace('\n', ' ').Trim()}\n";
            await File.AppendAllTextAsync(path, header + line);
        }
        catch
        {
            // Best-effort: a failed append never disturbs the session that reported.
        }
    }

    private static void WriteManifest(EcosystemRecord eco)
    {
        var manifest = new
        {
            id = eco.Id,
            name = eco.Name,
            createdAt = eco.CreatedAt,
            members = eco.MemberRoots
                .Select(root => new { name = LeafName(root), root })
                .ToList(),
        };
        File.WriteAllText(Path.Combine(eco.ManifestRoot, "manifest.json"), JsonSerializer.Serialize(manifest, Options));
    }

    private static void WriteIfMissing(string path, string content)
    {
        if (File.Exists(path))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string LeafName(string path)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(path);
        var leaf = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(leaf) ? trimmed : leaf;
    }

    private static string ChecklistTemplate(string name) => $"""
        # {name} — component checklist

        The apps and services this ecosystem should ship. Check items off as they reach a working
        state; add or remove components to match your plans.

        - [ ] Windows desktop app
        - [ ] Mac desktop app
        - [ ] iPhone app
        - [ ] Android app
        - [ ] Website
        - [ ] Auth proxy
        - [ ] Database

        """;

    private static string ContractTemplate(string name) => $"""
        # {name} — shared data contract

        The single source of truth for data shapes shared across this ecosystem's apps. Every
        member project builds against what is defined here; propose changes here first, then
        update the members.

        ## Entities

        _Define your core entities (fields, types, relationships) here._

        ## API surface

        _Define the endpoints/operations members rely on here._

        """;

    private const string TokensTemplate = """
        {
          "note": "Ecosystem-wide design tokens. Members should use these for visual consistency.",
          "colors": {},
          "typography": {},
          "spacing": {}
        }
        """;
}
