using System.Text.Json;
using System.Text.RegularExpressions;
using AutoCode.Engine.Tools;

namespace AutoCode.Engine.Agent;

public sealed record VerifyResult(bool Passed, int? ExitCode, string Output);

public static class Verification
{
    public static string? ResolveCommand(string root, string? overrideCommand, IReadOnlyList<ProjectInstruction> instructions, IReadOnlyList<string> changedFiles)
    {
        if (!string.IsNullOrWhiteSpace(overrideCommand))
        {
            return overrideCommand.Trim();
        }

        var best = instructions
            .Where(i => !string.IsNullOrWhiteSpace(i.VerifyCommand))
            .Where(i => changedFiles.Count == 0 || changedFiles.All(f => IsUnderRelativeDir(f, i.RelativeDirectory)))
            .OrderByDescending(i => i.RelativeDirectory.Length)
            .FirstOrDefault();
        if (best?.VerifyCommand is not null)
        {
            return best.VerifyCommand.Trim();
        }

        return Infer(root);
    }

    public static async Task<VerifyResult> RunAsync(string command, string root, CancellationToken cancellationToken)
    {
        var result = await ToolArgs.RunProcessAsync(command, root, 180_000, cancellationToken).ConfigureAwait(false);
        var output = ((result.Stdout.Length > 0 ? "--- stdout ---\n" + result.Stdout + "\n" : "")
            + (result.Stderr.Length > 0 ? "--- stderr ---\n" + result.Stderr + "\n" : "")).Trim();
        if (output.Length > 16_384)
        {
            output = output[^16_384..];
        }

        if (result.TimedOut)
        {
            output += "\n[verification timed out after 180s]";
        }

        return new VerifyResult(result.ExitCode == 0 && !result.TimedOut, result.ExitCode, output);
    }

    private static bool IsUnderRelativeDir(string file, string dir)
    {
        if (string.IsNullOrWhiteSpace(dir))
        {
            return true;
        }

        var f = file.Replace('\\', '/');
        var d = dir.Replace('\\', '/').TrimEnd('/');
        return f == d || f.StartsWith(d + "/", StringComparison.Ordinal);
    }

    /// <summary>
    /// Infer a verification command from project markers. Mirrors the AutoCode TUI's
    /// inferVerifyCommand (src/agent/Verify.ts): conditional test-vs-build for Rust/Go,
    /// conservative pytest gating, plus JVM (gradle/mvn) and C++ (cmake) coverage so the
    /// agent sees its own test failures across languages.
    /// </summary>
    public static string? Infer(string root)
    {
        // Node / TypeScript
        if (File.Exists(Path.Combine(root, "package.json")))
        {
            var (hasTest, hasBuild) = ReadPackageTestBuild(root);
            if (hasTest) return "npm test";
            if (hasBuild) return "npm run build";
            if (File.Exists(Path.Combine(root, "tsconfig.json"))) return "npx tsc --noEmit";
            // Fall through: a repo can have package.json alongside another stack.
        }

        // .NET (this app's own stack; not present in the TUI)
        if (Directory.EnumerateFiles(root, "*.sln", SearchOption.TopDirectoryOnly).Any()
            || Directory.EnumerateFiles(root, "*.csproj", SearchOption.TopDirectoryOnly).Any())
        {
            return "dotnet test";
        }

        // Rust — run tests only when integration tests exist, else compile-only.
        if (File.Exists(Path.Combine(root, "Cargo.toml")))
        {
            return Directory.Exists(Path.Combine(root, "tests")) ? "cargo test" : "cargo check";
        }

        // Go — run tests only when *_test.go is present, else build-only.
        if (File.Exists(Path.Combine(root, "go.mod")))
        {
            return HasFileAtRoot(root, n => n.EndsWith("_test.go", StringComparison.OrdinalIgnoreCase))
                ? "go test ./..."
                : "go build ./...";
        }

        // Python — only with clear pytest evidence (a bare tests/ dir isn't enough).
        if (HasPytestSetup(root))
        {
            return "pytest";
        }

        // JVM — Gradle wrapper preferred, then Maven.
        if (File.Exists(Path.Combine(root, "gradlew.bat")) && OperatingSystem.IsWindows()) return "gradlew.bat test";
        if (File.Exists(Path.Combine(root, "gradlew"))) return "./gradlew test";
        if (File.Exists(Path.Combine(root, "pom.xml"))) return "mvn -q test";

        // C++ — build-only verification when a CMake project is present.
        if (File.Exists(Path.Combine(root, "CMakeLists.txt"))) return "cmake -B build && cmake --build build";

        return null;
    }

    private static (bool HasTest, bool HasBuild) ReadPackageTestBuild(string root)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "package.json")));
            if (doc.RootElement.TryGetProperty("scripts", out var scripts) && scripts.ValueKind == JsonValueKind.Object)
            {
                var hasTest = scripts.TryGetProperty("test", out var t) && t.ValueKind == JsonValueKind.String
                    && !Regex.IsMatch(t.GetString() ?? "", "no test specified", RegexOptions.IgnoreCase);
                var hasBuild = scripts.TryGetProperty("build", out var b) && b.ValueKind == JsonValueKind.String;
                return (hasTest, hasBuild);
            }
        }
        catch
        {
            // Unreadable / malformed package.json — treat as no scripts.
        }

        return (false, false);
    }

    private static bool HasFileAtRoot(string dir, Func<string, bool> predicate)
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(dir))
            {
                if (predicate(Path.GetFileName(path)))
                {
                    return true;
                }
            }
        }
        catch
        {
            // Unreadable directory — treat as no match.
        }

        return false;
    }

    private static bool HasPytestSetup(string root)
    {
        if (File.Exists(Path.Combine(root, "pytest.ini"))
            || File.Exists(Path.Combine(root, "pytest.cfg"))
            || File.Exists(Path.Combine(root, "conftest.py")))
        {
            return true;
        }

        try
        {
            if (Regex.IsMatch(File.ReadAllText(Path.Combine(root, "pyproject.toml")), @"\[tool\.pytest"))
            {
                return true;
            }
        }
        catch
        {
            // No / unreadable pyproject.toml — fall through.
        }

        bool IsTestName(string n) => Regex.IsMatch(n, @"^(test_.+\.py|.+_test\.py|conftest\.py)$", RegexOptions.IgnoreCase);
        if (HasFileAtRoot(root, IsTestName))
        {
            return true;
        }

        foreach (var sub in new[] { "tests", "test" })
        {
            var dir = Path.Combine(root, sub);
            if (Directory.Exists(dir) && HasFileAtRoot(dir, IsTestName))
            {
                return true;
            }
        }

        return false;
    }
}
