using System.Reflection;
using System.Text.Json.Nodes;

namespace SpiceForgeServer;

/// <summary>
/// What the editor's About dialog shows, and the server prints when it starts: SpiceForge's version and commit, its
/// copyright and license, where its source is, and the third-party components the server ships, each with its license
/// text in <c>licenses/</c> beside the server (copied there by the build; see Assets/Licenses).
/// </summary>
public static class About
{
    /// <summary>A shipped component: its license text is <see cref="File"/> in <c>licenses/</c>.</summary>
    public sealed record Component(string Name, string By, string License, string Url, string File, string? Notices = null);

    public const string Name = "SpiceForge";
    public const string Repository = "https://github.com/mikeoliphant/SpiceForge";
    public const string License = "MIT License";
    /// <summary>SpiceForge's own license text, in <c>licenses/</c>.</summary>
    public const string LicenseFile = "SpiceForge.txt";

    /// <summary>What the server ships, beyond SpiceForge itself. The plugin's dependencies aren't here: it's packaged
    /// apart.</summary>
    public static readonly Component[] Components =
    {
        new("LiveSPICE", "Dillon Sharlet", "MIT License", "https://github.com/dsharlet/LiveSPICE", "LiveSPICE.txt"),
        new("ComputerAlgebra", "Dillon Sharlet", "MIT License", "https://github.com/dsharlet/ComputerAlgebra", "ComputerAlgebra.txt"),
        new("AudioIOSharp", "Mike Oliphant", "MIT License", "https://github.com/mikeoliphant/AudioIOSharp", "AudioIOSharp.txt"),
        new("JackSharpCore", "Thomas Mayer, Mike Oliphant", "MIT License", "https://github.com/mikeoliphant/JackSharpCore", "JackSharpCore.txt"),
        new("NAudio", "Mark Heath", "MIT License", "https://github.com/naudio/NAudio", "NAudio.txt"),
        // A package is self-contained, so it includes the runtime. Their third-party notices are long, and linked.
        new(".NET", ".NET Foundation and Contributors", "MIT License", "https://github.com/dotnet/runtime", "dotnet.txt",
            "https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT"),
        new("ASP.NET Core", ".NET Foundation and Contributors", "MIT License", "https://github.com/dotnet/aspnetcore", "dotnet.txt",
            "https://github.com/dotnet/aspnetcore/blob/main/THIRD-PARTY-NOTICES.txt"),
    };

    private static readonly Assembly Assembly = typeof(About).Assembly;
    private static readonly string Informational =
        Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    /// <summary>The version, without the commit: "0.0.1" for a release, or a development build's, as the server's project
    /// derives it from git tags: "0.0.2-dev.9" (9 commits after v0.0.1), "0.2.0-beta.1.dev.3", or "0.0.0-dev".</summary>
    public static string Version => Informational.Split('+')[0];

    /// <summary>The git commit built, in full; empty if the build didn't know it.</summary>
    public static string Commit => Informational.Contains('+') ? Informational[(Informational.IndexOf('+') + 1)..] : "";

    public static string ShortCommit => Commit.Length > 7 ? Commit[..7] : Commit;

    public static bool Development => System.Text.RegularExpressions.Regex.IsMatch(Version, @"[-.]dev(\.\d+)?$");

    public static string Copyright => Assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? "";

    /// <summary>Where the license texts are, beside the server.</summary>
    public static string LicenseFolder => Path.Combine(AppContext.BaseDirectory, "licenses");

    /// <summary>For hello's <c>about</c>.</summary>
    public static JsonObject ToJson() => new()
    {
        ["name"] = Name,
        ["version"] = Version,
        ["commit"] = Commit,
        ["development"] = Development,
        ["copyright"] = Copyright,
        ["license"] = License,
        ["licenseFile"] = LicenseFile,
        ["repository"] = Repository,
        ["components"] = new JsonArray(Components.Select(c =>
        {
            var o = new JsonObject { ["name"] = c.Name, ["by"] = c.By, ["license"] = c.License, ["url"] = c.Url, ["file"] = c.File };
            if (c.Notices != null)
                o["notices"] = c.Notices;
            return (JsonNode)o;
        }).ToArray()),
    };

    /// <summary>The startup summary: a few lines of what the About dialog shows.</summary>
    public static string Summary()
    {
        string commit = ShortCommit != "" ? $", commit {ShortCommit}" : "";
        return $"{Name} {Version}{commit}\n" +
            $"{Copyright}. {License}. Source: {Repository}\n" +
            $"Built on LiveSPICE by Dillon Sharlet. Third-party licenses: {LicenseFolder}";
    }
}
