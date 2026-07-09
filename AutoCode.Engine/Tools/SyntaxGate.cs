using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AutoCode.Engine.Tools;

internal sealed record SyntaxCheck(bool Ok, bool Skipped, string Checker, string Diagnostics);

internal abstract record GateOutcome
{
    public sealed record Pass : GateOutcome;

    public sealed record Reverted(ToolResult Result) : GateOutcome;

    public sealed record KeptWithWarning(string Warning) : GateOutcome;
}

internal static class SyntaxGate
{
    private const int CheckerTimeoutMs = 5_000;
    private const int DiagnosticsCap = 2_000;
    private const int MaxConsecutiveRejections = 2;
    private const string PythonSentinel = "AUTOCODE_SYNTAX_ERROR";
    private static readonly Dictionary<string, int> ConsecutiveRejections = new(StringComparer.OrdinalIgnoreCase);
    private static string? _cachedPython;
    private static bool _pythonProbed;

    public static async Task<GateOutcome> GateAfterWriteAsync(
        string target,
        string relPath,
        string projectRoot,
        string original,
        bool existedBefore,
        string content,
        CancellationToken cancellationToken)
    {
        if (Environment.GetEnvironmentVariable("AUTOCODE_NO_SYNTAX_GATE") == "1")
        {
            return new GateOutcome.Pass();
        }

        var check = await CheckFileSyntaxAsync(target, content, projectRoot, cancellationToken).ConfigureAwait(false);
        if (check.Ok || check.Skipped)
        {
            ConsecutiveRejections.Remove(target);
            return new GateOutcome.Pass();
        }

        var rejections = ConsecutiveRejections.TryGetValue(target, out var n) ? n + 1 : 1;
        if (rejections > MaxConsecutiveRejections)
        {
            ConsecutiveRejections.Remove(target);
            return new GateOutcome.KeptWithWarning(
                $"WARNING: the syntax checker ({check.Checker}) still reports an error, but this edit was applied anyway after {MaxConsecutiveRejections} rejected attempts:\n{check.Diagnostics}\nIf the checker is wrong, continue; otherwise fix the syntax.");
        }

        ConsecutiveRejections[target] = rejections;
        if (existedBefore)
        {
            File.WriteAllText(target, original);
        }
        else
        {
            File.Delete(target);
        }

        return new GateOutcome.Reverted(new ToolResult(
            "syntax error - edit rolled back",
            "The edit introduced a syntax error, so it was NOT applied - "
            + (existedBefore
                ? $"{relPath} was reverted to its previous content."
                : $"the new file {relPath} was removed.")
            + $"\n\n{check.Checker} reported:\n{check.Diagnostics}\n\nFix the syntax in your new content and retry the edit. The file on disk is unchanged, so your old_text anchor is still valid.",
            true,
            ToolArgs.Metadata(("syntaxGate", true), ("checker", check.Checker), ("diagnostics", check.Diagnostics))));
    }

    public static async Task<SyntaxCheck> CheckFileSyntaxAsync(
        string absPath,
        string content,
        string projectRoot,
        CancellationToken cancellationToken = default)
    {
        return Path.GetExtension(absPath).ToLowerInvariant() switch
        {
            ".json" => CheckJson(content),
            ".js" or ".mjs" or ".cjs" => await CheckWithNodeAsync(absPath, content, cancellationToken).ConfigureAwait(false),
            ".ts" or ".tsx" or ".mts" or ".cts" or ".jsx" => await CheckWithTypeScriptAsync(absPath, projectRoot, cancellationToken).ConfigureAwait(false),
            ".py" => await CheckWithPythonAsync(absPath, cancellationToken).ConfigureAwait(false),
            _ => new SyntaxCheck(true, true, "none", "")
        };
    }

    internal static void ResetForTests()
    {
        ConsecutiveRejections.Clear();
        _cachedPython = null;
        _pythonProbed = false;
    }

    private static SyntaxCheck CheckJson(string content)
    {
        try
        {
            using var _ = JsonDocument.Parse(content);
            return new SyntaxCheck(true, false, "json", "");
        }
        catch (Exception ex)
        {
            return new SyntaxCheck(false, false, "json", Cap(ex.Message));
        }
    }

    private static async Task<SyntaxCheck> CheckWithNodeAsync(string absPath, string content, CancellationToken cancellationToken)
    {
        var first = await RunCheckerAsync("node", ["--check", absPath], null, cancellationToken).ConfigureAwait(false);
        if (first.FailedToRun)
        {
            return new SyntaxCheck(true, true, "node", "");
        }

        if (first.Code == 0)
        {
            return new SyntaxCheck(true, false, "node", "");
        }

        if (IsEsmInCjsError(first.Stderr))
        {
            var retry = await RunCheckerAsync("node", ["--check", "--input-type=module", "-"], content, cancellationToken).ConfigureAwait(false);
            if (!retry.FailedToRun && retry.Code == 0)
            {
                return new SyntaxCheck(true, false, "node", "");
            }
        }

        return new SyntaxCheck(false, false, "node", Cap(first.Stderr));
    }

    private static async Task<SyntaxCheck> CheckWithTypeScriptAsync(string absPath, string projectRoot, CancellationToken cancellationToken)
    {
        var script =
            "const fs=require('fs');"
            + "let ts;try{ts=require(require.resolve('typescript',{paths:[process.cwd()]}));}catch(e){process.exit(43);}"
            + "const file=process.argv[1];"
            + "const content=fs.readFileSync(file,'utf8');"
            + "const out=ts.transpileModule(content,{reportDiagnostics:true,fileName:file,compilerOptions:{jsx:ts.JsxEmit.Preserve,target:ts.ScriptTarget.ESNext}});"
            + "const errors=(out.diagnostics||[]).filter(d=>d.category===ts.DiagnosticCategory.Error);"
            + "if(errors.length){"
            + "for(const d of errors.slice(0,10)){let msg=ts.flattenDiagnosticMessageText(d.messageText,'\\n');"
            + "if(d.file&&typeof d.start==='number'){const p=d.file.getLineAndCharacterOfPosition(d.start); msg=`line ${p.line+1}, col ${p.character+1}: ${msg}`;}"
            + "console.error(msg);}process.exit(42);}";
        var run = await RunCheckerAsync("node", ["-e", script, absPath], null, cancellationToken, projectRoot).ConfigureAwait(false);
        if (run.FailedToRun || run.Code == 43)
        {
            return new SyntaxCheck(true, true, "typescript", "");
        }

        return run.Code == 0
            ? new SyntaxCheck(true, false, "typescript", "")
            : new SyntaxCheck(false, false, "typescript", Cap(run.Stderr));
    }

    private static async Task<SyntaxCheck> CheckWithPythonAsync(string absPath, CancellationToken cancellationToken)
    {
        if (_pythonProbed && _cachedPython is null)
        {
            return new SyntaxCheck(true, true, "python", "");
        }

        var candidates = _cachedPython is not null
            ? [_cachedPython]
            : OperatingSystem.IsWindows() ? new[] { "py", "python", "python3" } : ["python3", "python"];
        var script = "import ast,sys\n"
            + "try:\n"
            + "    ast.parse(open(sys.argv[1],'rb').read(), sys.argv[1])\n"
            + "except SyntaxError as e:\n"
            + $"    sys.stderr.write('{PythonSentinel}\\n%s' % e); sys.exit(42)\n";

        foreach (var cmd in candidates)
        {
            var run = await RunCheckerAsync(cmd, ["-c", script, absPath], null, cancellationToken).ConfigureAwait(false);
            if (run.FailedToRun)
            {
                continue;
            }

            if (run.Code == 0)
            {
                _cachedPython = cmd;
                _pythonProbed = true;
                return new SyntaxCheck(true, false, "python", "");
            }

            if (run.Code == 42 && run.Stderr.Contains(PythonSentinel, StringComparison.Ordinal))
            {
                _cachedPython = cmd;
                _pythonProbed = true;
                var diag = run.Stderr[(run.Stderr.IndexOf(PythonSentinel, StringComparison.Ordinal) + PythonSentinel.Length)..].Trim();
                return new SyntaxCheck(false, false, "python", Cap(diag));
            }
        }

        _cachedPython = null;
        _pythonProbed = true;
        return new SyntaxCheck(true, true, "python", "");
    }

    private static async Task<CheckerRun> RunCheckerAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? stdin,
        CancellationToken cancellationToken,
        string? workingDirectory = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CheckerTimeoutMs);
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardInput = stdin is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory
        };
        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        try
        {
            using var process = new Process { StartInfo = startInfo };
            var stderr = new StringBuilder();
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null && stderr.Length < DiagnosticsCap * 2)
                {
                    stderr.AppendLine(e.Data);
                }
            };
            process.Start();
            process.BeginErrorReadLine();
            if (stdin is not null)
            {
                await process.StandardInput.WriteAsync(stdin).ConfigureAwait(false);
                process.StandardInput.Close();
            }

            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            process.WaitForExit();
            return new CheckerRun(process.ExitCode, stderr.ToString(), false);
        }
        catch
        {
            return new CheckerRun(null, "", true);
        }
    }

    private static bool IsEsmInCjsError(string stderr)
        => stderr.Contains("Cannot use import statement outside a module", StringComparison.Ordinal)
            || stderr.Contains("Unexpected token 'export'", StringComparison.Ordinal)
            || stderr.Contains("Cannot use 'export'", StringComparison.Ordinal)
            || stderr.Contains("await is only valid", StringComparison.Ordinal)
            || stderr.Contains("Unexpected token 'import'", StringComparison.Ordinal);

    private static string Cap(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= DiagnosticsCap ? trimmed : trimmed[..DiagnosticsCap] + "\n... (truncated)";
    }

    private sealed record CheckerRun(int? Code, string Stderr, bool FailedToRun);
}
