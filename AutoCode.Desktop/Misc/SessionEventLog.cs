using System.IO;
using System.Text.Json.Nodes;
using AutoCode.Engine.Agent;

namespace AutoCode.Desktop.Misc;

/// <summary>
/// Desktop-only rendered-chat log. The engine persists transcript.jsonl (flat role+text) for its own
/// LLM history; the *rendered* chat — tool groups, diff cards, notices, plan, timeline — is a Desktop
/// concept, so we persist the session's <see cref="AgentEvent"/> stream here and replay it on reopen.
/// One compact JSON line per event (true JSONL) in events.jsonl. The engine event records are untouched:
/// the mapping to/from JSON is explicit here, so the engine stays ignorant of this persistence format.
/// Only block-affecting events are stored; <see cref="StatusEvent"/> is ephemeral and skipped.
/// </summary>
public static class SessionEventLog
{
    private const string LogName = "events.jsonl";

    // Appends are quick and serialized per emitting thread; a single gate keeps concurrent sessions'
    // writes from interleaving a partial line into the wrong file's buffer.
    private static readonly object Gate = new();

    /// <summary>Append one event to the session's chat log. Returns silently for non-persisted kinds
    /// (e.g. <see cref="StatusEvent"/>) and on any I/O failure — the chat log never blocks a turn.</summary>
    public static void Append(string sessionDir, AgentEvent evt)
    {
        if (string.IsNullOrEmpty(sessionDir))
        {
            return;
        }

        var node = ToNode(evt);
        if (node is null)
        {
            return;
        }

        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(sessionDir);
                File.AppendAllText(Path.Combine(sessionDir, LogName), node.ToJsonString() + "\n");
            }
        }
        catch
        {
            // Best-effort — a failed chat-log write must not fail the turn.
        }
    }

    /// <summary>Load the persisted event stream in order. Empty when the log is absent (sessions created
    /// before this feature) — callers fall back to the transcript-only restore.</summary>
    public static IReadOnlyList<AgentEvent> Load(string sessionDir)
    {
        var result = new List<AgentEvent>();
        var path = Path.Combine(sessionDir, LogName);
        if (!File.Exists(path))
        {
            return result;
        }

        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (JsonNode.Parse(line) is JsonObject obj && FromNode(obj) is { } evt)
                {
                    result.Add(evt);
                }
            }
        }
        catch
        {
            // Corrupt log — replay whatever parsed cleanly.
        }

        return result;
    }

    /// <summary>True when a replayable chat log exists for this session.</summary>
    public static bool Exists(string sessionDir)
        => !string.IsNullOrEmpty(sessionDir) && File.Exists(Path.Combine(sessionDir, LogName));

    private static JsonObject? ToNode(AgentEvent evt) => evt switch
    {
        ChatEvent e => new JsonObject { ["kind"] = "chat", ["at"] = e.At.ToString("o"), ["role"] = e.Role, ["text"] = e.Text },
        ToolCallEvent e => new JsonObject { ["kind"] = "toolCall", ["at"] = e.At.ToString("o"), ["tool"] = e.ToolName, ["args"] = e.ArgumentsJson },
        ToolResultEvent e => new JsonObject { ["kind"] = "toolResult", ["at"] = e.At.ToString("o"), ["tool"] = e.ToolName, ["summary"] = e.Summary, ["content"] = e.Content, ["isError"] = e.IsError, ["durationMs"] = e.DurationMs },
        VerificationEvent e => new JsonObject { ["kind"] = "verification", ["at"] = e.At.ToString("o"), ["command"] = e.Command, ["passed"] = e.Passed, ["output"] = e.Output },
        PlanEvent e => new JsonObject { ["kind"] = "plan", ["at"] = e.At.ToString("o"), ["items"] = PlanItemsToArray(e.Items) },
        _ => null, // StatusEvent (and any future ephemeral event) is not persisted.
    };

    private static AgentEvent? FromNode(JsonObject obj)
    {
        var at = DateTimeOffset.TryParse((string?)obj["at"], out var parsed) ? parsed : DateTimeOffset.Now;
        return (string?)obj["kind"] switch
        {
            "chat" => new ChatEvent(at, (string?)obj["role"] ?? "assistant", (string?)obj["text"] ?? ""),
            "toolCall" => new ToolCallEvent(at, (string?)obj["tool"] ?? "", (string?)obj["args"] ?? ""),
            "toolResult" => new ToolResultEvent(at, (string?)obj["tool"] ?? "", (string?)obj["summary"] ?? "", (string?)obj["content"] ?? "", (bool?)obj["isError"] ?? false, (long?)obj["durationMs"] ?? 0),
            "verification" => new VerificationEvent(at, (string?)obj["command"] ?? "", (bool?)obj["passed"], (string?)obj["output"] ?? ""),
            "plan" => new PlanEvent(at, PlanItemsFromArray(obj["items"] as JsonArray)),
            _ => null,
        };
    }

    private static JsonArray PlanItemsToArray(IReadOnlyList<PlanItem> items)
    {
        var arr = new JsonArray();
        foreach (var item in items)
        {
            arr.Add(new JsonObject { ["id"] = item.Id, ["text"] = item.Text, ["status"] = item.Status });
        }

        return arr;
    }

    private static IReadOnlyList<PlanItem> PlanItemsFromArray(JsonArray? arr)
    {
        var items = new List<PlanItem>();
        if (arr is null)
        {
            return items;
        }

        foreach (var node in arr)
        {
            if (node is JsonObject o)
            {
                items.Add(new PlanItem((string?)o["id"] ?? "", (string?)o["text"] ?? "", (string?)o["status"] ?? "pending"));
            }
        }

        return items;
    }
}
