using System.Text.RegularExpressions;

namespace AutoCode.Engine.Safety;

public enum SafetyKind
{
    Allow,
    Confirm,
    Block
}

public sealed record SafetyVerdict(SafetyKind Kind, string? Reason = null, string? Pattern = null);

public static partial class SafetyPolicy
{
    private static readonly (Regex Re, string Reason)[] HardBlock =
    [
        (new Regex(@"\brm\s+-rf\s+/(?:\s|$)", RegexOptions.IgnoreCase), "recursive deletion of filesystem root"),
        (new Regex(@"\bformat\b", RegexOptions.IgnoreCase), "disk formatting command"),
        (new Regex(@"\bdiskpart\b", RegexOptions.IgnoreCase), "disk partitioning command"),
        (new Regex(@"\bshutdown\b", RegexOptions.IgnoreCase), "system shutdown command"),
        (new Regex(@"\bmkfs(\.|$|\s)", RegexOptions.IgnoreCase), "filesystem creation command"),
        (new Regex(@"\bdd\s+if=", RegexOptions.IgnoreCase), "raw disk write command"),
        (new Regex(@">\s*(/etc/passwd|/etc/shadow)", RegexOptions.IgnoreCase), "writes protected system credentials")
    ];

    private static readonly (Regex Re, string Reason)[] SoftConfirm =
    [
        (new Regex(@"\bgit\s+reset\s+--hard\b", RegexOptions.IgnoreCase), "hard git reset"),
        (new Regex(@"\bgit\s+push\b.*\s--force\b", RegexOptions.IgnoreCase), "force push"),
        (new Regex(@"\brm\s+-r", RegexOptions.IgnoreCase), "recursive deletion"),
        (new Regex(@"\bdel\s+[/\-][sq]\b", RegexOptions.IgnoreCase), "recursive deletion"),
        (new Regex(@"\bRemove-Item\b.*\s-Recurse\b", RegexOptions.IgnoreCase), "recursive deletion")
    ];

    private static readonly Regex DestructiveVerb =
        new(@"\b(rm|rmdir|rd|del|erase|unlink|mv|move|move-item|remove-item|ri)\b", RegexOptions.IgnoreCase);

    public static SafetyVerdict Classify(string command, string? projectRoot = null)
    {
        var trimmed = command.Trim();
        if (trimmed.Length == 0)
        {
            return new SafetyVerdict(SafetyKind.Allow);
        }

        foreach (var pattern in HardBlock)
        {
            if (pattern.Re.IsMatch(trimmed))
            {
                return new SafetyVerdict(SafetyKind.Block, pattern.Reason, pattern.Re.ToString());
            }
        }

        if (!string.IsNullOrWhiteSpace(projectRoot))
        {
            var pathVerdict = PathAwareVerdict(trimmed, projectRoot);
            if (pathVerdict is not null)
            {
                return pathVerdict;
            }
        }

        foreach (var pattern in SoftConfirm)
        {
            if (pattern.Re.IsMatch(trimmed))
            {
                return new SafetyVerdict(SafetyKind.Confirm, pattern.Reason, pattern.Re.ToString());
            }
        }

        return new SafetyVerdict(SafetyKind.Allow);
    }

    private static SafetyVerdict? PathAwareVerdict(string command, string projectRoot)
    {
        var candidates = new List<string>();
        if (DestructiveVerb.IsMatch(command))
        {
            candidates.AddRange(Tokenize(command).Where(IsPathLike));
        }

        foreach (Match match in Regex.Matches(command, @">>?\s*(""[^""]*""|'[^']*'|\S+)"))
        {
            var raw = match.Groups[1].Value.Trim('"', '\'');
            if (raw.Equals("nul", StringComparison.OrdinalIgnoreCase)
                || raw.Equals("nul:", StringComparison.OrdinalIgnoreCase)
                || raw.Equals("/dev/null", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            candidates.Add(raw);
        }

        foreach (var raw in candidates)
        {
            if (raw.Contains('$', StringComparison.Ordinal) || raw.Contains('%', StringComparison.Ordinal))
            {
                return new SafetyVerdict(
                    SafetyKind.Block,
                    "destructive command with unresolved path variable",
                    raw);
            }

            var abs = ResolvePathToken(raw, projectRoot);
            var fenced = PathSafety.FencedReason(abs);
            if (fenced is not null)
            {
                return new SafetyVerdict(SafetyKind.Block, $"targets a protected system zone ({fenced})", raw);
            }

            if (IsOutsideRoot(abs, projectRoot))
            {
                return new SafetyVerdict(SafetyKind.Block, "targets a path outside the project root", raw);
            }
        }

        return null;
    }

    private static IEnumerable<string> Tokenize(string command)
    {
        foreach (Match match in Regex.Matches(command, @"""([^""]*)""|'([^']*)'|(\S+)"))
        {
            yield return match.Groups[1].Success
                ? match.Groups[1].Value
                : match.Groups[2].Success
                    ? match.Groups[2].Value
                    : match.Groups[3].Value;
        }
    }

    private static bool IsPathLike(string token) =>
        token.Length > 0
        && !token.StartsWith("-", StringComparison.Ordinal)
        && (token.Contains("/", StringComparison.Ordinal)
            || token.Contains("\\", StringComparison.Ordinal)
            || Regex.IsMatch(token, @"^[a-zA-Z]:")
            || token.StartsWith("~", StringComparison.Ordinal)
            || token.StartsWith("..", StringComparison.Ordinal));

    private static string ResolvePathToken(string token, string projectRoot)
    {
        var t = token;
        if (t == "~" || t.StartsWith("~/", StringComparison.Ordinal) || t.StartsWith("~\\", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            t = Path.Combine(home, t[1..].TrimStart('/', '\\'));
        }

        return Path.IsPathRooted(t) ? Path.GetFullPath(t) : Path.GetFullPath(Path.Combine(projectRoot, t));
    }

    private static bool IsOutsideRoot(string abs, string projectRoot)
    {
        var rel = Path.GetRelativePath(Path.GetFullPath(projectRoot), Path.GetFullPath(abs));
        return rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel);
    }
}
