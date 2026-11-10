using System.Diagnostics;
using System.Text.RegularExpressions;

/// <summary>
/// Runs one child process per circuit, several at a time, and prints their output in order. Processes rather than
/// threads, because nothing guarantees LiveSPICE's computer algebra is thread-safe. Children report their phase
/// times as <c>#phase</c> lines, which are added to this process's <see cref="Phases"/> instead of being printed.
/// </summary>
internal static class ParallelRunner
{
    public const string PhasePrefix = "#phase\t";

    /// <summary>Set in a child: report phases as lines for the parent rather than as a table.</summary>
    public static bool IsChild { get; set; }

    /// <summary>Runs each argument list as a child, <paramref name="jobs"/> at a time, and returns the total problems.</summary>
    public static int Run(IReadOnlyList<string[]> children, int jobs)
    {
        var results = new Task<(string Output, int Problems)>[children.Count];
        using var slots = new SemaphoreSlim(jobs);
        for (int i = 0; i < children.Count; i++)
        {
            string[] arguments = children[i];
            results[i] = Task.Run(async () =>
            {
                await slots.WaitAsync();
                try
                {
                    return await RunChild(arguments);
                }
                finally
                {
                    slots.Release();
                }
            });
        }

        int problems = 0;
        foreach (var result in results)
        {
            (string output, int count) = result.Result;
            Console.Write(output);
            problems += count;
        }
        return problems;
    }

    private static async Task<(string Output, int Problems)> RunChild(string[] arguments)
    {
        var start = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // Started through dotnet (dotnet run, dotnet x.dll), the child needs the assembly; otherwise run the app host.
        string host = Environment.ProcessPath!;
        if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(ParallelRunner).Assembly.Location);
        start.FileName = host;
        start.ArgumentList.Add("--child");
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start)!;
        Task<string> error = process.StandardError.ReadToEndAsync();
        var output = new System.Text.StringBuilder();
        int problems = 0;
        while (await process.StandardOutput.ReadLineAsync() is string line)
        {
            if (line.StartsWith(PhasePrefix))
            {
                string[] parts = line.Split('\t');
                Phases.Add(parts[1], TimeSpan.FromSeconds(double.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture)));
                continue;
            }
            Match match = Regex.Match(line, @"^(\d+) problem\(s\)\.$");
            if (match.Success)
            {
                problems += int.Parse(match.Groups[1].Value);
                continue;
            }
            output.AppendLine(line);
        }
        await process.WaitForExitAsync();
        string errors = await error;
        if (errors.Length > 0)
            output.Append(errors);
        if (process.ExitCode != 0 && problems == 0)
        {
            output.AppendLine($"  error: child exited with code {process.ExitCode}");
            problems++;
        }
        return (output.ToString(), problems);
    }
}
