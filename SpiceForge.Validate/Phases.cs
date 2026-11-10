using System.Diagnostics;

/// <summary>Wall-clock time spent in named phases of a validation run, for finding where it goes.</summary>
internal static class Phases
{
    private static readonly Dictionary<string, TimeSpan> totals = new();
    private static readonly object gate = new();

    public static T Time<T>(string phase, Func<T> work)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            return work();
        }
        finally
        {
            Add(phase, watch.Elapsed);
        }
    }

    public static void Time(string phase, Action work) => Time(phase, () => { work(); return 0; });

    public static void Add(string phase, TimeSpan elapsed)
    {
        lock (gate)
            totals[phase] = totals.GetValueOrDefault(phase) + elapsed;
    }

    /// <summary>Prints the phases, largest first, with their share of the total.</summary>
    public static void Report(TextWriter writer)
    {
        lock (gate)
        {
            if (totals.Count == 0)
                return;
            if (ParallelRunner.IsChild)
            {
                foreach (var (phase, time) in totals)
                    writer.WriteLine(FormattableString.Invariant($"{ParallelRunner.PhasePrefix}{phase}\t{time.TotalSeconds}"));
                return;
            }
            double all = totals.Values.Sum(t => t.TotalSeconds);
            writer.WriteLine($"time by phase ({all:F1} s measured):");
            foreach (var (phase, time) in totals.OrderByDescending(p => p.Value))
                writer.WriteLine($"  {phase,-28}{time.TotalSeconds,8:F1} s{100 * time.TotalSeconds / all,6:F0}%");
        }
    }
}
