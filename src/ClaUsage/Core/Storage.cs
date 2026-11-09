using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace ClaUsage.Core;

public static class AppPaths
{
    public static string DataDir
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaUsage");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string Accounts => Path.Combine(DataDir, "accounts.dat");
    public static string Settings => Path.Combine(DataDir, "settings.json");
    public static string Snapshots => Path.Combine(DataDir, "snapshots.json");
    public static string Log => Path.Combine(DataDir, "claUsage.log");
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}

public static class Log
{
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                var path = AppPaths.Log;
                if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024) File.Delete(path);
                File.AppendAllText(path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never break the app.
        }
    }
}

/// <summary>
/// Accounts live in one file encrypted with DPAPI for the current Windows user, so tokens are unreadable
/// to other users and to copies of the file taken to another machine.
/// </summary>
public static class AccountStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ClaUsage.accounts.v1");

    public static List<Account> Load()
    {
        try
        {
            if (!File.Exists(AppPaths.Accounts)) return new();
            var bytes = ProtectedData.Unprotect(File.ReadAllBytes(AppPaths.Accounts), Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<List<Account>>(bytes, Json.Options) ?? new();
        }
        catch (Exception e)
        {
            Log.Write($"accounts load failed: {e.Message}");
            return new();
        }
    }

    public static void Save(IEnumerable<Account> accounts)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(accounts.ToList(), Json.Options);
        var bytes = ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser);
        var tmp = AppPaths.Accounts + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, AppPaths.Accounts, overwrite: true);
    }
}

public enum TrayDisplay
{
    /// <summary>Highest percentage among every account's limits.</summary>
    HighestPercent,
    /// <summary>Plain app icon, no number.</summary>
    IconOnly,
}

public sealed class AppSettings
{
    public int RefreshMinutes { get; set; } = 5;
    public int WarnPercent { get; set; } = 70;
    public int CriticalPercent { get; set; } = 90;
    public bool Notifications { get; set; } = true;
    public bool ShowPace { get; set; } = true;
    public TrayDisplay TrayDisplay { get; set; } = TrayDisplay.HighestPercent;
    public bool HotkeyEnabled { get; set; } = true;
    public bool CheckForUpdates { get; set; } = true;
    /// <summary>The release already announced with a notification, so it's announced once.</summary>
    public string? AnnouncedUpdate { get; set; }
    /// <summary>Per account id, metric labels hidden from the panel.</summary>
    public Dictionary<string, List<string>> HiddenMetrics { get; set; } = new();
    /// <summary>Account ids in display order.</summary>
    public List<string> Order { get; set; } = new();

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.Settings))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.Settings), Json.Options) ?? new();
        }
        catch (Exception e)
        {
            Log.Write($"settings load failed: {e.Message}");
        }
        return new();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(AppPaths.Settings, JsonSerializer.Serialize(this, Json.Options));
        }
        catch (Exception e)
        {
            Log.Write($"settings save failed: {e.Message}");
        }
    }

    public bool IsHidden(string accountId, string label) =>
        HiddenMetrics.TryGetValue(accountId, out var list) && list.Contains(label);

    public void SetHidden(string accountId, string label, bool hidden)
    {
        if (!HiddenMetrics.TryGetValue(accountId, out var list)) HiddenMetrics[accountId] = list = new();
        list.Remove(label);
        if (hidden) list.Add(label);
        Save();
    }
}

/// <summary>Last usage read per account, kept on disk (no tokens) for the panel at launch and for --json.</summary>
public static class SnapshotCache
{
    public sealed class Entry
    {
        public string AccountId { get; set; } = "";
        public string Provider { get; set; } = "";
        public string Name { get; set; } = "";
        public UsageSnapshot Snapshot { get; set; } = new();
    }

    public static List<Entry> Load()
    {
        try
        {
            if (File.Exists(AppPaths.Snapshots))
                return JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(AppPaths.Snapshots), Json.Options) ?? new();
        }
        catch (Exception e)
        {
            Log.Write($"snapshot cache load failed: {e.Message}");
        }
        return new();
    }

    public static void Save(IEnumerable<Entry> entries)
    {
        try
        {
            File.WriteAllText(AppPaths.Snapshots, JsonSerializer.Serialize(entries.ToList(), Json.Options));
        }
        catch (Exception e)
        {
            Log.Write($"snapshot cache save failed: {e.Message}");
        }
    }
}

public static class LaunchAtLogin
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ClaUsage";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --background");
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}

public static class WindowsTheme
{
    public static bool IsDark
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
            }
            catch
            {
                return false;
            }
        }
    }

    public static bool TaskbarIsDark
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("SystemUsesLightTheme") is not int v || v == 0;
            }
            catch
            {
                return true;
            }
        }
    }
}
