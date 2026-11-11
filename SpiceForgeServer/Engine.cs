namespace SpiceForgeServer;

/// <summary>
/// Runs work that touches LiveSPICE's model one call at a time, off the socket threads. LiveSPICE's computer algebra
/// isn't known to be thread-safe (LessonsLearned.md, Validation), and nearly everything here reaches it: deserializing
/// components, laying out symbols, parsing values, building circuits.
/// </summary>
public static class Engine
{
    private static readonly SemaphoreSlim gate = new(1, 1);

    public static async Task<T> Run<T>(Func<T> work)
    {
        await gate.WaitAsync();
        try
        {
            return await Task.Run(work);
        }
        finally
        {
            gate.Release();
        }
    }
}
