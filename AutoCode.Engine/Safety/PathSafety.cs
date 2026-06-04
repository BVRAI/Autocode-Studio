namespace AutoCode.Engine.Safety;

public sealed class PathSafetyException : Exception
{
    public PathSafetyException(string message) : base(message)
    {
    }
}

public static class PathSafety
{
    public static string ResolveInsideRoot(string projectRoot, string requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            throw new PathSafetyException("path must be a non-empty string");
        }

        var root = Path.GetFullPath(projectRoot);
        var absolute = Path.IsPathRooted(requested)
            ? Path.GetFullPath(requested)
            : Path.GetFullPath(Path.Combine(root, requested));
        var relative = Path.GetRelativePath(root, absolute);
        if (relative == "." || string.IsNullOrEmpty(relative))
        {
            return absolute;
        }

        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            throw new PathSafetyException($"path escapes project root: {requested}");
        }

        var fenced = FencedReason(absolute);
        if (fenced is not null)
        {
            throw new PathSafetyException($"path is in a protected zone ({fenced}): {requested}");
        }

        return absolute;
    }

    public static string ToRelative(string projectRoot, string absolute)
    {
        var rel = Path.GetRelativePath(Path.GetFullPath(projectRoot), Path.GetFullPath(absolute));
        return rel == "." ? "." : rel.Replace('\\', '/');
    }

    public static void EnsureDirectory(string absolute)
    {
        if (!Directory.Exists(absolute))
        {
            throw new PathSafetyException($"not a directory: {absolute}");
        }
    }

    public static string? FencedReason(string absolutePath)
    {
        var p = Path.GetFullPath(absolutePath);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        foreach (var zone in new[] { windows, programFiles, programFilesX86, system }.Where(z => !string.IsNullOrWhiteSpace(z)))
        {
            if (IsUnder(p, zone))
            {
                return zone;
            }
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var zone in new[] { ".ssh", ".aws", ".azure", ".gnupg", ".kube" })
        {
            var full = Path.Combine(home, zone);
            if (IsUnder(p, full))
            {
                return zone;
            }
        }

        return null;
    }

    private static bool IsUnder(string candidate, string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        var rel = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(candidate));
        return rel == "." || (!rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel));
    }
}
