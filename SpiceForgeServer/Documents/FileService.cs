using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using SpiceForgeServer.Protocol;
using Util;

namespace SpiceForgeServer.Documents;

/// <summary>
/// The file operations of WebSocketApi.md, Files: listing folders for the client's dialogs, reading and writing
/// schematics, and watching the files clients have open.
/// </summary>
public sealed class FileService : IDisposable
{
    private readonly SettingsStore settings;
    private readonly LogHub log;
    private readonly Hub hub;
    private readonly ConcurrentDictionary<string, FileSystemWatcher> watchers = new(StringComparer.OrdinalIgnoreCase);
    // The last write time of each file we wrote ourselves, so watching doesn't report our own saves as outside changes.
    private readonly ConcurrentDictionary<string, DateTime> ownWrites = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> pending = new(StringComparer.OrdinalIgnoreCase);

    public FileService(SettingsStore settings, LogHub log, Hub hub)
    {
        this.settings = settings;
        this.log = log;
        this.hub = hub;
    }

    public static string FullPath(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path))
                throw new ApiException(ErrorCodes.InvalidValue, $"'{path}' isn't an absolute path.");
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ApiException(ErrorCodes.InvalidValue, $"'{path}' isn't a valid path: {ex.Message}");
        }
    }

    public static string Time(DateTime utc) => utc.ToUniversalTime().ToString("O");

    public JsonObject List(string path, IReadOnlyCollection<string>? extensions)
    {
        string full = FullPath(path);
        if (!Directory.Exists(full))
            throw new ApiException(ErrorCodes.NotFound, $"There's no folder '{full}'.", new JsonObject { ["path"] = full });
        try
        {
            var dir = new DirectoryInfo(full);
            var entries = new JsonArray();
            foreach (DirectoryInfo child in dir.EnumerateDirectories().Where(Visible).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
                entries.Add(new JsonObject { ["name"] = child.Name, ["kind"] = "dir", ["modified"] = Time(child.LastWriteTimeUtc) });
            foreach (FileInfo file in dir.EnumerateFiles().Where(Visible).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                if (extensions == null || extensions.Count == 0 || extensions.Any(e => file.Name.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
                    entries.Add(new JsonObject { ["name"] = file.Name, ["kind"] = "file", ["modified"] = Time(file.LastWriteTimeUtc) });
            return new JsonObject { ["path"] = dir.FullName, ["parent"] = dir.Parent?.FullName, ["entries"] = entries };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ApiException(ErrorCodes.IoError, ex.Message, new JsonObject { ["path"] = full });
        }
    }

    private static bool Visible(FileSystemInfo info) => (info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0;

    public JsonObject Read(string path)
    {
        string full = FullPath(path);
        XDocument xml;
        DateTime modified;
        try
        {
            xml = XDocument.Load(full);
            modified = File.GetLastWriteTimeUtc(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            settings.Forget(full);
            string message = ex is FileNotFoundException or DirectoryNotFoundException ? $"There's no file '{full}'." : ex.Message;
            throw new ApiException(ErrorCodes.IoError, message, new JsonObject { ["path"] = full });
        }
        if (xml.Root?.Name.LocalName != "Schematic")
            throw new ApiException(ErrorCodes.IoError, $"'{full}' isn't a schematic.", new JsonObject { ["path"] = full });

        CapturingLog capture = log.For("file");
        JsonObject document;
        try
        {
            document = SchxConverter.ToDocument(xml.Root, component =>
            {
                string? problem = SchxConverter.ResolveComponent(component);
                if (problem != null)
                    capture.WriteLine(MessageType.Warning, "{0}: a component couldn't be loaded: {1}", Path.GetFileName(full), problem);
                return problem;
            });
        }
        catch (FormatException ex)
        {
            throw new ApiException(ErrorCodes.IoError, $"'{full}' isn't a valid schematic: {ex.Message}", new JsonObject { ["path"] = full });
        }
        settings.Used(full);
        return new JsonObject
        {
            ["path"] = full,
            ["document"] = document,
            ["modified"] = Time(modified),
            ["messages"] = Json.Array(capture.Messages.Select(m => (JsonNode?)new JsonObject
            {
                ["level"] = LogHub.LevelName(m.Level),
                ["message"] = m.Message,
            })),
        };
    }

    public JsonObject Write(string path, JsonObject document, bool overwrite, string? expectModified)
    {
        string full = FullPath(path);
        byte[] bytes = SchxConverter.ToFileBytes(SchxConverter.ToSchematic(document));
        if (File.Exists(full))
        {
            if (!overwrite)
                throw new ApiException(ErrorCodes.Exists, $"'{full}' already exists.", new JsonObject { ["path"] = full });
            if (expectModified != null)
            {
                string actual = Time(File.GetLastWriteTimeUtc(full));
                if (!SameTime(actual, expectModified))
                    throw new ApiException(ErrorCodes.ModifiedOnDisk, $"'{full}' has changed on disk since it was read.",
                        new JsonObject { ["path"] = full, ["modified"] = actual });
            }
        }

        try
        {
            // Atomic, as LiveSPICE writes it: other programs watching the file never see it half written.
            string temp = full + ".temp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, full, overwrite: true);
            DateTime written = File.GetLastWriteTimeUtc(full);
            ownWrites[full] = written;
            settings.Used(full);
            log.Write(MessageType.Info, "file", $"Schematic saved to '{full}'");
            return new JsonObject { ["path"] = full, ["modified"] = Time(written) };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ApiException(ErrorCodes.IoError, ex.Message, new JsonObject { ["path"] = full });
        }
    }

    /// <summary>The files <see cref="Delete"/> will delete: schematics, as the client's dialogs list them.</summary>
    public static readonly string[] SchematicExtensions = { ".schx", ".xml" };

    /// <summary>Creates a folder in one that exists (not a chain of them, so a typo can't make several).</summary>
    public JsonObject CreateFolder(string path)
    {
        string full = FullPath(path);
        if (Directory.Exists(full) || File.Exists(full))
            throw new ApiException(ErrorCodes.Exists, $"'{full}' already exists.", new JsonObject { ["path"] = full });
        string? parent = Path.GetDirectoryName(full);
        if (parent == null || !Directory.Exists(parent))
            throw new ApiException(ErrorCodes.NotFound, $"There's no folder '{parent ?? full}'.", new JsonObject { ["path"] = full });
        try
        {
            Directory.CreateDirectory(full);
            log.Write(MessageType.Info, "file", $"Folder '{full}' created");
            return new JsonObject { ["path"] = full };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ApiException(ErrorCodes.IoError, ex.Message, new JsonObject { ["path"] = full });
        }
    }

    /// <summary>Deletes a schematic file, for good, and takes it out of the recent files. Nothing else is deleted:
    /// not other files, and not folders.</summary>
    public JsonObject Delete(string path)
    {
        string full = FullPath(path);
        if (!SchematicExtensions.Any(e => full.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
            throw new ApiException(ErrorCodes.InvalidValue, $"Only schematics ({string.Join(", ", SchematicExtensions)}) can be deleted.",
                new JsonObject { ["path"] = full });
        if (!File.Exists(full))
            throw new ApiException(ErrorCodes.NotFound, $"There's no file '{full}'.", new JsonObject { ["path"] = full });
        try
        {
            File.Delete(full);
            ownWrites.TryRemove(full, out _);
            settings.Forget(full);
            log.Write(MessageType.Info, "file", $"Schematic '{full}' deleted");
            return new JsonObject { ["path"] = full };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ApiException(ErrorCodes.IoError, ex.Message, new JsonObject { ["path"] = full });
        }
    }

    private static bool SameTime(string a, string b) =>
        DateTime.TryParse(a, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime x)
        && DateTime.TryParse(b, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime y)
        && x.ToUniversalTime() == y.ToUniversalTime();

    /// <summary>Sets the files a session watches, and keeps one watcher per folder anyone is watching in.</summary>
    public void Watch(Session session, IEnumerable<string> paths)
    {
        session.WatchedPaths = paths.Select(FullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        UpdateWatchers();
    }

    public void Forget(Session session)
    {
        session.WatchedPaths = Array.Empty<string>();
        UpdateWatchers();
    }

    private void UpdateWatchers()
    {
        var folders = hub.Sessions.SelectMany(s => s.WatchedPaths).Select(p => Path.GetDirectoryName(p)!)
            .Where(Directory.Exists).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string folder in watchers.Keys.Where(f => !folders.Contains(f)).ToList())
            if (watchers.TryRemove(folder, out FileSystemWatcher? old))
                old.Dispose();
        foreach (string folder in folders.Where(f => !watchers.ContainsKey(f)))
        {
            var watcher = new FileSystemWatcher(folder)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                IncludeSubdirectories = false,
            };
            watcher.Changed += (_, e) => Changed(e.FullPath);
            watcher.Created += (_, e) => Changed(e.FullPath);
            watcher.Renamed += (_, e) => Changed(e.FullPath);
            watcher.EnableRaisingEvents = true;
            watchers[folder] = watcher;
        }
    }

    // Watchers report a save as several events; wait for them to settle, then tell whoever watches the file.
    private void Changed(string path)
    {
        string full = Path.GetFullPath(path);
        if (!hub.Sessions.Any(s => s.WatchedPaths.Contains(full, StringComparer.OrdinalIgnoreCase)))
            return;
        var cancel = new CancellationTokenSource();
        if (pending.TryGetValue(full, out CancellationTokenSource? previous))
            previous.Cancel();
        pending[full] = cancel;
        _ = Task.Delay(200, cancel.Token).ContinueWith(t =>
        {
            if (t.IsCanceled || !File.Exists(full))
                return;
            DateTime modified = File.GetLastWriteTimeUtc(full);
            if (ownWrites.TryGetValue(full, out DateTime own) && own == modified)
                return;
            hub.Broadcast("file.changed", new JsonObject { ["path"] = full, ["modified"] = Time(modified) },
                s => s.WatchedPaths.Contains(full, StringComparer.OrdinalIgnoreCase));
        }, TaskScheduler.Default);
    }

    public void Dispose()
    {
        foreach (FileSystemWatcher watcher in watchers.Values)
            watcher.Dispose();
        watchers.Clear();
    }
}
