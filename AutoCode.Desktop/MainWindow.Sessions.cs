using System.IO;
using System.Linq;
using System.Windows;
using AutoCode.Desktop.Misc;
using AutoCode.Desktop.ViewModels;
using AutoCode.Engine.Agent;
using AutoCode.Engine.Auth;
using AutoCode.Engine.Backends;
using AutoCode.Engine.Llm;
using AutoCode.Engine.Session;
using AutoCode.Engine.Tools;

namespace AutoCode.Desktop;

// Sessions: project picking, workspace lifecycle, engine-loop wiring, sidebar, file tree, usage.
// Each workspace is a WorkspaceSession kept live in SessionManager; switching is instant + state-preserving.
public partial class MainWindow
{
    private async void ChooseProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose project root",
            InitialDirectory = Directory.Exists(_vm.ProjectRoot) ? _vm.ProjectRoot : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };

        if (dialog.ShowDialog(this) == true)
        {
            await StartNewSession(dialog.FolderName);
        }
    }

    private async void NewSession_Click(object sender, RoutedEventArgs e)
    {
        SessionPopup.IsOpen = false;
        await StartNewSession(_vm.Active?.ProjectRoot ?? DefaultProjectRoot());
    }

    private void ClearConversation_Click(object sender, RoutedEventArgs e)
    {
        SessionPopup.IsOpen = false;
        var session = _vm.Active;
        if (session is null)
        {
            return;
        }

        session.Conversation.Clear();
        session.Timeline.Clear();
        ClearPlan(session);
        session.Backend?.ClearConversation();
        ResetTurnState(session);
    }

    private async Task StartNewSession(string projectRoot, string agentId = "builtin")
    {
        if (!Directory.Exists(projectRoot))
        {
            return;
        }

        SaveModelToConfig();
        var sessionId = SessionIds.NewId();
        var sessionDir = SessionIndex.SessionDir(sessionId);
        var session = CreateSession(sessionId, sessionDir, Path.GetFullPath(projectRoot), agentId);
        session.Status = "ready";
        session.ChatTitle = "New session";
        session.ChatSubtitle = "";
        _vm.Sessions.Activate(session);

        // Don't persist an unused session. It stays live in the WORKSPACES list (bound to
        // SessionManager) but isn't written to disk until the first prompt is sent
        // (UpdateActiveSessionTitle -> WriteSidecar), so empty "New session" tabs never clutter the
        // saved PROJECTS history. (SessionIndex.LoadAll skips session folders with no session.json.)
        RebuildSidebar(sessionId);
        RefreshUsage(session);
        RefreshFiles(session);

        if (_config.AutoWorktree)
        {
            await PrepareWorktreeAsync(session);
        }
    }

    /// <summary>Create this session's git worktree + branch and repoint its agent at it. Falls back to
    /// the project folder (no isolation) when it isn't a git repo or git fails.</summary>
    private async Task PrepareWorktreeAsync(WorkspaceSession session)
    {
        if (session.Context is null)
        {
            return;
        }

        var repoRoot = await GitService.RepoRootAsync(session.ProjectRoot);
        if (repoRoot is null)
        {
            session.Conversation.Add(new NoticeBlock { Title = "Auto-branch", Detail = "Not a git repository — running in the project folder without an isolated branch." });
            return;
        }

        session.IsPreparing = true;
        session.Status = "preparing branch…";
        try
        {
            var baseBranch = await GitService.CurrentBranchAsync(repoRoot) ?? "main";
            var shortId = session.Id.Length > 8 ? session.Id[^8..] : session.Id;
            var branch = $"autocode/{shortId}";
            var worktreePath = Path.Combine(Engine.Auth.Paths.DataDirectory(), "worktrees", session.Id);

            var res = await GitService.CreateWorktreeAsync(repoRoot, worktreePath, branch);
            if (!res.Ok)
            {
                session.Conversation.Add(new NoticeBlock { Title = "Auto-branch failed", Detail = res.Message + "\n\nRunning in the project folder instead." });
                return;
            }

            session.BaseBranch = baseBranch;
            session.Branch = branch;
            session.WorktreePath = worktreePath;
            session.Context = session.Context.WithProjectRoot(worktreePath);
            UpdateSessionMeta(session);
            WriteSidecar(session);
            RefreshFiles(session);
        }
        finally
        {
            session.IsPreparing = false;
            session.Status = "ready";
        }
    }

    /// <summary>Persist the session sidecar (grouped by repo root) including any git worktree fields.</summary>
    private void WriteSidecar(WorkspaceSession session)
    {
        if (session.Context is null)
        {
            return;
        }

        SessionIndex.Write(session.SessionDir, new SessionSidecar(
            session.Id,
            string.IsNullOrWhiteSpace(session.ChatTitle) ? "New session" : session.ChatTitle,
            session.ProjectRoot,
            $"{session.Context.Model.Provider}/{session.Context.Model.Model}",
            session.StartedAt,
            session.Branch,
            session.WorktreePath,
            session.BaseBranch));
    }

    /// <summary>Commit + merge the active session's branch back into its base, then notify.</summary>
    private async void MergeActive()
    {
        var session = _vm.Active;
        if (session?.Branch is null || session.WorktreePath is null)
        {
            return;
        }

        var repoRoot = await GitService.RepoRootAsync(session.ProjectRoot);
        if (repoRoot is null)
        {
            return;
        }

        await GitService.CommitAllAsync(session.WorktreePath, session.ChatTitle);
        var res = await GitService.MergeAsync(repoRoot, session.Branch);
        session.Conversation.Add(new NoticeBlock
        {
            Title = res.Ok ? "Merged" : "Merge failed",
            Detail = res.Ok ? $"Merged {session.Branch} into {session.BaseBranch}." : res.Message,
        });
        ScrollChatToEnd();
    }

    /// <summary>Build a fresh WorkspaceSession (context + wired agent backend) for the given project root.</summary>
    private WorkspaceSession CreateSession(string sessionId, string sessionDir, string projectRoot, string agentId = "builtin")
    {
        Directory.CreateDirectory(sessionDir);
        var dataDir = Engine.Auth.Paths.DataDirectory();
        var session = new WorkspaceSession
        {
            Id = sessionId,
            SessionDir = sessionDir,
            ProjectRoot = projectRoot,
            StartedAt = DateTimeOffset.Now,
            AgentId = agentId,
        };
        session.Context = new SessionContext(
            sessionId,
            projectRoot,
            dataDir,
            sessionDir,
            new ModelConfig(_vm.Provider, _vm.Model),
            session.StartedAt,
            _vm.Mode);

        WireLoop(session);
        UpdateSessionMeta(session);
        return session;
    }

    /// <summary>(Re)build the agent backend for a session, capturing it in the event/approval callbacks.
    /// Today this is always the built-in engine; the <see cref="IAgentBackend"/> seam lets a future
    /// per-workspace picker swap in an external CLI agent (Claude Code / Codex) with no shell change.</summary>
    private void WireLoop(WorkspaceSession session)
    {
        // External CLI agents (Claude Code / Codex) run in the worktree on the user's subscription and
        // parse into the same event stream — they only need the emit callback.
        if (session.AgentId == "claude-code")
        {
            session.Backend = new ClaudeCodeBackend(evt => EmitAsync(session, evt));
            return;
        }

        var store = new TranscriptStore(session.SessionDir);
        var checkpoints = new CheckpointStore(session.SessionDir);
        var router = new AutoCode.Engine.Llm.LlmRouter(new AuthResolver(_config, ProxyTokenProvider));
        var registry = new ToolRegistry(_config);
        var loop = new AgentLoop(
            _config, store, checkpoints, router, registry,
            evt => EmitAsync(session, evt),
            (req, ct) => ApproveToolAsync(session, req, ct),
            ConfirmAsync,
            ChooseAsync);
        session.Backend = new BuiltinAgentBackend(loop);
    }

    private void UpdateSessionMeta(WorkspaceSession session)
    {
        if (session.Context is null)
        {
            return;
        }

        session.SessionId = session.Context.SessionId;
        session.SessionModel = $"{session.Context.Model.Provider}/{session.Context.Model.Model}";
        session.SessionRoot = session.ProjectRootShort;
    }

    private void RebuildSidebar(string? activeId)
    {
        _vm.Projects.Clear();
        var now = DateTimeOffset.Now;
        var groups = SessionIndex.LoadAll()
            .GroupBy(s => s.ProjectRoot, StringComparer.OrdinalIgnoreCase)
            .Select(g => new { Root = g.Key, Items = g.OrderByDescending(s => s.StartedAt).ToList(), Latest = g.Max(s => s.StartedAt) })
            .OrderByDescending(g => g.Latest)
            .ToList();

        foreach (var g in groups)
        {
            var project = new ProjectNode { Name = LeafName(g.Root), Path = g.Root };
            project.ToggleCommand = new RelayCommand(_ => project.IsExpanded = !project.IsExpanded);
            var hasActive = false;
            foreach (var s in g.Items)
            {
                var node = new SessionNode
                {
                    Id = s.Id,
                    Title = string.IsNullOrWhiteSpace(s.Title) ? "Session" : s.Title,
                    ProjectRoot = s.ProjectRoot,
                    SessionDir = SessionIndex.SessionDir(s.Id),
                    RelativeTime = Converters.RelativeTimeConverter.Format(s.StartedAt, now),
                    IsActive = s.Id == activeId,
                    GitBranch = s.GitBranch,
                    GitWorktreePath = s.GitWorktreePath,
                    GitBaseBranch = s.GitBaseBranch,
                };
                if (node.IsActive) { hasActive = true; }
                var captured = node;
                node.OpenCommand = new RelayCommand(_ => OpenSession(captured));
                project.Sessions.Add(node);
            }

            project.IsExpanded = hasActive;
            _vm.Projects.Add(project);
        }
    }

    private void UpdateActiveSessionTitle(WorkspaceSession session, string prompt)
    {
        if (session.Context is null || (session.ChatTitle != "New session" && !string.IsNullOrEmpty(session.ChatTitle)))
        {
            return;
        }

        var title = prompt.Length > 48 ? prompt[..48] + "…" : prompt;
        session.ChatTitle = title;
        WriteSidecar(session);
        RebuildSidebar(session.Id);
    }

    private void OpenSession(SessionNode node)
    {
        // Already-live workspace: just refocus it (instant, state preserved).
        var existing = _vm.Sessions.FindById(node.Id);
        if (existing is not null)
        {
            _vm.Sessions.Activate(existing);
            RebuildSidebar(node.Id);
            _ = RefreshChangesAsync(existing);
            ScrollChatToEnd();
            return;
        }

        // Cold open: build it and rehydrate the conversation + engine context from the transcript.
        var session = CreateSession(node.Id, node.SessionDir, node.ProjectRoot);
        session.ChatTitle = string.IsNullOrWhiteSpace(node.Title) ? "Session" : node.Title;
        session.Status = "ready";

        // Reuse the session's existing git worktree if it's still on disk.
        if (!string.IsNullOrEmpty(node.GitWorktreePath) && Directory.Exists(node.GitWorktreePath) && session.Context is not null)
        {
            session.Branch = node.GitBranch;
            session.BaseBranch = node.GitBaseBranch;
            session.WorktreePath = node.GitWorktreePath;
            session.Context = session.Context.WithProjectRoot(node.GitWorktreePath);
            UpdateSessionMeta(session);
        }

        var history = SessionIndex.LoadTranscript(node.SessionDir);
        session.Backend?.LoadHistory(history);
        foreach (var (role, text) in history)
        {
            session.Conversation.Add(string.Equals(role, "user", StringComparison.OrdinalIgnoreCase)
                ? new UserBubbleBlock { Text = text }
                : new AssistantBlock { Text = text });
        }

        _vm.Sessions.Activate(session);
        RebuildSidebar(node.Id);
        RefreshUsage(session);
        RefreshFiles(session);
        _ = RefreshChangesAsync(session);
        ScrollChatToEnd();
    }

    // Live workspace switcher (the WORKSPACES list, bound to SessionManager.Sessions).
    private void ActivateWorkspace(WorkspaceSession session)
    {
        _vm.Sessions.Activate(session);
        RebuildSidebar(session.Id);
        _ = RefreshChangesAsync(session);
        ScrollChatToEnd();
    }

    private async void CloseWorkspace(WorkspaceSession session)
    {
        _vm.Sessions.Close(session);
        if (_vm.Active is null)
        {
            // Never leave zero workspaces open.
            await StartNewSession(session.ProjectRoot.Length > 0 ? session.ProjectRoot : DefaultProjectRoot());
        }
        else
        {
            RebuildSidebar(_vm.Active.Id);
        }
    }

    private void RefreshFiles(WorkspaceSession session)
    {
        session.Files.Clear();
        if (session.Context is null || !Directory.Exists(session.Context.ProjectRoot))
        {
            return;
        }

        try
        {
            foreach (var row in EnumerateFileNodes(session, session.Context.ProjectRoot, 0).Take(300))
            {
                session.Files.Add(row);
            }
        }
        catch
        {
            // Ignore file tree refresh failures.
        }
    }

    /// <summary>
    /// Recompute the session's changed-file list (the review surface). For a worktree session this is
    /// the net diff vs its base branch; otherwise the uncommitted working-tree changes. Best-effort —
    /// non-git projects simply show nothing. Must be called on the UI thread (mutates bound collection).
    /// </summary>
    private async Task RefreshChangesAsync(WorkspaceSession session)
    {
        var root = session.Context?.ProjectRoot;
        if (string.IsNullOrEmpty(root))
        {
            return;
        }

        try
        {
            var files = await GitService.ChangedFilesAsync(root, session.BaseBranch);
            session.Changes.Clear();
            foreach (var f in files.Take(200))
            {
                session.Changes.Add(new ChangeItem { Status = f.Status, Path = f.Path });
            }

            session.HasChanges = session.Changes.Count > 0;
        }
        catch
        {
            // Ignore — review surface stays empty when git isn't available here.
        }
    }

    private IEnumerable<FileNode> EnumerateFileNodes(WorkspaceSession session, string root, int depth)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(root)
                     .OrderBy(e => Directory.Exists(e) ? 0 : 1)
                     .ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(entry);
            if (Directory.Exists(entry))
            {
                if (ToolConstants.NoiseDirectories.Contains(name))
                {
                    continue;
                }

                yield return new FileNode { Name = name, Depth = depth, IsDirectory = true };
                if (depth < 3)
                {
                    foreach (var child in EnumerateFileNodes(session, entry, depth + 1).Take(80))
                    {
                        yield return child;
                    }
                }
            }
            else
            {
                var rel = TryRelative(session, entry);
                yield return new FileNode
                {
                    Name = name,
                    Depth = depth,
                    IsDirectory = false,
                    IsModified = rel is not null && session.ModifiedFiles.Contains(rel),
                };
            }
        }
    }

    private void RefreshUsage(WorkspaceSession session)
    {
        // Single source of truth for context-window size lives in the engine (handles the
        // [1m] long-context variant, per-provider families, etc.). Don't re-derive it here.
        var model = session.Context?.Model;
        var window = model is null ? 200_000 : ContextWindow.ContextWindowFor(model.Provider, model.Model);
        if (session.Backend is null)
        {
            session.SetUsage(0, 0, window);
            return;
        }

        var usage = session.Backend.CumulativeUsage;
        session.SetUsage(usage.InputTokens, usage.OutputTokens, window);
    }
}
