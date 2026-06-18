# AutoCode Studio — Development Guidelines

## What this is
AutoCode Studio is a native WPF agentic **coding harness** heading toward a Codex × Conductor
hybrid: a great built-in agent **and** an orchestrator that runs multiple agents in parallel, each
isolated on its own git branch/worktree.
- `AutoCode.Engine` — headless agent runtime (`net8.0`, **no WPF**). A hand-port of the
  TypeScript core engine in the sibling repo `../autocode`.
- `AutoCode.Desktop` — the WPF shell (`net8.0-windows`). Replaces the TS CLI surface.
- `AutoCode.Engine.Tests` — MSTest unit tests for the engine.

## IMPORTANT: User context
Gregory is a commercial-real-estate expert and "vibe coder," **not** a software engineer. He
drives product/domain direction; you own and protect the architecture. He cannot track code
dependencies himself, so **strict modularity is the top priority — above feature velocity.** A
clean foundation grown in the right order matters more than reaching the end goal fast. Your job:
- PROTECT the dependency structure (below). If a change would couple two modules, duplicate instead.
- PUSH BACK on tech debt; explain trade-offs simply. Prefer the boring, explicit, maintainable option.
- The goal is a codebase an AI can still comprehend at 1,000,000 lines.

## Primary principle: extreme modularity (build as if 10× the size)
- **Self-contained modules; minimize cross-module dependencies.** Duplication is acceptable and
  often preferred over a shared abstraction that couples two areas.
- Avoid "core utility" grab-bags imported everywhere, deep inheritance, and clever DI graphs.
- **One class per file. Explicit names** (`SessionManager`, not `Helper`).
- A 2,500-line self-contained file beats a 300-line file with 10 dependencies — BUT a file that
  mixes many *unrelated* concerns is the bad kind of big; split those by concern.

## Module map & dependency rules (KEEP THIS TRUE — it is the modularity ledger)
Dependencies flow one way. Never reverse an arrow.
```
AutoCode.Engine (net8.0, portable, no WPF, depends on nothing in Desktop)
  Agent/ (AgentLoop, PromptBuilder, ProjectContext, AgentEvents, AgentModels)
  Backends/ (IAgentBackend seam + BuiltinAgentBackend + ClaudeCodeBackend — see "Agent backends")
  Llm/   (LlmRouter, AnthropicProvider, OpenAiCompatProvider, Pricing, ContextWindow, ModelCatalog)
  Tools/ (ITool + ToolRegistry + one tool per file; ToolArgs.RunProcessAsync)
  Session/ (SessionContext, TranscriptStore, CheckpointStore, GitService)
  Safety/ (SafetyPolicy, PathSafety)   Auth/ (AuthResolver, AutocodeConfig, Paths, ConfigStore)
        ▲
        │ (Desktop → Engine only)
AutoCode.Desktop (net8.0-windows, WPF)
  ViewModels/  pure state, NO reference to MainWindow:
      WorkspaceSession (one tab's state) · SessionManager (live set + Active) ·
      MainViewModel (app-level state + façade over Active) · PanelModels · ConversationBlocks · Mvvm
  MainWindow.*.cs  the controller layer (behavior, not state); references ViewModels + Engine;
      operates on a given WorkspaceSession. Split by concern (see below).
  Views/Styles/Themes/Icons  XAML; bind to MainViewModel (the façade).
  Services/ (LocalizationService, Loc) · Misc/ (SessionIndex, InlineParser, SessionIds) ·
  Voice/ · Auth/ (FirebaseAuthService, SubscriptionService) · Controls/ (IconGlyph)  — leaf modules.
```
Hard rules: **(1)** Engine never references Desktop. **(2)** ViewModels never reference MainWindow.
**(3)** Per-session state lives on `WorkspaceSession` (+ a façade forwarder on `MainViewModel`);
app-level state lives on `MainViewModel`. **(4)** Model metadata only in the engine
(`ModelCatalog`/`Pricing`/`ContextWindow`). **(5)** Colors/sizes via `DynamicResource`; user-facing
strings via `Loc`/`{DynamicResource L_*}`; engine→LLM strings stay English.

## The two boundaries (read before adding anything)
**1. TS core ↔ C# engine — a cross-language mirror.** `../autocode` (TypeScript) is the source of
truth. `AutoCode.Engine` mirrors its *portable* modules 1:1 in structure/naming. Port the portable
layer; **never** port the terminal surface (`repl/`, `cli.ts`) — the WPF shell replaces it.

**2. C# engine ↔ WPF desktop — the callback seam.** The engine is headless and **must not reference
WPF/Windows-only APIs** (keep it on plain `net8.0`). It talks to the shell only through four
delegates injected into `AgentLoop`: `EmitAsync`, `ApproveToolAsync`, `ConfirmAsync`, `ChooseAsync`.
Desktop owns all UI and marshals events via the `Dispatcher`.

**Where does a feature go?** Agent capability the model calls → an `ITool` in the **engine**.
Platform-specific capability (computer-use UIA, embedded WebView2) → `ITool` in **Desktop**,
registered into the engine's `ToolRegistry` at startup. Pure UI (editor, voice, theming, i18n) →
**Desktop only**.

## Multi-workspace architecture (per-session state)
- A **`WorkspaceSession`** (`ViewModels/WorkspaceSession.cs`) is one open tab: its engine
  (`SessionContext` + `AgentLoop` + `RunCts`), its bindable view-state (conversation, timeline,
  plan, files, usage, approval, status, title), its per-turn state, and — in auto-branch mode — its
  git `Branch`/`WorktreePath`/`BaseBranch`.
- **`SessionManager`** (`ViewModels/SessionManager.cs`) owns the live set + the `Active` one +
  `ActiveChanged`. Sessions stay in memory, so switching tabs is instant and state-preserving.
- **`MainViewModel` is a façade.** App-level state (theme, voice, account, sidebar `Projects`,
  layout, model picker) lives on it directly. Per-session bound properties (`Conversation`,
  `ChatTitle`, `Timeline`, `Plan`, `Files`, usage, `Approval`, `HasWorktree`, `Branch`, …)
  **forward to `Active`**; it relays `Active.PropertyChanged` and raises-all on switch. So XAML binds
  the same names and they follow the active session. **To add per-session UI state:** put it on
  `WorkspaceSession` + add a one-line forwarder on `MainViewModel` — no XAML change.
- Engine callbacks are built per session (closures capture the `WorkspaceSession`) → already
  concurrency-ready. One agent runs at a time today (Phase 3 lifts the guard). UI-only side effects
  (scroll, auto-open Plan) fire only for the active session.

## Git isolation (auto-branch)
- **`GitService`** (`AutoCode.Engine/Session/GitService.cs`, portable) wraps git via
  `ToolArgs.RunProcessAsync`: repo-root, current-branch, worktree-add, commit-all, merge, worktree-remove.
- Toggle `AutocodeConfig.AutoWorktree` (Settings → "Isolate sessions on a git branch", default off).
  When on, a new session gets `git worktree add -b autocode/<id>` under
  `%LocalAppData%/autocode-gui/worktrees/<id>`, and its `Context.ProjectRoot` points there — so the
  agent's edits are isolated with **no engine change** (all file tools resolve against
  `Context.ProjectRoot`). Per-turn auto-commit; a Merge button folds the branch back. Branch/worktree
  persist in the session sidecar (`SessionIndex`) and are reused on reopen.

## MainWindow partials (don't grow the core file)
`MainWindow.xaml.cs` (ctor + lifecycle + composer + shared helpers) is split by concern:
`.Account.cs`, `.WindowChrome.cs`, `.Layout.cs`, `.Menus.cs`, `.Sessions.cs`, `.EngineEvents.cs`,
`.Approvals.cs`, `.Voice.cs`. New feature areas get their **own partial or a small Desktop service** —
never pile onto the core file.

## Side-panel modules (`Views/Panels/`)
The right panel is a host that just switches self-contained panel UserControls by `PanelTab`:
`WorkspacePanel`, `RunPanel`, `PlanPanel`, `ChangesPanel` (`Views/Panels/*.xaml`). Each inherits the `MainViewModel`
façade as DataContext and binds to it; interactive actions are **bound commands** on `MainViewModel`
(`MergeCommand`, `Accept/Decline/ReviseApprovalCommand`, `OpenFileCommand`) wired in the MainWindow
ctor — panels never reference MainWindow. **Add a panel** = new UserControl + a tab toggle in the
header + a `DataTrigger` on `PanelTab` in the host. Don't add panel bodies inline to MainWindow.xaml.

## Engine internals you must not casually break
- **`LlmRouter`** — single entry point for all LLM calls (retry + backoff; per-provider translation).
  Providers each own a `static HttpClient`; proxy/BYOK resolved **live per request** by `AuthResolver`
  (so config changes apply without rebuilding loops — no `IHttpClientProvider` chokepoint).
- **`AgentLoop`** — mode-gating, per-tool approval, loop detection, auto-compaction, cost backstop,
  verify-and-fix loop, retry caps, `<external_untrusted_content>` wrapping. Emits `AgentEvent`s
  (incl. `PlanEvent` for the todo checklist). Keep parity with the TS `AgentLoop`.
- **Safety:** `SafetyPolicy` + `PathSafety` gate destructive shell commands and out-of-root writes.

## Agent backends (`AutoCode.Engine/Backends/` — the orchestrator seam)
A workspace's turns are driven by an **`IAgentBackend`**, not the `AgentLoop` directly — this is the
seam that lets AutoCode run **other** agents, the Conductor differentiator. Contract: `SubmitAsync`
(one turn, emits `AgentEvent`s) · `Cancel` · `CumulativeUsage` · `LoadHistory` · `ClearConversation`.
- **`BuiltinAgentBackend`** — thin adapter over `AgentLoop` (the native engine). Default.
- **`ClaudeCodeBackend`** — spawns the `claude` CLI headlessly (`-p --output-format stream-json
  --verbose --dangerously-skip-permissions`) in the workspace's `Context.ProjectRoot`, parses its
  NDJSON (`system/init`→session id, `assistant.content[]`→text/`tool_use`, `user`→`tool_result`,
  `result`→usage/error) into the **same** `AgentEvent` stream — so an external agent renders in the UI
  identically with **zero UI change**. (Codex is the same shape with a different parser — next.)
- **Auth = subscription, not API.** External backends spawn the CLI with `ANTHROPIC_API_KEY` /
  `ANTHROPIC_AUTH_TOKEN` **removed from the child env** so the CLI uses the user's `claude login` OAuth
  session (this is the cost win). `.cmd` shims need `cmd.exe /c` on Windows; invoke directly elsewhere.
- **Selection:** `WorkspaceSession.AgentId` ("builtin" | "claude-code" | "codex"); `WireLoop` builds the
  matching backend. External backends only need the `emit` callback (the CLI handles its own approvals,
  isolated in the worktree). Today set via the `--agent <id>` launch arg; a per-workspace picker is next.
- **Add a backend:** new `IAgentBackend` in `Backends/`, a `WireLoop` branch, an `AgentId` value. Keep
  each backend self-contained (its own parser/spawn) — don't prematurely share a "CLI runner".

## Adding a tool
Implement `ITool` (`Definition` = name + description + JSON `InputSchema`; `ExecuteAsync`). Register
in `ToolRegistry` (engine) or inject from Desktop (platform tools). Follow Anthropic's "writing
effective tools" guidance for descriptions.

## i18n (11 languages)
Strings in `AutoCode.Desktop/Strings/{code}.json` (flat). `en.json` is the baseline; the selected
language overlays it (missing keys fall back to English). `LocalizationService` builds an `L_`-keyed
`ResourceDictionary` and live-switches. C#: `Loc.T("Key")`/`Loc.F`. XAML: `{DynamicResource L_Key}`.
Languages: en, fr, es, de, ja, ko, zh-Hans, zh-Hant, yue, hi, pa (fr/es/de translated; rest fall back).
Add a string → `en.json` key + reference it; translate the others later.

## Style
- Colors and font sizes via `DynamicResource` (theme tokens in `Themes/*.xaml`) so theme + text-scale
  switch at runtime. Don't hardcode colors/sizes on elements.
- Icons are vector `Geometry` resources (`Icons.xaml`) via `IconGlyph` — **one exception:** the
  Settings gear uses the Segoe MDL2 `E713` glyph (deliberate match to Automax V6's gear).
- Active-toggle chips use `EditableBrush`/`EditableInkBrush` (cream + ink, both themes — matches V6).
- Responsive 13"→32"; proportional `Grid`; no `ViewBox` text scaling.

## WPF view ↔ viewmodel lifecycle (memory-leak prevention)
A view that subscribes to a long-lived VM/singleton event: subscribe with a **named** handler in
`Loaded` (`-=` then `+=`), unsubscribe in `Unloaded`. **Never** an anonymous lambda for a
VM/singleton event from a view (can't unsubscribe → leak). Child-control subscriptions may be lambdas.

## Process & editing
- **Use the Edit tool for code — never sed/PowerShell/Python scripts** (line edits drift/corrupt;
  exact-match edits fail loudly).
- **Don't commit or build unless asked.** Crash logs append to `%LocalAppData%/autocode-gui/crash.log`.
- Stuck >1–2h? Stop and look for a dramatically simpler approach (often a hosted/SDK option).
- Frontier work (agents, computer-use, browser automation): research SOTA from first-party/big-lab
  sources and borrow the proven approach before coding.

## Build & run
```powershell
dotnet build AutoCodeGui.sln
dotnet test  AutoCodeGui.sln
dotnet run   --project AutoCode.Desktop
```
Sessions persist under `%LocalAppData%\autocode-gui\sessions`; config at `~/.autocode-gui/config.json`.
**Launch into a project:** pass `--project <dir>` (repeatable) to open one workspace per folder at
startup instead of the default root (`MainWindow.StartupProjectRoots`). Used for "open-in-folder"
launches and for driving multiple isolated workspaces deterministically. Add `--agent <id>`
("builtin" | "claude-code" | "codex", `MainWindow.StartupAgentId`) to drive those startup workspaces
with an external CLI agent (until the per-workspace picker lands).

## Build order (foundation before features — this is the plan)
Each phase ships usable and leaves the dependency map intact. Don't sprint ahead.
- ✅ **Phase 0** — live plan/todo checklist (`PlanEvent`).
- ✅ **Phase 1** — `WorkspaceSession` + `SessionManager` + façade (live, state-preserving multi-workspace).
- ✅ **Phase 2** — git worktree per workspace + auto-branch + per-turn commit + Merge.
- ✅ **Phase 3** — multi-agent concurrency made usable:
  - ✅ live workspace switcher (the **WORKSPACES** sidebar section bound to `SessionManager.Sessions`,
    per-tab running status + close ✕, distinct from the disk PROJECTS history).
  - ✅ concurrent runs (each `WorkspaceSession` has its own loop/`RunCts`; creating/switching never
    cancels a running one — only Stop/Close/app-exit cancel).
  - ✅ review surface — `ChangesPanel` (`Views/Panels/`) lists the session's git changes
    (`GitService.ChangedFilesAsync`: net-vs-base for worktree sessions, else porcelain) with colored
    A/M/D/R badges; conditional **Changes** tab (visible when `HasChanges`); refreshed after each turn
    and on activate. The Merge action lives here.
  - **Verified live** (3 isolated temp projects, interleaved Full-access prompts via `--project`):
    concurrent runs (≥2 amber dots at once) + perfect isolation (each agent's files landed only in its
    own root; zero cross-contamination) + the Changes panel populated correctly.
  - Known limitation: `RunShellTool` background processes are a shared static; isolate per session when
    concurrent shell use becomes common.
- **Phase 4 (in progress)** — external-agent backends via the `IAgentBackend` seam (see "Agent backends"):
  - ✅ the seam + `BuiltinAgentBackend` (built-in engine behind it; verified behaviour-preserving).
  - ✅ `ClaudeCodeBackend` — runs the `claude` CLI in the worktree on **subscription auth** (API key
    stripped), stream-json parsed into the shared event stream. **Verified live:** a `--agent claude-code`
    workspace created a file via Claude Code's `Write` tool, rendered through the normal UI, usage shown.
  - **Remaining:** `CodexBackend` (same shape, `codex exec --json`); a **per-workspace agent picker**
    (replace the `--agent` launch arg); persist `AgentId` in the sidecar so reopened sessions keep their
    agent; surface the active agent in the workspace meta.
- **Phase 5** — merge/conflict UX polish; richer per-worktree diffs.
✅ **Modular side-panel host** — Workspace/Run/Plan/Changes extracted into self-contained `Views/Panels/*`
UserControls driven by commands; new panels slot in without coupling (see "Side-panel modules").
Deferred small items: per-session model/mode picker; per-session **delete** affordance.
Other backlog: inline code-editor view; computer-use (borrow V6's UIA set-of-marks); MCP (mirror TS `mcp/`).
