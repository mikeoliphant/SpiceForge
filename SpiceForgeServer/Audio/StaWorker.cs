using System.Collections.Concurrent;

namespace SpiceForgeServer.Audio;

/// <summary>
/// A thread that runs work one item at a time, in a single-threaded COM apartment on Windows. ASIO drivers are COM
/// objects made for one thread: each is created, called and released on this one.
/// </summary>
public sealed class StaWorker : IDisposable
{
    private readonly BlockingCollection<Action> queue = new();
    private readonly Thread thread;

    public StaWorker(string name)
    {
        thread = new Thread(Loop) { IsBackground = true, Name = name };
        if (OperatingSystem.IsWindows())
            thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private void Loop()
    {
        foreach (Action work in queue.GetConsumingEnumerable())
            work();
    }

    public Task<T> Run<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Add(() =>
        {
            try { done.SetResult(work()); }
            catch (Exception ex) { done.SetException(ex); }
        });
        return done.Task;
    }

    public Task Run(Action work) => Run(() => { work(); return true; });

    /// <summary>Queues work without waiting for it; an exception it throws is passed to <paramref name="failed"/>.</summary>
    public void Post(Action work, Action<Exception>? failed = null)
    {
        if (queue.IsAddingCompleted)
            return;
        queue.Add(() =>
        {
            try { work(); }
            catch (Exception ex) { failed?.Invoke(ex); }
        });
    }

    public void Dispose() => queue.CompleteAdding();
}
