using System.Diagnostics;

namespace SpiceForgeServer;

/// <summary>
/// Opens the front end: in the default browser, or in a window of its own. A window of its own is a Chromium browser's
/// app mode (Edge, Chrome, Chromium, Brave): no tabs or address bar, its own taskbar entry. It runs with a profile of
/// its own, in SpiceForge's settings folder, so the editor's local storage (its theme, remembered setups) lives there,
/// apart from the user's browsing. Without such a browser, the default browser opens instead.
/// </summary>
public static class EditorWindow
{
    /// <summary>Opens <paramref name="url"/>; returns what opened it, for the console.</summary>
    public static string Open(string url, OpenMode mode, string profileFolder)
    {
        if (mode == OpenMode.App && FindChromium() is string browser)
        {
            try
            {
                Directory.CreateDirectory(profileFolder);
                var start = new ProcessStartInfo(browser) { UseShellExecute = false };
                foreach (string argument in new[]
                {
                    $"--app={url}",
                    $"--user-data-dir={profileFolder}",
                    "--window-size=1400,900",
                    "--no-first-run",
                    "--no-default-browser-check",
                })
                    start.ArgumentList.Add(argument);
                Process.Start(start)?.Dispose();
                return $"a window of its own ({Path.GetFileNameWithoutExtension(browser)})";
            }
            catch (Exception)
            {
                // Fall back to the default browser.
            }
        }
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        return "the default browser";
    }

    /// <summary>A Chromium browser that has an app mode, or null.</summary>
    public static string? FindChromium()
    {
        if (OperatingSystem.IsWindows())
        {
            string[] programs =
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            };
            string[] browsers =
            {
                @"Microsoft\Edge\Application\msedge.exe",
                @"Google\Chrome\Application\chrome.exe",
                @"Chromium\Application\chrome.exe",
                @"BraveSoftware\Brave-Browser\Application\brave.exe",
            };
            foreach (string browser in browsers)
                foreach (string folder in programs)
                    if (folder.Length > 0 && File.Exists(Path.Combine(folder, browser)))
                        return Path.Combine(folder, browser);
            return null;
        }
        if (OperatingSystem.IsMacOS())
        {
            foreach (string app in new[] { "Google Chrome", "Microsoft Edge", "Chromium", "Brave Browser" })
            {
                string path = $"/Applications/{app}.app/Contents/MacOS/{app}";
                if (File.Exists(path))
                    return path;
            }
            return null;
        }
        foreach (string name in new[] { "google-chrome", "google-chrome-stable", "chromium", "chromium-browser", "microsoft-edge", "brave-browser" })
            foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
                if (folder.Length > 0 && File.Exists(Path.Combine(folder, name)))
                    return Path.Combine(folder, name);
        return null;
    }
}

/// <summary>
/// The running server, recorded in a file beside its settings (its port, token and process), so launching SpiceForge
/// again opens another window onto it instead of failing on the port. Removed when the server stops.
/// </summary>
public sealed record ServerInstance(int Port, string Token, int ProcessId)
{
    public static string PathFor(ServerOptions options) =>
        Path.Combine(Path.GetDirectoryName(options.SettingsPath) ?? ".", "server-instance.json");

    [System.Text.Json.Serialization.JsonIgnore]
    public string Page => $"http://127.0.0.1:{Port}/?token={Token}";

    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(this));
        }
        catch (IOException) { }
    }

    public static void Remove(string path, int processId)
    {
        try
        {
            if (Read(path)?.ProcessId == processId)
                File.Delete(path);
        }
        catch (IOException) { }
    }

    public static ServerInstance? Read(string path)
    {
        try
        {
            return File.Exists(path) ? System.Text.Json.JsonSerializer.Deserialize<ServerInstance>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>Whether this instance is still running and answering: its process alive, and /health saying so.</summary>
    public async Task<bool> IsAlive()
    {
        try
        {
            using var process = Process.GetProcessById(ProcessId);
            if (process.HasExited)
                return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            return (await http.GetStringAsync($"http://127.0.0.1:{Port}/health")).StartsWith("SpiceForgeServer", StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
