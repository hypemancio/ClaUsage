using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClaUsage.Core;
using ClaUsage.UI;
using WinForms = System.Windows.Forms;

namespace ClaUsage;

public partial class App : Application
{
    private const string MutexName = "ClaUsage.SingleInstance.v1";
    private const string ShowEventName = "ClaUsage.ShowPanel.v1";
    private const int HotkeyId = 0xC1A0;

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private WinForms.NotifyIcon? _tray;
    private PanelWindow? _panel;
    private SettingsWindow? _settings;
    private SignInWindow? _signIn;
    private HwndSource? _hotkeyWindow;
    private bool _hotkeyRegistered;

    public UsageService Service { get; private set; } = null!;
    public Updater Updater { get; } = new();
    private readonly System.Windows.Threading.DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromHours(24) };
    public bool Installing { get; private set; }

    public static ImageSource? WindowIcon { get; private set; }

    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int processId);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Contains("--json") || e.Args.Contains("--help") || e.Args.Contains("--selftest"))
        {
            RunCli(e.Args);
            // Application.Shutdown() would reset the exit code to 0; keep the self-test result.
            Shutdown(Environment.ExitCode);
            return;
        }

        var demo = e.Args.Contains("--demo");
        _mutex = new Mutex(true, demo ? MutexName + ".demo" : MutexName, out var first);
        if (!first && e.Args.Contains("--updated"))
        {
            // Started by the previous version, which quits right after: wait for it.
            try { first = _mutex.WaitOne(TimeSpan.FromSeconds(15)); }
            catch (AbandonedMutexException) { first = true; }
        }
        if (!first)
        {
            // Already running: ask that instance to open its panel.
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { /* first instance gone */ }
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Write($"unhandled: {args.Exception}");
            args.Handled = true;
        };

        WindowIcon = LoadWindowIcon();
        Service = new UsageService(demo);
        _panel = new PanelWindow(this);

        _tray = new WinForms.NotifyIcon
        {
            Text = "ClaUsage",
            Visible = true,
            Icon = TrayIconRenderer.Render(null, Service.Settings, stale: true),
            ContextMenuStrip = BuildTrayMenu(),
        };
        _tray.MouseClick += (_, args) =>
        {
            if (args.Button != WinForms.MouseButtons.Left) return;
            // Clicking the tray icon first takes focus from the panel, which hides it; don't reopen it.
            if ((DateTime.UtcNow - _panel.LastAutoHide).TotalMilliseconds < 350) return;
            _panel.Toggle();
        };
        _tray.BalloonTipClicked += (_, _) => _panel.ShowPanel();

        Service.Changed += UpdateTray;
        Service.Alert += (title, text) => _tray?.ShowBalloonTip(8000, title, text, WinForms.ToolTipIcon.Warning);

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var thread = new Thread(() =>
        {
            while (_showEvent.WaitOne())
                Dispatcher.BeginInvoke(() => _panel?.ShowPanel());
        }) { IsBackground = true, Name = "ClaUsage.ShowListener" };
        thread.Start();

        CreateHotkeyWindow();
        UpdateHotkey();

        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, _) => Dispatcher.BeginInvoke(UpdateTray);
        Microsoft.Win32.SystemEvents.PowerModeChanged += (_, args) =>
        {
            if (args.Mode == Microsoft.Win32.PowerModes.Resume)
                Dispatcher.BeginInvoke(() => _ = Service.RefreshAll(force: false));
        };

        Service.Start();
        UpdateTray();

        _ = Updater.CleanupAsync();
        if (e.Args.Contains("--updated"))
            _tray.ShowBalloonTip(5000, "ClaUsage updated", $"You're on version {Updater.Current}.", WinForms.ToolTipIcon.Info);
        _updateTimer.Tick += (_, _) => _ = CheckForUpdates(manual: false);
        _updateTimer.Start();
        if (!demo)
            Dispatcher.BeginInvoke(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(20));
                await CheckForUpdates(manual: false);
            });

        var open = e.Args.SkipWhile(a => a != "--open").Skip(1).FirstOrDefault();
        if (open == "settings") { ShowSettings(); return; }
        if (open == "signin") { ShowSignIn(null); return; }

        var background = e.Args.Contains("--background");
        if (!background && Service.Accounts.Count == 0) ShowSignIn(null);
        else if (!background) _panel.ShowPanel();
    }

    private WinForms.ContextMenuStrip BuildTrayMenu()
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Open ClaUsage", null, (_, _) => _panel?.ShowPanel());
        menu.Items.Add("Refresh now", null, (_, _) => _ = Service.RefreshAll(force: true));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Add account…", null, (_, _) => ShowSignIn(null));
        menu.Items.Add("Settings…", null, (_, _) => ShowSettings());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Quit());
        return menu;
    }

    private void UpdateTray()
    {
        if (_tray is null) return;
        var highest = Service.Highest();
        var stale = Service.Accounts.All(a => Service.Snapshot(a.Id)?.State is not SnapshotState.Ok);
        var old = _tray.Icon;
        _tray.Icon = TrayIconRenderer.Render(highest?.Fraction, Service.Settings, stale && highest is null);
        old?.Dispose();

        // Tooltip: the top limit of each account (NotifyIcon caps it at 127 characters).
        var lines = new List<string> { "ClaUsage" };
        foreach (var account in Service.Accounts)
        {
            var snap = Service.Snapshot(account.Id);
            if (snap is null) continue;
            if (snap.State == SnapshotState.SignInExpired) { lines.Add($"{account.Kind.DisplayName()}: sign in again"); continue; }
            var parts = snap.Lines
                .Where(l => l.Kind == MetricKind.Percent && !Service.Settings.IsHidden(account.Id, l.Label))
                .Take(2)
                .Select(l => $"{l.Label} {l.Used:0}%");
            var text = string.Join(" · ", parts);
            if (text.Length > 0) lines.Add($"{account.Kind.DisplayName()}: {text}");
        }
        if (Service.Accounts.Count == 0) lines.Add("Click to add an account");
        var tip = string.Join("\n", lines);
        _tray.Text = tip.Length > 127 ? tip[..127] : tip;
    }

    public async Task CheckForUpdates(bool manual)
    {
        if (!manual && (!Service.Settings.CheckForUpdates || !Updater.CanSelfUpdate)) return;
        var release = await Updater.CheckAsync(CancellationToken.None);
        Service.NotifySettingsChanged();
        if (release is null || manual || Service.Settings.AnnouncedUpdate == release.Tag) return;
        Service.Settings.AnnouncedUpdate = release.Tag;
        Service.Settings.Save();
        _tray?.ShowBalloonTip(8000, $"ClaUsage {release.Tag} is available", "Open the panel to update.", WinForms.ToolTipIcon.Info);
    }

    public async Task InstallUpdate()
    {
        if (Updater.Available is not { } release || Installing) return;
        if (!Updater.CanSelfUpdate)
        {
            OAuthFlow.OpenBrowser(release.Page);
            return;
        }
        Installing = true;
        Service.NotifySettingsChanged();
        try
        {
            await Updater.InstallAsync(release, null, CancellationToken.None);
            Quit();
        }
        catch (Exception e)
        {
            Log.Write($"update failed: {e.Message}");
            Installing = false;
            Service.NotifySettingsChanged();
            MessageBox.Show($"The update didn't install: {e.Message}\n\nYou can download it from {release.Page}.", "ClaUsage", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public void ShowSignIn(ProviderKind? kind, Account? replacing = null)
    {
        _panel?.HidePanel();
        if (_signIn is { IsLoaded: true })
        {
            _signIn.Activate();
            return;
        }
        _signIn = new SignInWindow(Service, kind, replacing);
        _signIn.Closed += (_, _) => _signIn = null;
        _signIn.Show();
        _signIn.Activate();
    }

    public void ShowSettings()
    {
        _panel?.HidePanel();
        if (_settings is { IsLoaded: true })
        {
            _settings.Activate();
            return;
        }
        _settings = new SettingsWindow(this);
        _settings.Closed += (_, _) => _settings = null;
        _settings.Show();
        _settings.Activate();
    }

    public void Quit()
    {
        _updateTimer.Stop();
        Service.Stop();
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        UnregisterHotkey();
        _hotkeyWindow?.Dispose();
        // Release the single-instance lock now, so an updated copy starting up can take it.
        try { _mutex?.ReleaseMutex(); } catch { /* not owned */ }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_tray is not null) _tray.Visible = false;
        _mutex?.Dispose();
        base.OnExit(e);
    }

    // ---------------------------------------------------------------- global hotkey

    private void CreateHotkeyWindow()
    {
        var parameters = new HwndSourceParameters("ClaUsage.Hotkey") { Width = 0, Height = 0, WindowStyle = 0, ParentWindow = new IntPtr(-3) /* HWND_MESSAGE */ };
        _hotkeyWindow = new HwndSource(parameters);
        _hotkeyWindow.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg == 0x0312 /* WM_HOTKEY */ && wParam.ToInt32() == HotkeyId)
            {
                _panel?.Toggle();
                handled = true;
            }
            return IntPtr.Zero;
        });
    }

    public void UpdateHotkey()
    {
        UnregisterHotkey();
        if (!Service.Settings.HotkeyEnabled || _hotkeyWindow is null) return;
        // MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 'U'
        _hotkeyRegistered = RegisterHotKey(_hotkeyWindow.Handle, HotkeyId, 0x0002 | 0x0001 | 0x4000, 0x55);
        if (!_hotkeyRegistered) Log.Write("Ctrl+Alt+U is taken by another app");
    }

    private void UnregisterHotkey()
    {
        if (_hotkeyRegistered && _hotkeyWindow is not null) UnregisterHotKey(_hotkeyWindow.Handle, HotkeyId);
        _hotkeyRegistered = false;
    }

    // ---------------------------------------------------------------- CLI

    /// <summary>
    /// <c>ClaUsage.exe --json [provider]</c> prints the last usage the tray app read, as JSON. It never
    /// contacts a provider itself, so it can't eat into rate limits.
    /// </summary>
    private static void RunCli(string[] args)
    {
        AttachConsole(-1);
        var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        if (args.Contains("--selftest"))
        {
            var (ok, report) = DemoData.SelfTest();
            stdout.Write(report);
            stdout.WriteLine(ok ? "SELFTEST PASSED" : "SELFTEST FAILED");
            Environment.ExitCode = ok ? 0 : 1;
            return;
        }
        if (args.Contains("--help"))
        {
            stdout.WriteLine("ClaUsage                         Start the tray app.");
            stdout.WriteLine("ClaUsage --background            Start without opening the panel.");
            stdout.WriteLine("ClaUsage --json [claude|codex|openrouter]   Print the last usage read by the tray app as JSON.");
            stdout.WriteLine("ClaUsage --demo                  Run with sample data, no sign-in.");
            stdout.WriteLine("ClaUsage --selftest              Check the response parsers against sample data.");
            return;
        }
        var filter = args.SkipWhile(a => a != "--json").Skip(1).FirstOrDefault(a => !a.StartsWith("--"));
        var entries = SnapshotCache.Load()
            .Where(e => filter is null || e.Provider.Equals(filter, StringComparison.OrdinalIgnoreCase))
            .Select(e => new
            {
                provider = e.Provider,
                account = e.Name,
                plan = e.Snapshot.Plan,
                state = e.Snapshot.State.ToString().ToLowerInvariant(),
                fetchedAt = e.Snapshot.FetchedAt,
                limits = e.Snapshot.Lines.Select(l => new
                {
                    label = l.Label,
                    kind = l.Kind.ToString().ToLowerInvariant(),
                    used = l.Used,
                    limit = l.Limit,
                    unit = l.Unit,
                    percent = l.Fraction is { } f ? Math.Round(f * 100, 1) : (double?)null,
                    resetsAt = l.ResetsAt,
                }),
            });
        stdout.WriteLine(JsonSerializer.Serialize(entries, Json.Options));
    }

    private static ImageSource? LoadWindowIcon()
    {
        try
        {
            return BitmapFrame.Create(new Uri("pack://application:,,,/Assets/app.ico"));
        }
        catch
        {
            return null;
        }
    }
}
