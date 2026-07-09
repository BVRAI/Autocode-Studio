using System.Text.Json;
using System.Text.RegularExpressions;
using AutoCode.Engine.Tools;

namespace AutoCode.Engine.Agent;

public sealed record VerifyResult(bool Passed, int? ExitCode, string Output);

public sealed record VerifyPlan(string Command, string? FullCommand, string Source);

public static class Verification
{
    public static VerifyPlan? ResolvePlan(string root, string? overrideCommand, IReadOnlyList<ProjectInstruction> instructions, IReadOnlyList<string> changedFiles)
    {
        if (!string.IsNullOrWhiteSpace(overrideCommand))
        {
            return new VerifyPlan(overrideCommand.Trim(), null, "override");
        }

        var best = instructions
            .Where(i => !string.IsNullOrWhiteSpace(i.VerifyCommand))
            .Where(i => changedFiles.Count == 0 || changedFiles.All(f => IsUnderRelativeDir(f, i.RelativeDirectory)))
            .OrderByDescending(i => i.RelativeDirectory.Length)
            .FirstOrDefault();
        if (best?.VerifyCommand is not null)
        {
            return new VerifyPlan(best.VerifyCommand.Trim(), null, "directive");
        }

        var inferred = Infer(root);
        if (inferred is null)
        {
            return null;
        }

        var scoped = ScopeInferredCommand(root, inferred, changedFiles);
        return scoped.IsScoped
            ? new VerifyPlan(scoped.Command, inferred, "inferred-scoped")
            : new VerifyPlan(inferred, null, "inferred");
    }

    public static string? ResolveCommand(string root, string? overrideCommand, IReadOnlyList<ProjectInstruction> instructions, IReadOnlyList<string> changedFiles)
    {
        var plan = ResolvePlan(root, overrideCommand, instructions, changedFiles);
        return plan is null ? null : plan.FullCommand ?? plan.Command;
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

    public sealed record ScopedCommand(string Command, bool IsScoped);

    public static ScopedCommand ScopeInferredCommand(string root, string inferred, IReadOnlyList<string> changedFiles)
    {
        var full = new ScopedCommand(inferred, false);
        var files = changedFiles
            .Select(p => p.Replace('\\', '/').TrimStart('.', '/'))
            .Where(p => !Regex.IsMatch(p, @"\.(md|txt)$", RegexOptions.IgnoreCase))
            .ToList();
        if (files.Count == 0)
        {
            return full;
        }

        if (inferred == "go test ./...")
        {
            return ScopeGo(files) ?? full;
        }

        if (inferred == "pytest")
        {
            return ScopePytest(root, files) ?? full;
        }

        if (inferred == "npm test")
        {
            return ScopeNpmTest(root, files) ?? full;
        }

        return full;
    }

    private static ScopedCommand? ScopeGo(IReadOnlyList<string> files)
    {
        if (!files.All(f => f.EndsWith(".go", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var dirs = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var i = file.LastIndexOf('/');
            if (i < 0)
            {
                return null;
            }

            dirs.Add(file[..i]);
        }

        return new ScopedCommand("go test " + string.Join(' ', dirs.Select(d => "./" + d + "/...")), true);
    }

    private static ScopedCommand? ScopePytest(string root, IReadOnlyList<string> files)
    {
        var targets = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (!file.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var fileName = Path.GetFileName(file);
            if (fileName.Equals("conftest.py", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (Regex.IsMatch(fileName, @"^(test_.+|.+_test)\.py$", RegexOptions.IgnoreCase))
            {
                targets.Add(file);
                continue;
            }

            var dir = file.Contains('/') ? file[..file.LastIndexOf('/')] : "";
            var stem = Path.GetFileNameWithoutExtension(file);
            var prefix = string.IsNullOrEmpty(dir) ? "" : dir + "/";
            var candidates = new[]
            {
                $"{prefix}test_{stem}.py",
                $"{prefix}{stem}_test.py",
                $"{prefix}tests/test_{stem}.py",
                $"tests/test_{stem}.py",
                $"test/test_{stem}.py"
            };
            var hit = candidates.FirstOrDefault(c => File.Exists(Path.Combine(root, c.Replace('/', Path.DirectorySeparatorChar))));
            if (hit is null)
            {
                return null;
            }

            targets.Add(hit);
        }

        return targets.Count == 0 ? null : new ScopedCommand("pytest " + QuoteAll(targets), true);
    }

    private static ScopedCommand? ScopeNpmTest(string root, IReadOnlyList<string> files)
    {
        var runner = DetectNodeTestRunner(root);
        if (runner is null)
        {
            return null;
        }

        var targets = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (!Regex.IsMatch(file, @"\.[cm]?[jt]sx?$", RegexOptions.IgnoreCase)
                || file.EndsWith(".d.ts", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (Regex.IsMatch(file, @"\.(test|spec)\.[cm]?[jt]sx?$", RegexOptions.IgnoreCase))
            {
                if (!File.Exists(Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar))))
                {
                    return null;
                }

                targets.Add(file);
                continue;
            }

            var dir = file.Contains('/') ? file[..file.LastIndexOf('/')] : "";
            var baseName = Regex.Replace(Path.GetFileName(file), @"\.[cm]?[jt]sx?$", "", RegexOptions.IgnoreCase);
            var prefix = string.IsNullOrEmpty(dir) ? "" : dir + "/";
            var testExts = new[] { ".test.ts", ".test.tsx", ".test.js", ".test.jsx", ".spec.ts", ".spec.tsx", ".spec.js", ".spec.jsx" };
            var candidates = new List<string>();
            foreach (var ext in testExts)
            {
                candidates.Add($"{prefix}{baseName}{ext}");
                candidates.Add($"{prefix}__tests__/{baseName}{ext}");
            }

            if (file.StartsWith("src/", StringComparison.Ordinal))
            {
                var restDir = dir.Length > "src/".Length ? dir["src/".Length..] : "";
                var mid = string.IsNullOrEmpty(restDir) ? "" : restDir + "/";
                foreach (var mirror in new[] { "test", "tests" })
                {
                    foreach (var ext in testExts)
                    {
                        candidates.Add($"{mirror}/{mid}{baseName}{ext}");
                    }
                }
            }

            var hit = candidates.FirstOrDefault(c => File.Exists(Path.Combine(root, c.Replace('/', Path.DirectorySeparatorChar))));
            if (hit is null)
            {
                return null;
            }

            targets.Add(hit);
        }

        if (targets.Count == 0)
        {
            return null;
        }

        return new ScopedCommand((runner == "vitest" ? "npx vitest run " : "npx jest ") + QuoteAll(targets), true);
    }

    private static string? DetectNodeTestRunner(string root)
    {
        try
        {
            using var doc = JsonDocument.Parse(StripBom(File.ReadAllText(Path.Combine(root, "package.json"))));
            var scripts = doc.RootElement.TryGetProperty("scripts", out var s) && s.ValueKind == JsonValueKind.Object ? s : default;
            var testScript = scripts.ValueKind == JsonValueKind.Object
                && scripts.TryGetProperty("test", out var test)
                && test.ValueKind == JsonValueKind.String
                ? test.GetString() ?? ""
                : "";
            var depsText = File.ReadAllText(Path.Combine(root, "package.json"));
            if (depsText.Contains("\"vitest\"", StringComparison.Ordinal) || Regex.IsMatch(testScript, @"\bvitest\b"))
            {
                return "vitest";
            }

            if (depsText.Contains("\"jest\"", StringComparison.Ordinal) || Regex.IsMatch(testScript, @"\bjest\b"))
            {
                return "jest";
            }
        }
        catch
        {
            // Cannot scope safely.
        }

        return null;
    }

    private static string QuoteAll(IEnumerable<string> paths)
        => string.Join(' ', paths.Select(p => p.Contains(' ') ? "\"" + p + "\"" : p));

    private static string StripBom(string text)
        => text.Length > 0 && text[0] == '\ufeff' ? text[1..] : text;

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
            using var doc = JsonDocument.Parse(StripBom(File.ReadAllText(Path.Combine(root, "package.json"))));
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
