using System.Collections.ObjectModel;

namespace AutoCode.Desktop.ViewModels;

/// <summary>
/// Owns the live set of open <see cref="WorkspaceSession"/>s and the active one. Keeping sessions in
/// memory is what makes tab-switching instant and state-preserving. UI-framework-agnostic so the same
/// orchestration can back a future Mac shell.
/// </summary>
public sealed class SessionManager
{
    public ObservableCollection<WorkspaceSession> Sessions { get; } = [];

    public WorkspaceSession? Active { get; private set; }

    /// <summary>Raised after <see cref="Active"/> changes (argument is the new active session).</summary>
    public event Action<WorkspaceSession?>? ActiveChanged;

    public WorkspaceSession? FindById(string id) =>
        Sessions.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));

    public void Add(WorkspaceSession session)
    {
        if (!Sessions.Contains(session))
        {
            Sessions.Add(session);
        }
    }

    public void Activate(WorkspaceSession session)
    {
        if (ReferenceEquals(Active, session))
        {
            return;
        }

        Add(session);
        if (Active is not null)
        {
            Active.IsActive = false;
        }

        Active = session;
        session.IsActive = true;
        ActiveChanged?.Invoke(session);
    }

    public void Close(WorkspaceSession session)
    {
        var wasActive = ReferenceEquals(Active, session);
        session.RunCts?.Cancel();
        session.IsActive = false;
        Sessions.Remove(session);
        if (wasActive)
        {
            Active = Sessions.LastOrDefault();
            if (Active is not null)
            {
                Active.IsActive = true;
            }

            ActiveChanged?.Invoke(Active);
        }
    }
}
