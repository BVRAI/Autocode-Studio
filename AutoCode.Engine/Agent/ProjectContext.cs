using System.Text;
using AutoCode.Engine.Tools;

namespace AutoCode.Engine.Agent;

public sealed record ProjectContextInfo(IReadOnlyList<string> Types, bool IsGitRepository);

public static class ProjectContext
{
    public static ProjectContextInfo Detect(string root)
    {
        var types = new List<string>();
        if (File.Exists(Path.Combine(root, "package.json"))) types.Add("node");
        if (File.Exists(Path.Combine(root, "tsconfig.json"))) types.Add("typescript");
        if (File.Exists(Path.Combine(root, "pyproject.toml")) || File.Exists(Path.Combine(root, "requirements.txt"))) types.Add("python");
        if (File.Exists(Path.Combine(root, "Cargo.toml"))) types.Add("rust");
        if (File.Exists(Path.Combine(root, "go.mod"))) types.Add("go");
        if (Directory.EnumerateFiles(root, "*.csproj", SearchOption.TopDirectoryOnly).Any() || Directory.EnumerateFiles(root, "*.sln", SearchOption.TopDirectoryOnly).Any()) types.Add("dotnet");
        if (File.Exists(Path.Combine(root, "pom.xml")) || File.Exists(Path.Combine(root, "build.gradle")) || File.Exists(Path.Combine(root, "build.gradle.kts"))) types.Add("jvm");
        return new ProjectContextInfo(types, Directory.Exists(Path.Combine(root, ".git")));
    }

    public static string RepoMap(string root, int maxFiles = 180)
    {
        var sb = new StringBuilder();
        var count = 0;
        void Walk(string dir, int depth)
        {
            if (count >= maxFiles || depth > 5)
            {
                return;
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(dir).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(entry);
                if (Directory.Exists(entry))
                {
                    if (ToolConstants.NoiseDirectories.Contains(name))
                    {
                        continue;
                    }

                    Walk(entry, depth + 1);
                }
                else
                {
                    var rel = Path.GetRelativePath(root, entry).Replace('\\', '/');
                    sb.AppendLine(rel);
                    count++;
                    if (count >= maxFiles)
                    {
                        return;
                    }
                }
            }
        }

        try
        {
            Walk(root, 0);
        }
        catch
        {
            return "";
        }

        return sb.ToString().Trim();
    }
}
