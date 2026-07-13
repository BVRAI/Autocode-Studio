using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoCode.Engine.Auth;

namespace AutoCode.Engine.Llm;

public sealed class AnthropicProvider : ILlmProvider
{
    private const string DefaultBaseUrl = "https://api.anthropic.com/v1";
    private const string ApiVersion = "2023-06-01";
    // Server-side context editing (clear stale tool results after cache lookup).
    private const string ContextManagementBeta = "context-management-2025-06-27";
    private static readonly HttpClient Http = new();
    private readonly AuthMode _auth;

    public AnthropicProvider(AuthMode auth)
    {
        _auth = auth;
    }

    public string Name => "anthropic";

    public async Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken)
    {
        if (_auth.Kind == AuthKind.Missing)
        {
            throw new InvalidOperationException("Anthropic credentials missing. Set ANTHROPIC_API_KEY or configure proxy access.");
        }

        var baseUrl = _auth.Kind == AuthKind.Proxy ? _auth.BaseUrl! : DefaultBaseUrl;
        var contextManagement = ContextManagementParam(request);
        using var http = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/messages");
        http.Content = new StringContent(BuildBody(request, contextManagement).ToJsonString(), Encoding.UTF8, "application/json");
        http.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        http.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
        if (contextManagement is not null)
        {
            http.Headers.TryAddWithoutValidation("anthropic-beta", ContextManagementBeta);
        }

        if (_auth.Kind == AuthKind.Byok)
        {
            http.Headers.TryAddWithoutValidation("x-api-key", _auth.ApiKey);
        }
        else
        {
            http.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _auth.ProxyToken);
        }

        using var response = await Http.SendAsync(http, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"anthropic {(int)response.StatusCode}: {Trim(text, 500)}");
        }

        return ParseResponse(text);
    }

    internal static JsonObject BuildBody(CompletionRequest request, JsonObject? contextManagement)
    {
        var system = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = request.System,
                ["cache_control"] = new JsonObject { ["type"] = "ephemeral" }
            }
        };
        if (!string.IsNullOrWhiteSpace(request.SystemVolatile))
        {
            system.Add(new JsonObject { ["type"] = "text", ["text"] = request.SystemVolatile });
        }

        var tools = new JsonArray();
        for (var i = 0; i < request.Tools.Count; i++)
        {
            var tool = request.Tools[i];
            var obj = new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["input_schema"] = JsonHelpers.CloneNode(tool.InputSchema)
            };
            if (i == request.Tools.Count - 1)
            {
                obj["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
            }

            tools.Add(obj);
        }

        var messages = new JsonArray();
        foreach (var message in request.Messages)
        {
            messages.Add(ToAnthropicMessage(message));
        }

        WithRollingCacheBreakpoint(messages);

        // With thinking enabled, max_tokens must EXCEED the budget (keep ≥8K visible output beyond it),
        // and Anthropic requires temperature 1.
        int? thinkingBudget = request.Thinking is { BudgetTokens: > 0 } t ? Math.Max(1024, t.BudgetTokens) : null;

        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["max_tokens"] = thinkingBudget is int b ? Math.Max(request.MaxTokens, b + 8192) : request.MaxTokens,
            ["temperature"] = thinkingBudget is not null ? 1.0 : request.Temperature,
            ["system"] = system,
            ["tools"] = tools,
            ["messages"] = messages
        };
        if (thinkingBudget is int budget)
        {
            body["thinking"] = new JsonObject { ["type"] = "enabled", ["budget_tokens"] = budget };
        }

        if (contextManagement is not null)
        {
            body["context_management"] = contextManagement;
        }

        return body;
    }

    private JsonObject? ContextManagementParam(CompletionRequest request)
    {
        // Server-side tool-result clearing after cache lookup — preserves the cached prefix (unlike
        // client-side masking). BYOK-only: the proxy isn't verified to forward the beta header, and
        // sending context_management without it 400s.
        if (request.ContextEditing is null || request.ContextEditing.TriggerInputTokens <= 0)
        {
            return null;
        }

        if (_auth.Kind != AuthKind.Byok)
        {
            return null;
        }

        return new JsonObject
        {
            ["edits"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "clear_tool_uses_20250919",
                    ["trigger"] = new JsonObject { ["type"] = "input_tokens", ["value"] = request.ContextEditing.TriggerInputTokens },
                    ["keep"] = new JsonObject { ["type"] = "tool_uses", ["value"] = 5 },
                    ["clear_at_least"] = new JsonObject { ["type"] = "input_tokens", ["value"] = 2_000 }
                }
            }
        };
    }

    // Attach a rolling cache breakpoint to the last cacheable block of the last message so the whole
    // conversation prefix caches turn-over-turn — the system + last-tool breakpoints only cache the
    // fixed prefix. Uses the 3rd of Anthropic's 4 allowed breakpoints. Thinking blocks can't carry
    // cache_control, so skip past them.
    internal static void WithRollingCacheBreakpoint(JsonArray messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i] is not JsonObject m)
            {
                continue;
            }

            var contentNode = m["content"];
            if (contentNode is JsonValue value && value.TryGetValue<string>(out var s))
            {
                if (string.IsNullOrEmpty(s))
                {
                    continue;
                }

                m["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = s,
                        ["cache_control"] = new JsonObject { ["type"] = "ephemeral" }
                    }
                };
                return;
            }

            if (contentNode is JsonArray arr)
            {
                for (var j = arr.Count - 1; j >= 0; j--)
                {
                    if (arr[j] is not JsonObject block)
                    {
                        continue;
                    }

                    var type = block["type"]?.GetValue<string>();
                    if (type != "thinking" && type != "redacted_thinking")
                    {
                        block["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
                        return;
                    }
                }
            }
        }
    }

    private static JsonObject ToAnthropicMessage(AgentMessage message)
    {
        if (message.Role == "system")
        {
            throw new InvalidOperationException("System messages are passed via the request system field.");
        }

        if (message.Text is not null)
        {
            return new JsonObject { ["role"] = message.Role, ["content"] = message.Text };
        }

        var content = new JsonArray();
        foreach (var block in message.Blocks ?? [])
        {
            switch (block)
            {
                case TextBlock text:
                    content.Add(new JsonObject { ["type"] = "text", ["text"] = text.Text });
                    break;
                case ToolUseBlock tool:
                    content.Add(new JsonObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = tool.Id,
                        ["name"] = tool.Name,
                        ["input"] = JsonHelpers.DictionaryToJson(tool.Input)
                    });
                    break;
                case ToolResultBlock result:
                    content.Add(new JsonObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = result.ToolUseId,
                        ["content"] = result.Content,
                        ["is_error"] = result.IsError ? true : null
                    });
                    break;
                case ImageBlock image:
                    content.Add(new JsonObject
                    {
                        ["type"] = "image",
                        ["source"] = new JsonObject
                        {
                            ["type"] = "base64",
                            ["media_type"] = image.MediaType,
                            ["data"] = image.Data
                        }
                    });
                    break;
                case ThinkingBlock thinking:
                    if (!string.IsNullOrEmpty(thinking.RedactedData))
                    {
                        content.Add(new JsonObject
                        {
                            ["type"] = "redacted_thinking",
                            ["data"] = thinking.RedactedData
                        });
                    }
                    else if (!string.IsNullOrEmpty(thinking.Signature))
                    {
                        content.Add(new JsonObject
                        {
                            ["type"] = "thinking",
                            ["thinking"] = thinking.Text,
                            ["signature"] = thinking.Signature
                        });
                    }
                    break;
            }
        }

        return new JsonObject { ["role"] = message.Role, ["content"] = content };
    }

    private static CompletionResponse ParseResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var blocks = new List<ContentBlock>();
        if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                var type = block.GetProperty("type").GetString();
                if (type == "text")
                {
                    blocks.Add(new TextBlock(block.GetProperty("text").GetString() ?? ""));
                }
                else if (type == "tool_use")
                {
                    blocks.Add(new ToolUseBlock(
                        block.GetProperty("id").GetString() ?? "toolu",
                        block.GetProperty("name").GetString() ?? "",
                        block.TryGetProperty("input", out var input)
                            ? JsonHelpers.ToDictionary(input)
                            : new Dictionary<string, object?>(StringComparer.Ordinal)));
                }
                else if (type == "thinking")
                {
                    blocks.Add(new ThinkingBlock(
                        block.TryGetProperty("thinking", out var thinking) ? thinking.GetString() ?? "" : "",
                        block.TryGetProperty("signature", out var signature) ? signature.GetString() : null));
                }
                else if (type == "redacted_thinking")
                {
                    blocks.Add(new ThinkingBlock(
                        "",
                        RedactedData: block.TryGetProperty("data", out var data) ? data.GetString() : null));
                }
            }
        }

        var usageElement = root.GetProperty("usage");
        var usage = new CompletionUsage(
            GetInt(usageElement, "input_tokens"),
            GetInt(usageElement, "output_tokens"),
            GetInt(usageElement, "cache_read_input_tokens"),
            GetInt(usageElement, "cache_creation_input_tokens"));

        return new CompletionResponse(
            root.TryGetProperty("model", out var model) ? model.GetString() ?? "" : "",
            NormalizeStopReason(root.TryGetProperty("stop_reason", out var stop) ? stop.GetString() : null),
            blocks,
            usage);
    }

    private static int GetInt(JsonElement obj, string property) =>
        obj.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;

    private static string NormalizeStopReason(string? raw) =>
        raw switch
        {
            "end_turn" => "end_turn",
            "tool_use" => "tool_use",
            "max_tokens" => "max_tokens",
            "stop_sequence" => "stop_sequence",
            _ => "end_turn"
        };

    private static string Trim(string text, int max) => text.Length <= max ? text : text[..max];
}
