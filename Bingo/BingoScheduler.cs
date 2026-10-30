namespace RadBot.Bingo;

/// <summary>Reconciles durable deadlines and starts explicitly configured successor windows, including after reconnect.</summary>
public sealed class BingoScheduler(BingoService service, BingoAnnouncementService announcements, TimeProvider clock) : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private readonly object _startGate = new();

    /// <summary>Starts one recovery loop, even if Discord Ready fires repeatedly.</summary>
    public Task StartAsync()
    {
        lock (_startGate) _loop ??= RunAsync(_stop.Token);
        return Task.CompletedTask;
    }

    /// <summary>Reconciles one persisted snapshot; exposed for deterministic offline recovery tests.</summary>
    public async Task TickAsync(CancellationToken cancellationToken = default)
    {
        await service.RecoverAsync(cancellationToken);
        await announcements.RefreshAsync(cancellationToken);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), clock);
        try
        {
            do
            {
                try { await TickAsync(cancellationToken); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { Console.Error.WriteLine($"Bingo recovery failed ({ex.GetType().Name}); state preserved, retrying."); }
            } while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    /// <summary>Cancels reconciliation; deadlines remain persisted for the next process.</summary>
    public void Dispose() => _stop.Cancel();
}
