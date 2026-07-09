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
        using var http = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/messages");
        http.Content = new StringContent(BuildBody(request).ToJsonString(), Encoding.UTF8, "application/json");
        http.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        http.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
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

    private static JsonObject BuildBody(CompletionRequest request)
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

        return new JsonObject
        {
            ["model"] = request.Model,
            ["max_tokens"] = request.MaxTokens,
            ["temperature"] = request.Temperature,
            ["system"] = system,
            ["tools"] = tools,
            ["messages"] = messages
        };
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
