using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Caches expensive reference results (such as tightly converged sweeps) on disk, keyed by the schematic's contents
/// and a description of the run. A reference only changes with the circuit and the run, not with the engine under
/// test, so validation runs can reuse it. Set <see cref="Refresh"/> to recompute everything.
/// </summary>
internal static class ReferenceCache
{
    /// <summary>Where cached results go; caching is off when null.</summary>
    public static string? Directory { get; set; }

    public static bool Refresh { get; set; }

    // Bump to invalidate every cached result, for example when how references are computed changes.
    private const string Version = "1";

    public static double[] GetOrCreate(string schematic, string run, Func<double[]> compute)
    {
        if (Directory == null)
            return compute();

        string path = Path.Combine(Directory, $"{Path.GetFileNameWithoutExtension(schematic)}-{Key(schematic, run)}.bin");
        if (!Refresh && File.Exists(path))
        {
            byte[] bytes = File.ReadAllBytes(path);
            var cached = new double[bytes.Length / sizeof(double)];
            Buffer.BlockCopy(bytes, 0, cached, 0, cached.Length * sizeof(double));
            return cached;
        }

        double[] result = compute();
        System.IO.Directory.CreateDirectory(Directory);
        var output = new byte[result.Length * sizeof(double)];
        Buffer.BlockCopy(result, 0, output, 0, output.Length);
        // Write then move, so parallel runs never read a half-written file.
        string temporary = path + "." + Environment.ProcessId + ".tmp";
        File.WriteAllBytes(temporary, output);
        File.Move(temporary, path, overwrite: true);
        return result;
    }

    private static string Key(string schematic, string run)
    {
        using var hash = SHA256.Create();
        byte[] contents = File.ReadAllBytes(schematic);
        byte[] description = Encoding.UTF8.GetBytes($"{Version}|{run}");
        hash.TransformBlock(contents, 0, contents.Length, null, 0);
        hash.TransformFinalBlock(description, 0, description.Length);
        return Convert.ToHexString(hash.Hash!, 0, 8);
    }
}
