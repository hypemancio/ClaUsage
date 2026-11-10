using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace ClaUsage.Core;

/// <summary>
/// The install made by ClaUsage-Setup.exe (installer/ClaUsage.Setup): its entry in Windows' installed apps,
/// its shortcuts, and the uninstall that entry runs (<c>ClaUsage.exe --uninstall</c>). Names match the setup.
/// </summary>
public static class InstallInfo
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ClaUsage";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static string Exe => Environment.ProcessPath ?? "";
    private static string Folder => Path.GetDirectoryName(Exe) ?? "";

    private static string[] Shortcuts =>
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "ClaUsage.lnk"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "ClaUsage.lnk"),
    ];

    /// <summary>True when this exe is the one the setup registered.</summary>
    public static bool IsInstalled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
                return key?.GetValue("InstallLocation") is string dir && SamePath(dir, Folder);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>After a self-update, Windows' installed apps shows the new version.</summary>
    public static void RefreshVersion(string version)
    {
        try
        {
            if (!IsInstalled) return;
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKey, writable: true);
            if (key is null || key.GetValue("DisplayVersion") as string == version) return;
            key.SetValue("DisplayVersion", version);
            key.SetValue("EstimatedSize", (int)(new FileInfo(Exe).Length / 1024), RegistryValueKind.DWord);
        }
        catch (Exception e)
        {
            Log.Write($"install info: {e.Message}");
        }
    }

    /// <summary>
    /// Removes the install: other running copies, shortcuts, the start-with-Windows value, the installed-apps entry
    /// and, when asked, the saved accounts and settings. The exe and its folder go once this process has exited.
    /// </summary>
    public static void Uninstall(bool deleteData)
    {
        foreach (var process in Process.GetProcessesByName("ClaUsage"))
        {
            try
            {
                if (process.Id != Environment.ProcessId && SamePath(process.MainModule?.FileName ?? "", Exe))
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

        foreach (var link in Shortcuts)
            try { if (File.Exists(link)) File.Delete(link); } catch { /* left behind, harmless */ }

        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (run?.GetValue("ClaUsage") is string command && command.Contains(Exe, StringComparison.OrdinalIgnoreCase))
                run.DeleteValue("ClaUsage", throwOnMissingValue: false);
        }
        catch
        {
            // No Run value.
        }

        var registered = IsInstalled;
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false); } catch { /* already gone */ }

        if (deleteData)
        {
            var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaUsage");
            try { if (Directory.Exists(data)) Directory.Delete(data, recursive: true); } catch { /* in use: left behind */ }
        }

        // A running exe can't delete itself: a hidden shell removes the two exe files once this process is gone,
        // then the folder only if it's the registered one and now empty (rmdir without /s).
        var old = Path.Combine(Folder, "ClaUsage.old.exe");
        var script = $"ping -n 3 127.0.0.1 >nul & del /f /q \"{Exe}\" \"{old}\"" + (registered ? $" & rmdir \"{Folder}\"" : "");
        Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c \"{script}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetTempPath(),
        });
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}
