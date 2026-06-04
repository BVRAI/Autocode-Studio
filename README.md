# AutoCode Studio

AutoCode Studio is a self-contained C#/.NET desktop port of the core AutoCode agent runtime. It is intended to be an independent open-source project: a native WPF app with an agentic back end, project-scoped file tools, command safety checks, approvals, sessions, verification, BYOK credentials, and configurable proxy access.

## Current Scope

- Native .NET 8 WPF desktop UI.
- Engine library separated from the desktop shell.
- Provider-neutral message and tool-call loop.
- Anthropic plus OpenAI-compatible providers: OpenAI, xAI, and OpenRouter.
- Configurable proxy routing through `AUTOCODE_GUI_PROXY_TOKEN` and `AUTOCODE_GUI_PROXY_URL`.
- Project-scoped tools: list/read/edit/write/delete, glob, grep, find symbol, shell, todos, ask user, skills, web fetch, and Brave web search.
- Modes: `planning`, `default`, `autocode`, and `admin`.
- Session transcript and tool logs under `%LocalAppData%\autocode-gui\sessions`.

## Proxy Access

The proxy layer is deliberately configurable for an independent OSS project.

Environment variables:

```powershell
$env:AUTOCODE_GUI_PROXY_TOKEN = "your-token"
$env:AUTOCODE_GUI_PROXY_URL = "https://your-proxy.example.com"
```

The app also accepts the legacy `AUTOMAX_PROXY_TOKEN` and `AUTOMAX_PROXY_URL` variables for compatibility. Saved settings live in `~/.autocode-gui/config.json`.

Provider requests route to:

```text
{proxyBaseUrl}/v1/{provider}
```

For example, Anthropic through a proxy uses `{proxyBaseUrl}/v1/anthropic/messages`; OpenAI-compatible providers use `{proxyBaseUrl}/v1/openai/chat/completions`, `{proxyBaseUrl}/v1/xai/chat/completions`, or `{proxyBaseUrl}/v1/openrouter/chat/completions`.

## BYOK

Environment variables take precedence:

- `ANTHROPIC_API_KEY`
- `OPENAI_API_KEY`
- `XAI_API_KEY`
- `OPENROUTER_API_KEY`
- `BRAVE_API_KEY`

Keys can also be saved in the Settings tab. This first version stores saved keys in config JSON; a production-ready OSS release should add OS keyring storage.

## Build

```powershell
dotnet build AutoCodeGui.sln
dotnet test AutoCodeGui.sln
dotnet run --project AutoCode.Desktop
```

## Notes

This is not a wrapper around the TypeScript CLI. The engine has been ported into C# so the desktop app can evolve independently. MCP, browser screenshot automation, richer model catalog/rates, and encrypted credential storage are natural next modules.
