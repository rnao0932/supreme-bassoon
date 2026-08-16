using System.Collections.Concurrent;

namespace QbReclass.App;

/// <summary>
/// Runs work on one dedicated single-threaded-apartment thread.
/// </summary>
/// <remarks>
/// The QuickBooks request processor is an apartment-threaded COM component. Calling it from the
/// thread pool would work only by going through a COM marshalling proxy, and calling it from the
/// user-interface thread would freeze the window for the length of every batch. Owning one STA
/// thread gives the component the apartment it expects while leaving the interface responsive, and
/// it also serializes every QuickBooks call, which is what the spec asks for in section 16.
/// </remarks>
public sealed class StaWorker : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private bool _disposed;

    public StaWorker()
    {
        _thread = new Thread(Pump)
        {
            IsBackground = true,
            Name = "QuickBooks session (STA)",
        };

        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private void Pump()
    {
        foreach (var work in _queue.GetConsumingEnumerable())
        {
            work();
        }
    }

    public Task RunAsync(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);

        return RunAsync(() =>
        {
            work();
            return true;
        });
    }

    public Task<T> RunAsync<T>(Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        _queue.Add(() =>
        {
            try
            {
                completion.SetResult(work());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });

        return completion.Task;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.CompleteAdding();

        // Give in-flight QuickBooks work a moment to finish rather than tearing down its apartment.
        _thread.Join(TimeSpan.FromSeconds(30));
        _queue.Dispose();
    }
}
