using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace ClaUsage.Setup;

/// <summary>
/// Per-user install: the exe in a folder under %LOCALAPPDATA%\Programs, a Start menu shortcut, an entry in
/// Windows' installed apps (uninstalled by <c>ClaUsage.exe --uninstall</c>) and, optionally, a desktop
/// shortcut and the start-with-Windows Run value. The names here match the app's Core/InstallInfo.cs.
/// </summary>
public sealed class Installer
{
    public const string AppName = "ClaUsage";
    public const string ExeName = "ClaUsage.exe";
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ClaUsage";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Repo = "hypemancio/ClaUsage";

    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? Assembly.GetExecutingAssembly().GetName().Version.ToString(3);

    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);

    public string Folder { get; set; } = Existing?.Folder ?? DefaultFolder;
    public bool DesktopShortcut { get; set; } = File.Exists(DesktopLink);
    public bool StartWithWindows { get; set; } = Existing is null || RunValue() is not null;

    /// <summary>The install found in the registry, if its exe is still there.</summary>
    public static (string Folder, string? Version)? Existing
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
                if (key?.GetValue("InstallLocation") is string dir && File.Exists(Path.Combine(dir, ExeName)))
                    return (dir, key.GetValue("DisplayVersion") as string);
            }
            catch
            {
                // No readable install: a fresh one.
            }
            // An install made by hand before the setup existed.
            return File.Exists(Path.Combine(DefaultFolder, ExeName)) ? (DefaultFolder, null) : null;
        }
    }

    public static string StartMenuLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");
    public static string DesktopLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk");
    public string ExePath => Path.Combine(Folder, ExeName);

    /// <summary>True when the .NET 10 Desktop Runtime (x64) is installed, so the small exe can run.</summary>
    public static bool HasDesktopRuntime()
    {
        var roots = new[] { Environment.GetEnvironmentVariable("ProgramW6432"), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) };
        foreach (var root in roots.Where(r => !string.IsNullOrEmpty(r)).Distinct())
        {
            var shared = Path.Combine(root!, "dotnet", "shared", "Microsoft.WindowsDesktop.App");
            if (Directory.Exists(shared) && Directory.GetDirectories(shared, "10.*").Length > 0) return true;
        }
        return false;
    }

    /// <summary>Runs the install, reporting progress 0..1 and a short status line.</summary>
    public async Task RunAsync(IProgress<(double Fraction, string Status)> progress, CancellationToken ct)
    {
        progress.Report((0.05, "Closing ClaUsage…"));
        CloseRunning(Folder);

        Directory.CreateDirectory(Folder);
        var target = ExePath;
        var staged = target + ".new";
        if (HasDesktopRuntime())
        {
            progress.Report((0.3, "Copying ClaUsage…"));
            using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream(ExeName)
                ?? throw new InvalidOperationException("The setup has no ClaUsage.exe inside.");
            using var file = File.Create(staged);
            await payload.CopyToAsync(file, 81920, ct);
        }
        else
        {
            await DownloadStandalone(staged, progress, ct);
        }

        progress.Report((0.8, "Installing…"));
        File.Copy(staged, target, overwrite: true);
        File.Delete(staged);
        var old = Path.Combine(Folder, "ClaUsage.old.exe");
        if (File.Exists(old)) try { File.Delete(old); } catch { /* left for the app's cleanup */ }

        progress.Report((0.9, "Creating shortcuts…"));
        CreateShortcut(StartMenuLink, target);
        if (DesktopShortcut) CreateShortcut(DesktopLink, target);
        else if (File.Exists(DesktopLink)) File.Delete(DesktopLink);

        using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (StartWithWindows) run.SetValue(AppName, $"\"{target}\" --background");
            else run.DeleteValue(AppName, throwOnMissingValue: false);
        }
        Register(target);
        progress.Report((1, "Done"));
    }

    /// <summary>The release's standalone build, checked against its published SHA-256.</summary>
    private static async Task DownloadStandalone(string path, IProgress<(double, string)> progress, CancellationToken ct)
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"ClaUsage-Setup/{Version}");
        var baseUrl = $"https://github.com/{Repo}/releases/download/v{Version}/ClaUsage-standalone.exe";

        progress.Report((0.1, "Downloading ClaUsage (the .NET 10 runtime isn't installed)…"));
        var expected = (await http.GetStringAsync(baseUrl + ".sha256")).Trim().Split(' ')[0].ToLowerInvariant();
        using (var response = await http.GetAsync(baseUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? 0;
            using var source = await response.Content.ReadAsStreamAsync();
            using var file = File.Create(path);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
            {
                await file.WriteAsync(buffer, 0, read, ct);
                done += read;
                if (total > 0)
                    progress.Report((0.1 + 0.65 * done / total, $"Downloading ClaUsage… {done / 1048576} of {total / 1048576} MB"));
            }
        }
        using var sha = SHA256.Create();
        using var check = File.OpenRead(path);
        var actual = BitConverter.ToString(sha.ComputeHash(check)).Replace("-", "").ToLowerInvariant();
        check.Dispose();
        if (actual != expected)
        {
            File.Delete(path);
            throw new InvalidOperationException("The download doesn't match its checksum. Try again.");
        }
    }

    /// <summary>Ends ClaUsage instances running from the install folder; the app saves its state as it goes.</summary>
    public static void CloseRunning(string folder)
    {
        var exe = Path.GetFullPath(Path.Combine(folder, ExeName));
        foreach (var process in Process.GetProcessesByName(AppName))
        {
            try
            {
                if (string.Equals(process.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase))
                {
                    process.Kill();
                    process.WaitForExit(5000);
                }
            }
            catch
            {
                // Not ours, or already gone.
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    public static bool IsRunning(string folder)
    {
        var exe = Path.GetFullPath(Path.Combine(folder, ExeName));
        return Process.GetProcessesByName(AppName).Any(p =>
        {
            try { return string.Equals(p.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
            finally { p.Dispose(); }
        });
    }

    private void Register(string exe)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", AppName);
        key.SetValue("DisplayVersion", Version);
        key.SetValue("Publisher", "MANCIO");
        key.SetValue("DisplayIcon", $"\"{exe}\",0");
        key.SetValue("InstallLocation", Folder);
        key.SetValue("UninstallString", $"\"{exe}\" --uninstall");
        key.SetValue("QuietUninstallString", $"\"{exe}\" --uninstall --silent");
        key.SetValue("URLInfoAbout", $"https://github.com/{Repo}");
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        key.SetValue("EstimatedSize", (int)(new FileInfo(exe).Length / 1024), RegistryValueKind.DWord);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    private static string? RunValue()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(AppName) as string;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>A .lnk through the Windows Script Host shell object, without a COM interop assembly.</summary>
    private static void CreateShortcut(string link, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows Script Host is unavailable.");
        var shell = Activator.CreateInstance(shellType);
        var shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { link });
        var type = shortcut.GetType();
        type.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { target });
        type.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { Path.GetDirectoryName(target)! });
        type.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { target + ",0" });
        type.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { "AI subscription limits in the tray" });
        type.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
    }

    public void Launch() => Process.Start(new ProcessStartInfo(ExePath) { UseShellExecute = true, WorkingDirectory = Folder });
}
