using DevDash.Hub.Services;
using Microsoft.AspNetCore.Components;

namespace DevDash.Hub.Components;

/// <summary>
/// Base for pages that refresh themselves when a snapshot or GitHub poll lands. The re-render travels
/// to the browser over the Blazor circuit's SignalR connection.
/// </summary>
public abstract class LivePage : ComponentBase, IDisposable
{
    private int _reloadQueued;
    private bool _disposed;

    [Inject] protected DashboardNotifier Notifier { get; set; } = default!;

    [Inject] protected TimeProvider Time { get; set; } = default!;

    protected bool Loaded { get; private set; }

    protected DateTimeOffset Now => Time.GetUtcNow();

    protected abstract Task LoadAsync();

    protected override async Task OnInitializedAsync()
    {
        Notifier.Changed += OnChanged;
        await LoadAsync();
        Loaded = true;
    }

    protected async Task ReloadAsync()
    {
        await LoadAsync();
        StateHasChanged();
    }

    private void OnChanged(DashboardChange change)
    {
        // Coalesce bursts (several agents, or a snapshot and a poll together) into one reload.
        if (_disposed || Interlocked.Exchange(ref _reloadQueued, 1) == 1)
        {
            return;
        }

        _ = InvokeAsync(async () =>
        {
            try
            {
                await Task.Delay(200);
                Interlocked.Exchange(ref _reloadQueued, 0);
                if (!_disposed)
                {
                    await LoadAsync();
                    StateHasChanged();
                }
            }
            catch (Exception)
            {
                // The circuit went away mid-refresh; nothing to update.
                Interlocked.Exchange(ref _reloadQueued, 0);
            }
        });
    }

    public void Dispose()
    {
        _disposed = true;
        Notifier.Changed -= OnChanged;
        GC.SuppressFinalize(this);
    }
}
