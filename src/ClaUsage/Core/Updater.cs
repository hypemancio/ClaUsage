using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace ClaUsage.Core;

/// <summary>
/// Self-update from the GitHub Releases of <see cref="Repo"/>. A release carries the exe plus a
/// <c>.sha256</c> file per flavor; the download is checked against it before it replaces the running exe.
/// Windows lets a running exe be renamed but not overwritten, so the old one is moved aside, the new one
/// put in its place and started, and the old file is deleted on the next launch.
/// </summary>
public sealed class Updater
{
    public const string Repo = "hypemancio/ClaUsage";
    public static readonly Uri ReleasesPage = new($"https://github.com/{Repo}/releases");

#if SELF_CONTAINED
    private const string AssetName = "ClaUsage-standalone.exe";
#else
    private const string AssetName = "ClaUsage.exe";
#endif

    public sealed record Release(Version Version, string Tag, string Notes, string ExeUrl, string ShaUrl, Uri Page);

    public Release? Available { get; private set; }
    public DateTimeOffset? LastCheck { get; private set; }
    public string? LastError { get; private set; }

    public static Version Current =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 0, 0);

    /// <summary>Updating only makes sense for the published single-file exe, not a dev build.</summary>
    public static bool CanSelfUpdate
    {
        get
        {
            var path = Environment.ProcessPath ?? "";
            return path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                   && !path.Contains(Path.Combine("bin", "Debug"), StringComparison.OrdinalIgnoreCase)
                   && !path.Contains(Path.Combine("bin", "Release"), StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Returns the newer release, or null when up to date, the repo is private, or nothing is published.</summary>
    public async Task<Release?> CheckAsync(CancellationToken ct)
    {
        LastCheck = DateTimeOffset.UtcNow;
        LastError = null;
        try
        {
            var response = await Http.Get($"https://api.github.com/repos/{Repo}/releases/latest", new Dictionary<string, string>
            {
                ["Accept"] = "application/vnd.github+json",
                ["User-Agent"] = $"ClaUsage/{Current}",
                ["X-GitHub-Api-Version"] = "2022-11-28",
            }, ct);
            // 404: no release yet, or the repository is private.
            if (response.Code == 404) { Available = null; return null; }
            if (!response.Ok) throw new ProviderException($"GitHub answered HTTP {response.Code}.");
            var json = response.Json() ?? throw new ProviderException("GitHub returned an invalid release.");
            if (JsonRead.Bool(json["draft"]) == true || JsonRead.Bool(json["prerelease"]) == true) { Available = null; return null; }

            var tag = JsonRead.Str(json["tag_name"]) ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) throw new ProviderException($"Release tag {tag} isn't a version.");
            version = new Version(version.Major, version.Minor, Math.Max(0, version.Build));
            if (version <= Current) { Available = null; return null; }

            string? exe = null, sha = null;
            foreach (var asset in json["assets"] as JsonArray ?? new JsonArray())
            {
                var name = JsonRead.Str(asset?["name"]);
                var url = JsonRead.Str(asset?["browser_download_url"]);
                if (name == AssetName) exe = url;
                if (name == AssetName + ".sha256") sha = url;
            }
            if (exe is null || sha is null) throw new ProviderException($"Release {tag} has no {AssetName} with checksum.");

            Available = new Release(version, tag, JsonRead.Str(json["body"]) ?? "", exe, sha,
                new Uri(JsonRead.Str(json["html_url"]) ?? ReleasesPage.AbsoluteUri));
            return Available;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LastError = e.Message;
            Log.Write($"update check failed: {e.Message}");
            return null;
        }
    }

    /// <summary>Downloads, verifies and swaps in <paramref name="release"/>, then starts it. The caller quits.</summary>
    public static async Task InstallAsync(Release release, IProgress<double>? progress, CancellationToken ct)
    {
        var current = Environment.ProcessPath ?? throw new ProviderException("Can't find the running exe.");
        var dir = Path.GetDirectoryName(current)!;
        var staged = Path.Combine(dir, "ClaUsage.new.exe");
        var old = Path.Combine(dir, "ClaUsage.old.exe");

        var shaText = await Http.Client.GetStringAsync(release.ShaUrl, ct);
        var expected = shaText.Trim().Split(' ', '\t', '\r', '\n')[0].ToLowerInvariant();
        if (expected.Length != 64) throw new ProviderException("The release checksum is malformed.");

        using (var response = await Http.Client.GetAsync(release.ExeUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? 0;
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using var output = File.Create(staged);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total > 0) progress?.Report((double)done / total);
            }
        }

        string actual;
        await using (var file = File.OpenRead(staged))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(file, ct)).ToLowerInvariant();
        if (actual != expected)
        {
            File.Delete(staged);
            throw new ProviderException("The download doesn't match the release checksum. Nothing was changed.");
        }

        if (File.Exists(old)) File.Delete(old);
        File.Move(current, old);
        try
        {
            File.Move(staged, current);
        }
        catch
        {
            File.Move(old, current); // put the running version back
            throw;
        }
        Process.Start(new ProcessStartInfo(current, "--updated") { UseShellExecute = false });
    }

    /// <summary>Removes the previous exe left by an update; it may stay locked for a moment after the swap.</summary>
    public static async Task CleanupAsync()
    {
        var dir = Path.GetDirectoryName(Environment.ProcessPath ?? "");
        if (string.IsNullOrEmpty(dir)) return;
        var old = Path.Combine(dir, "ClaUsage.old.exe");
        for (var i = 0; i < 10 && File.Exists(old); i++)
        {
            try { File.Delete(old); }
            catch { await Task.Delay(1000); }
        }
    }
}
