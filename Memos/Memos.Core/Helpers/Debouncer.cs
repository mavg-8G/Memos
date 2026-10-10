namespace Memos.Core.Helpers;

/// <summary>
/// Runs only the last action requested within a quiet period and cancels the ones it replaces.
/// Continuations resume on the caller's synchronization context (the UI thread in the app).
/// </summary>
public sealed class Debouncer : IDisposable
{
    private CancellationTokenSource? _cts;

    /// <summary>Completes when the most recently scheduled action has finished or been cancelled.</summary>
    public Task Pending { get; private set; } = Task.CompletedTask;

    public void Run(TimeSpan delay, Func<CancellationToken, Task> action)
    {
        Cancel();
        var cts = _cts = new CancellationTokenSource();
        Pending = RunCore(delay, action, cts.Token);
    }

    public void Cancel()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose() => Cancel();

    private static async Task RunCore(TimeSpan delay, Func<CancellationToken, Task> action, CancellationToken token)
    {
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, token);
            }

            await action(token);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
