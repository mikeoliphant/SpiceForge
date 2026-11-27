using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;

// Builds SpiceForge's packages into artifacts/: the server, self-contained, with the web editor and LiveSPICE's
// component libraries and examples, and the plugin as CLAP and VST3 (in clap/ and vst3/).
//
//   dotnet run -c Release --project SpiceForge.Package [-- --rid win-x64,linux-x64] [--skipWeb]
//       [--requireInstaller]   fail, rather than skip the installer, without Inno Setup (for releases)
//       [--requirePlugins]     fail, rather than leave the plugin out, without AudioPlugSharp's native adapters for a
//                              platform (for releases)
//
// The plugin's bundles need AudioPlugSharp's native adapters for each platform, in
// SpiceForgePlugin/Dependencies/AudioPlugSharp/AudioPlugSharpBridge/bin/native/<rid>/. This machine's own are built here
// (with CMake) when they're missing; another platform's are built on that platform (CI's Linux adapters job) and put
// there first.
//
// The version comes from git tags, as the server's project derives it (its SpiceForgeGitVersion target): on a commit
// tagged v0.2.0 it's 0.2.0; on a later one, a development version such as 0.2.1-dev.4. There's no option to set it, so
// a package can't claim a version its commit wasn't tagged with.
//
// - win-x64: SpiceForge-<version>-win-x64.zip (portable), and SpiceForge-<version>-setup.exe when Inno Setup's
//   compiler (ISCC) is installed. The server is built without a console window, and logs to server.log in its
//   settings folder.
// - linux-x64: SpiceForge-<version>-linux-x64.tar.gz, with the executables' permissions set (tar tools on Windows
//   don't reliably record them).
// Both start the editor in a window of its own, and stop when it closes (appsettings.json: "open": "app").

string repo = FindRepository();
if (args.Contains("--version"))
    Fail("There's no --version: the version comes from git tags. Tag the commit v<version> to release it (the Release workflow does).");
string serverProject = Path.Combine(repo, "SpiceForgeServer", "SpiceForgeServer.csproj");
string version = Output("dotnet", $"msbuild \"{serverProject}\" -nologo -target:SpiceForgeGitVersion -getProperty:Version", repo).Trim();
if (!System.Text.RegularExpressions.Regex.IsMatch(version, @"^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$"))
    Fail($"Couldn't work out the version from git (got '{version}').");
Step($"Version {version}");
string[] rids = (Option("--rid") ?? "win-x64,linux-x64").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
bool skipWeb = args.Contains("--skipWeb");
bool requireInstaller = args.Contains("--requireInstaller");
bool requirePlugins = args.Contains("--requirePlugins");
string pluginProject = Path.Combine(repo, "SpiceForgePlugin", "SpiceForgePlugin", "SpiceForgePlugin.csproj");
string pluginOutput = Path.Combine(repo, "SpiceForgePlugin", "SpiceForgePlugin", "bin", "Release", "net10.0");
string bridge = Path.Combine(repo, "SpiceForgePlugin", "Dependencies", "AudioPlugSharp", "AudioPlugSharpBridge");
string artifacts = Path.Combine(repo, "artifacts");
Directory.CreateDirectory(artifacts);

string web = Path.Combine(repo, "SpiceForgeWebUI");
if (!skipWeb)
{
    Step("Building the web editor");
    if (!Directory.Exists(Path.Combine(web, "node_modules")))
        Run("npm", "ci", web);
    Run("npm", "run build", web);
}
string dist = Path.Combine(web, "dist");
if (!File.Exists(Path.Combine(dist, "index.html")))
    Fail("The web editor isn't built (SpiceForgeWebUI/dist).");

foreach (string rid in rids)
{
    bool windows = rid.StartsWith("win", StringComparison.Ordinal);
    string stage = Path.Combine(artifacts, "stage", rid, "SpiceForge");
    if (Directory.Exists(stage))
        Directory.Delete(stage, recursive: true);

    Step($"Publishing the server for {rid}");
    // Self-contained, so nothing needs installing first. Not ReadyToRun: with tiered compilation off, precompiled code
    // would be used as it is, less optimized than the JIT's, on the audio path.
    // A single file: the runtime and every assembly in the one executable, rather than 350 files beside it, so the
    // folder shows what a user needs (the server, its settings, the README, the examples). The symbols are embedded,
    // so stack traces keep their line numbers; nothing reads an assembly's file path. No reference documentation, and
    // no static web assets manifest (the server serves the editor from wwwroot itself; the manifest was empty).
    // The version is passed on, so the publish uses exactly the one the packages are named for.
    Run("dotnet", $"publish \"{serverProject}\" -c Release -r {rid} --self-contained true " +
        "-p:PublishSingleFile=true -p:DebugType=embedded -p:PublishReferencesDocumentationFiles=false -p:StaticWebAssetsEnabled=false " +
        "-p:IsTransformWebConfigDisabled=true " +
        $"-p:SpiceForgeVersion={version} {(windows ? "-p:SpiceForgeNoConsole=true " : "")}-o \"{stage}\"", repo);
    // ASP.NET Core's module for hosting in IIS, which SpiceForge never is: a native file the single file leaves beside
    // it on Windows. (SpiceForge.Validate server, run against the package's executable, checks nothing needs it.)
    File.Delete(Path.Combine(stage, "aspnetcorev2_inprocess.dll"));

    Step("Adding the web editor, examples and documents");
    CopyFolder(dist, Path.Combine(stage, "wwwroot"), file => !file.EndsWith(".map", StringComparison.Ordinal));
    CopyFolder(Path.Combine(repo, "Dependencies", "LiveSPICE", "Tests", "Examples"), Path.Combine(stage, "Examples"),
        file => file.EndsWith(".schx", StringComparison.OrdinalIgnoreCase));
    File.WriteAllText(Path.Combine(stage, "appsettings.json"),
        "{\n  \"open\": \"app\",\n" + (windows ? "  \"logFile\": \"server.log\"\n" : "  \"logFile\": \"\"\n") + "}\n");
    File.Copy(Path.Combine(repo, "LICENSE"), Path.Combine(stage, "LICENSE.txt"));
    File.Copy(Path.Combine(repo, "CREDITS.md"), Path.Combine(stage, "CREDITS.md"));
    File.WriteAllText(Path.Combine(stage, "README.txt"), Readme(version, windows));
    if (!windows)
        // For a desktop entry (the Windows executable carries the icon itself).
        File.Copy(Path.Combine(repo, "Assets", "Icon", "SpiceForge-256.png"), Path.Combine(stage, "spiceforge.png"));
    if (!File.Exists(Path.Combine(stage, "Components", "Diodes.xml")) && Directory.GetFiles(Path.Combine(stage, "Components"), "*.xml").Length == 0)
        Fail("LiveSPICE's component libraries weren't published.");

    Step($"Adding the plugin (CLAP and VST3) for {rid}");
    if (AddPlugin(rid, stage) is string missing)
    {
        if (requirePlugins)
            Fail(missing);

        Console.WriteLine($"  {missing} The package has no plugin.");
    }

    if (windows)
    {
        string zip = Path.Combine(artifacts, $"SpiceForge-{version}-{rid}.zip");
        Step($"Zipping {Path.GetFileName(zip)}");
        File.Delete(zip);
        ZipFile.CreateFromDirectory(stage, zip, CompressionLevel.Optimal, includeBaseDirectory: true);

        if (FindIscc() is string iscc)
        {
            Step("Building the installer");
            Run(iscc, $"/Q /DAppVersion={version} \"/DSourceDir={stage}\" \"/DOutputDir={artifacts}\" \"{Path.Combine(repo, "SpiceForge.Package", "SpiceForge.iss")}\"", repo);
        }
        else if (requireInstaller)
            Fail("Inno Setup's compiler (ISCC) isn't installed, and the installer is required.");
        else
            Console.WriteLine("  Inno Setup's compiler (ISCC) isn't installed, so there's no installer; the zip is complete. " +
                "Install it from https://jrsoftware.org/isinfo.php (or: winget install JRSoftware.InnoSetup).");
    }
    else
    {
        string tarball = Path.Combine(artifacts, $"SpiceForge-{version}-{rid}.tar.gz");
        Step($"Writing {Path.GetFileName(tarball)}");
        WriteTarball(stage, tarball);
    }
}
Step($"Done: {artifacts}");
foreach (string file in Directory.GetFiles(artifacts))
    Console.WriteLine($"  {Path.GetFileName(file)}  {new FileInfo(file).Length / (1024.0 * 1024):F1} MB");
return 0;

// The plugin's bundles for the platform, in the stage's clap/ and vst3/ folders; or why they couldn't be made.
string? AddPlugin(string rid, string stage)
{
    string native = Path.Combine(bridge, "bin", "native", rid);
    bool HaveAdapters() => File.Exists(Path.Combine(native, "AudioPlugSharpClap.clap"));

    if (!HaveAdapters() && (rid == HostRid()))
    {
        // The plugin's build renders AudioPlugSharp's image sheets, which the native build's managed part needs.
        Run("dotnet", $"build \"{pluginProject}\" -c Release", repo);

        Step("Building AudioPlugSharp's native adapters");
        string build = Path.Combine(bridge, "bin", "native-build");
        Run("cmake", $"-S \"{Path.Combine(bridge, "Native")}\" -B \"{build}\" -DCMAKE_BUILD_TYPE=Release", repo);
        Run("cmake", $"--build \"{build}\" --config Release --target stage_adapters", repo);
    }

    if (!HaveAdapters())
        return $"AudioPlugSharp's native adapters for {rid} aren't in {native}: they're built on {rid} (CI's Linux adapters job builds linux-x64's).";

    // Made afresh for this platform, so another platform's bundles from an earlier build can't be packaged instead.
    foreach (string bundles in new[] { "clap", "vst3" })
    {
        if (Directory.Exists(Path.Combine(pluginOutput, bundles)))
            Directory.Delete(Path.Combine(pluginOutput, bundles), recursive: true);
    }

    Run("dotnet", $"build \"{pluginProject}\" -c Release -p:AudioPlugSharpBridgeRuntimeIdentifier={rid} -p:SpiceForgeVersion={version}", repo);

    string clap = Path.Combine(pluginOutput, "clap", "SpiceForgePlugin");
    string vst3 = Path.Combine(pluginOutput, "vst3", "SpiceForgePlugin.vst3");

    if (!File.Exists(Path.Combine(clap, "SpiceForgePlugin.clap")) || !Directory.Exists(vst3))
        Fail($"The plugin's bundles for {rid} weren't made.");

    // The plugin's output has its packages' native libraries for every platform (MonoGame's and the file dialog's,
    // for Android, iOS, macOS...): only this platform's go in (runtimes/<rid>, and runtimes/<os> if there is one).
    string os = rid.Split('-')[0];
    bool ThisPlatform(string file, string bundle)
    {
        string[] parts = Path.GetRelativePath(bundle, file).Split(Path.DirectorySeparatorChar);
        int runtimes = Array.IndexOf(parts, "runtimes");

        return (runtimes < 0) || (runtimes + 1 >= parts.Length) || (parts[runtimes + 1] == rid) || (parts[runtimes + 1] == os);
    }

    CopyFolder(clap, Path.Combine(stage, "clap", "SpiceForgePlugin"), file => ThisPlatform(file, clap));
    CopyFolder(vst3, Path.Combine(stage, "vst3", "SpiceForgePlugin.vst3"), file => ThisPlatform(file, vst3));

    return null;
}

static string HostRid()
{
    string os = OperatingSystem.IsWindows() ? "win" : (OperatingSystem.IsLinux() ? "linux" : "osx");

    return $"{os}-{RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()}";
}

string? Option(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static string FindRepository()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "SpiceForge.slnx")))
            return dir.FullName;
    throw new InvalidOperationException("Run it from within the SpiceForge repository.");
}

static void Step(string what) => Console.WriteLine($"== {what}");

static void Fail(string why)
{
    Console.Error.WriteLine(why);
    Environment.Exit(1);
}

static void Run(string program, string arguments, string folder)
{
    // npm is a script on Windows: run it through the shell there.
    var start = OperatingSystem.IsWindows() && program == "npm"
        ? new ProcessStartInfo("cmd.exe", $"/c npm {arguments}")
        : new ProcessStartInfo(program, arguments);
    start.WorkingDirectory = folder;
    start.UseShellExecute = false;
    using Process process = Process.Start(start) ?? throw new InvalidOperationException($"Couldn't start {program}.");
    process.WaitForExit();
    if (process.ExitCode != 0)
        Fail($"{program} {arguments} failed ({process.ExitCode}).");
}

static string Output(string program, string arguments, string folder)
{
    var start = new ProcessStartInfo(program, arguments) { WorkingDirectory = folder, UseShellExecute = false, RedirectStandardOutput = true };
    using Process process = Process.Start(start) ?? throw new InvalidOperationException($"Couldn't start {program}.");
    string output = process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    if (process.ExitCode != 0)
        Fail($"{program} {arguments} failed ({process.ExitCode}).");
    return output;
}

static void CopyFolder(string from, string to, Func<string, bool> include)
{
    foreach (string file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
    {
        if (!include(file))
            continue;
        string target = Path.Combine(to, Path.GetRelativePath(from, file));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(file, target, overwrite: true);
    }
}

static string? FindIscc()
{
    if (!OperatingSystem.IsWindows())
        return null;
    foreach (string folder in new[]
    {
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
    })
    {
        string iscc = Path.Combine(folder, "Inno Setup 6", "ISCC.exe");
        if (File.Exists(iscc))
            return iscc;
    }
    return null;
}

// A gzipped tar of the folder, under SpiceForge/, with Unix permissions: executables (the server, the runtime's
// createdump, and the plugin's adapters, which are shared libraries) 755, everything else 644.
static void WriteTarball(string stage, string tarball)
{
    string root = Path.GetFileName(stage);
    using FileStream file = File.Create(tarball);
    using var gzip = new GZipStream(file, CompressionLevel.Optimal);
    using var tar = new TarWriter(gzip, TarEntryFormat.Pax);
    var executable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
    var plain = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
    foreach (string dir in Directory.GetDirectories(stage, "*", SearchOption.AllDirectories).Prepend(stage))
    {
        string name = Path.Combine(root, Path.GetRelativePath(stage, dir)).Replace('\\', '/').TrimEnd('.', '/') + "/";
        tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, name) { Mode = executable });
    }
    foreach (string path in Directory.GetFiles(stage, "*", SearchOption.AllDirectories))
    {
        string relative = Path.GetRelativePath(stage, path).Replace('\\', '/');
        bool isExecutable = relative is "SpiceForgeServer" or "createdump" || relative.EndsWith(".clap", StringComparison.Ordinal)
            || relative.EndsWith(".so", StringComparison.Ordinal);
        using FileStream data = File.OpenRead(path);
        tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, $"{root}/{relative}") { Mode = isExecutable ? executable : plain, DataStream = data });
    }
}

static string Readme(string version, bool windows) => $"""
    SpiceForge {version}

    SpiceForge simulates LiveSPICE schematics in real time. This package is its editor: a server that runs on this
    computer, and the editor in a window of its own.

    Starting it
    {(windows
        ? "  Run SpiceForgeServer.exe. The editor opens in a window of its own (Microsoft Edge's app mode, or else the default\n    browser), and SpiceForge stops a few seconds after the window closes. Starting it again while it runs opens\n    another window."
        : "  Run ./SpiceForgeServer. The editor opens in a window of its own (Chrome's or Chromium's app mode, or else the\n    default browser), and SpiceForge stops a few seconds after the window closes. Starting it again while it runs\n    opens another window.")}

    Audio
      Audio... in the editor's toolbar chooses where simulations play: {(windows ? "an ASIO device, the JACK server (if JACK is\n    installed)," : "the JACK server (JACK2, or PipeWire's JACK support),")} or None (no audio: simulations drive the meters and scope).

    Plugin
      SpiceForge is also a plugin for DAWs (CLAP and VST3), running a schematic on a track. {(windows
        ? "The installer installs it (in\n    Common Files\\CLAP and Common Files\\VST3, for all users or just you). From the zip, copy the SpiceForgePlugin folder\n    in clap to C:\\Program Files\\Common Files\\CLAP, and SpiceForgePlugin.vst3 in vst3 to C:\\Program Files\\Common Files\\VST3."
        : "Copy the SpiceForgePlugin folder in clap to\n    ~/.clap, and SpiceForgePlugin.vst3 in vst3 to ~/.vst3.")}
      The plugin needs the .NET 10 runtime installed {(windows
        ? "(the .NET Desktop Runtime, from https://dotnet.microsoft.com/download)."
        : "(on Ubuntu: sudo apt install dotnet-runtime-10.0).")}

    Settings and files
      Settings are kept in {(windows ? "%APPDATA%\\SpiceForge" : "~/.config/SpiceForge")}. Schematics and component libraries are in Documents/LiveSPICE,
      as LiveSPICE keeps them; the example schematics are in the Examples folder beside this file.
    {(windows ? "  The server's log is server.log in the settings folder.\n" : "")}
    Options
      SpiceForgeServer --help isn't available; the options are listed in DesignDocs/WebSocketApi.md (Server notes) in
      SpiceForge's repository, and can also be set in appsettings.json beside the server.

    License
      MIT: see LICENSE.txt. The licenses folder holds the license texts of SpiceForge and of what it includes (the
      editor's About dialog shows them too); CREDITS.md lists the projects SpiceForge is built on.
    """;
