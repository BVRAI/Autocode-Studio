using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoCode.Engine.Auth;

namespace AutoCode.Engine.Llm;

public sealed class OpenAiCompatProvider : ILlmProvider
{
    private static readonly HttpClient Http = new();

    private readonly string _defaultBaseUrl;
    private readonly AuthMode _auth;
    private readonly bool _isOpenRouter;

    public OpenAiCompatProvider(string name, string defaultBaseUrl, AuthMode auth, bool isOpenRouter = false)
    {
        Name = name;
        _defaultBaseUrl = defaultBaseUrl;
        _auth = auth;
        _isOpenRouter = isOpenRouter;
    }

    public string Name { get; }

    private ReasoningEcho ReasoningEchoMode =>
        _isOpenRouter ? ReasoningEcho.ReasoningDetails :
        Name.Equals("xai", StringComparison.OrdinalIgnoreCase) ? ReasoningEcho.ReasoningContent :
        ReasoningEcho.None;

    public async Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken)
    {
        if (_auth.Kind == AuthKind.Missing)
        {
            throw new InvalidOperationException($"{Name} credentials missing. Set {AuthResolver.EnvVarFor(Name)} or configure proxy access.");
        }

        var baseUrl = _auth.Kind == AuthKind.Proxy ? _auth.BaseUrl! : _defaultBaseUrl;
        using var http = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/chat/completions");
        http.Content = new StringContent(BuildBody(request, ReasoningEchoMode).ToJsonString(), Encoding.UTF8, "application/json");
        http.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (_auth.Kind == AuthKind.Byok)
        {
            http.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _auth.ApiKey);
        }
        else
        {
            http.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _auth.ProxyToken);
        }

        if (_isOpenRouter)
        {
            http.Headers.TryAddWithoutValidation("http-referer", "https://github.com/automax/autocode-gui");
            http.Headers.TryAddWithoutValidation("x-title", "AutoCode GUI");
        }

        using var response = await Http.SendAsync(http, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"{Name} {(int)response.StatusCode}: {Trim(text, 500)}");
        }

        return ParseResponse(text);
    }

    private static JsonObject BuildBody(CompletionRequest request, ReasoningEcho reasoningEcho)
    {
        var systemText = string.IsNullOrWhiteSpace(request.SystemVolatile)
            ? request.System
            : request.System + "\n" + request.SystemVolatile;
        var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = systemText } };
        foreach (var message in request.Messages)
        {
            foreach (var converted in ToOpenAiMessages(message, reasoningEcho))
            {
                messages.Add(converted);
            }
        }

        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["messages"] = messages
        };

        if (IsOpenAiReasoningModel(request.Model))
        {
            body["max_completion_tokens"] = request.MaxTokens;
        }
        else
        {
            body["max_tokens"] = request.MaxTokens;
            body["temperature"] = request.Temperature;
        }

        // Arm reasoning when requested, for the families that accept the param (o-series, gpt-5).
        // Others (grok, llama routes) never see it — the catalog doesn't mark them SupportsThinking,
        // so request.Thinking is null for them.
        if (request.Thinking is not null && (IsOpenAiReasoningModel(request.Model) || IsGpt5Model(request.Model)))
        {
            body["reasoning_effort"] = "medium";
        }

        if (request.Tools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var tool in request.Tools)
            {
                tools.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["parameters"] = JsonHelpers.CloneNode(tool.InputSchema)
                    }
                });
            }

            body["tools"] = tools;
            body["tool_choice"] = "auto";
        }

        return body;
    }

    private static IEnumerable<JsonObject> ToOpenAiMessages(AgentMessage message, ReasoningEcho reasoningEcho)
    {
        if (message.Text is not null)
        {
            yield return new JsonObject { ["role"] = message.Role, ["content"] = message.Text };
            yield break;
        }

        var blocks = message.Blocks ?? [];
        if (message.Role == "assistant")
        {
            var assistantText = string.Join("\n", blocks.OfType<TextBlock>().Select(t => t.Text));
            var outMessage = new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = string.IsNullOrWhiteSpace(assistantText) ? null : assistantText
            };
            var toolCalls = new JsonArray();
            foreach (var tool in blocks.OfType<ToolUseBlock>())
            {
                toolCalls.Add(new JsonObject
                {
                    ["id"] = tool.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["arguments"] = JsonHelpers.DictionaryToJson(tool.Input).ToJsonString()
                    }
                });
            }

            if (toolCalls.Count > 0)
            {
                outMessage["tool_calls"] = toolCalls;
            }

            var thinking = blocks.OfType<ThinkingBlock>().ToList();
            if (thinking.Count > 0 && reasoningEcho != ReasoningEcho.None)
            {
                if (reasoningEcho == ReasoningEcho.ReasoningContent)
                {
                    var reasoningTextEcho = string.Join("\n", thinking.Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)));
                    if (!string.IsNullOrWhiteSpace(reasoningTextEcho))
                    {
                        outMessage["reasoning_content"] = reasoningTextEcho;
                    }
                }
                else
                {
                    var details = new JsonArray();
                    foreach (var node in thinking.Select(t => t.Opaque).Where(n => n is JsonArray).Cast<JsonArray>())
                    {
                        foreach (var item in node)
                        {
                            details.Add(item?.DeepClone());
                        }
                    }

                    if (details.Count > 0)
                    {
                        outMessage["reasoning_details"] = details;
                    }
                    else
                    {
                        var fallbackReasoningText = string.Join("\n", thinking.Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)));
                        if (!string.IsNullOrWhiteSpace(fallbackReasoningText))
                        {
                            outMessage["reasoning"] = fallbackReasoningText;
                        }
                    }
                }
            }

            yield return outMessage;
            yield break;
        }

        var textParts = blocks.OfType<TextBlock>().Select(t => t.Text).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        var imageParts = blocks.OfType<ImageBlock>().ToList();
        if (textParts.Count > 0 || imageParts.Count > 0)
        {
            if (imageParts.Count == 0)
            {
                yield return new JsonObject { ["role"] = "user", ["content"] = string.Join("\n", textParts) };
            }
            else
            {
                var content = new JsonArray();
                if (textParts.Count > 0)
                {
                    content.Add(new JsonObject { ["type"] = "text", ["text"] = string.Join("\n", textParts) });
                }

                foreach (var image in imageParts)
                {
                    content.Add(new JsonObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new JsonObject { ["url"] = $"data:{image.MediaType};base64,{image.Data}" }
                    });
                }

                yield return new JsonObject { ["role"] = "user", ["content"] = content };
            }
        }

        foreach (var result in blocks.OfType<ToolResultBlock>())
        {
            yield return new JsonObject
            {
                ["role"] = "tool",
                ["tool_call_id"] = result.ToolUseId,
                ["content"] = result.Content
            };
        }
    }

    private static CompletionResponse ParseResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var choice = root.GetProperty("choices").EnumerateArray().FirstOrDefault();
        if (choice.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidOperationException("OpenAI-compatible response has no choices.");
        }

        var message = choice.GetProperty("message");
        var blocks = new List<ContentBlock>();
        var reasoningText = "";
        if (message.TryGetProperty("reasoning_content", out var reasoningContent) && reasoningContent.ValueKind == JsonValueKind.String)
        {
            reasoningText = reasoningContent.GetString() ?? "";
        }
        else if (message.TryGetProperty("reasoning", out var reasoning) && reasoning.ValueKind == JsonValueKind.String)
        {
            reasoningText = reasoning.GetString() ?? "";
        }

        JsonNode? opaque = null;
        if (message.TryGetProperty("reasoning_details", out var details) && details.ValueKind == JsonValueKind.Array)
        {
            opaque = JsonNode.Parse(details.GetRawText());
        }

        if (!string.IsNullOrWhiteSpace(reasoningText) || opaque is not null)
        {
            blocks.Add(new ThinkingBlock(reasoningText, Opaque: opaque));
        }

        if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
        {
            var text = content.GetString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                blocks.Add(new TextBlock(text));
            }
        }

        if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
        {
            foreach (var call in toolCalls.EnumerateArray())
            {
                var function = call.GetProperty("function");
                var args = function.TryGetProperty("arguments", out var rawArgs)
                    ? JsonHelpers.JsonStringToDictionary(rawArgs.GetString())
                    : new Dictionary<string, object?>(StringComparer.Ordinal);
                blocks.Add(new ToolUseBlock(
                    call.GetProperty("id").GetString() ?? "call",
                    function.GetProperty("name").GetString() ?? "",
                    args));
            }
        }

        var usage = root.TryGetProperty("usage", out var usageElement)
            ? new CompletionUsage(
                GetInt(usageElement, "prompt_tokens"),
                GetInt(usageElement, "completion_tokens"),
                usageElement.TryGetProperty("prompt_tokens_details", out var tokenDetails)
                    ? GetInt(tokenDetails, "cached_tokens")
                    : 0)
            : new CompletionUsage(0, 0);

        return new CompletionResponse(
            root.TryGetProperty("model", out var model) ? model.GetString() ?? "" : "",
            NormalizeStopReason(choice.TryGetProperty("finish_reason", out var finish) ? finish.GetString() : null),
            blocks,
            usage);
    }

    private static int GetInt(JsonElement obj, string property) =>
        obj.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;

    private static bool IsOpenAiReasoningModel(string model) =>
        System.Text.RegularExpressions.Regex.IsMatch(model, @"^(openai/)?o\d", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static bool IsGpt5Model(string model) =>
        System.Text.RegularExpressions.Regex.IsMatch(model, @"^(openai/)?gpt-5", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static string NormalizeStopReason(string? raw) =>
        raw switch
        {
            "tool_calls" => "tool_use",
            "length" => "max_tokens",
            "stop" => "end_turn",
            "content_filter" => "error",
            _ => "end_turn"
        };

    private static string Trim(string text, int max) => text.Length <= max ? text : text[..max];

    private enum ReasoningEcho
    {
        None,
        ReasoningContent,
        ReasoningDetails
    }
}
