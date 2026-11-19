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
    private Timer? _ticker;
    private Func<bool>? _tickWhile;

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

    /// <summary>
    /// Re-renders once a second for as long as <paramref name="condition"/> holds, for text that counts
    /// ("Refreshed 3 s ago"). Call it again whenever the condition may have become true; it stops itself.
    /// </summary>
    protected void TickWhile(Func<bool> condition)
    {
        _tickWhile = condition;
        if (_ticker is null && !_disposed && condition())
        {
            _ticker = new Timer(_ => OnTick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }
    }

    private void OnTick()
    {
        try
        {
            _ = InvokeAsync(() =>
            {
                if (_disposed)
                {
                    return;
                }

                if (_tickWhile?.Invoke() != true)
                {
                    _ticker?.Dispose();
                    _ticker = null;
                }

                StateHasChanged();
            });
        }
        catch (Exception)
        {
            // The circuit went away between ticks.
        }
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
        _ticker?.Dispose();
        Notifier.Changed -= OnChanged;
        GC.SuppressFinalize(this);
    }
}
