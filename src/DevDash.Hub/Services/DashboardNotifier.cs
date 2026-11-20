namespace DevDash.Hub.Services;

public enum DashboardChange
{
    Snapshot,
    GitHub,
    Findings,
    Machines,
    Actions,

    /// <summary>An alert opened, cleared or was sent, or the alert settings changed.</summary>
    Alerts,
}

/// <summary>
/// In-process fan-out for "something changed". Blazor components subscribe and re-render, and the
/// update reaches the browser over the circuit's SignalR connection.
/// </summary>
public sealed class DashboardNotifier
{
    public event Action<DashboardChange>? Changed;

    public void Notify(DashboardChange change)
    {
        var handlers = Changed;
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<Action<DashboardChange>>())
        {
            try
            {
                handler(change);
            }
            catch (Exception)
            {
                // A circuit that is shutting down must not stop the others from updating.
            }
        }
    }
}
