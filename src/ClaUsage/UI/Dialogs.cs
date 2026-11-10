using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ClaUsage.Core;
using ClaUsage.Providers;

namespace ClaUsage.UI;

/// <summary>Shared look for the small windows: themed background, Segoe UI, centered.</summary>
public class ThemedWindow : Window
{
    protected readonly Theme T = Theme.Current();

    public ThemedWindow()
    {
        Background = T.Background;
        Foreground = T.Text;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 13;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        UseLayoutRounding = true;
        T.Apply(Resources);
        Icon = App.WindowIcon;
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this, T.Dark);
    }

    protected TextBlock Heading(string text) => new()
    {
        Text = text,
        FontSize = 18,
        FontWeight = FontWeights.SemiBold,
        Foreground = T.Text,
        Margin = new Thickness(0, 0, 0, 8),
    };

    protected TextBlock Body(string text, double top = 0) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Foreground = T.Muted,
        Margin = new Thickness(0, top, 0, 0),
    };

    protected Button Button(string text, bool primary = false)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 88,
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(8, 0, 0, 0),
            Cursor = Cursors.Hand,
            Foreground = primary ? Brushes.White : T.Text,
            Background = primary ? T.Accent : T.Card,
            BorderBrush = T.Border,
            BorderThickness = new Thickness(primary ? 0 : 1),
        };
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        border.AppendChild(presenter);
        template.VisualTree = border;
        template.Triggers.Add(new Trigger { Property = IsEnabledProperty, Value = false, Setters = { new Setter(OpacityProperty, 0.45) } });
        template.Triggers.Add(new Trigger { Property = IsMouseOverProperty, Value = true, Setters = { new Setter(OpacityProperty, 0.88) } });
        button.Template = template;
        return button;
    }

    protected TextBox Input(string text = "") => new()
    {
        Text = text,
        Padding = new Thickness(6, 5, 6, 5),
        Background = T.Card,
        Foreground = T.Text,
        CaretBrush = T.Text,
        BorderBrush = T.Border,
        SelectionBrush = T.Accent,
    };

    protected static StackPanel ButtonRow(params UIElement[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        foreach (var b in buttons) row.Children.Add(b);
        return row;
    }
}

public static class DarkTitleBar
{
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Apply(Window window, bool dark)
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            var value = dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref value, sizeof(int));
        }
        catch
        {
            // Older Windows: default title bar.
        }
    }
}

public sealed class InputDialog : ThemedWindow
{
    private readonly TextBox _box;
    private string? _result;

    private InputDialog(string title, string prompt, string initial)
    {
        Title = title;
        Width = 380;
        Topmost = true;
        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(Body(prompt));
        _box = Input(initial);
        _box.Margin = new Thickness(0, 8, 0, 0);
        root.Children.Add(_box);
        var ok = Button("Save", primary: true);
        ok.IsDefault = true;
        ok.Click += (_, _) => { _result = _box.Text; Close(); };
        var cancel = Button("Cancel");
        cancel.IsCancel = true;
        root.Children.Add(ButtonRow(cancel, ok));
        Content = root;
        Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); };
    }

    public static string? Ask(Window? owner, string title, string prompt, string initial)
    {
        var dialog = new InputDialog(title, prompt, initial);
        if (owner is { IsVisible: true }) dialog.Owner = owner;
        dialog.ShowDialog();
        return dialog._result;
    }
}

/// <summary>Add an account: pick a provider, then follow its browser or device-code sign-in.</summary>
public sealed class SignInWindow : ThemedWindow, ILoginUi
{
    private readonly UsageService _service;
    private readonly Account? _replacing;
    private readonly ContentControl _page = new();
    private TaskCompletionSource<string> _apiKey = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _cts;
    private TaskCompletionSource<string> _pasted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<string> PastedCode => _pasted.Task;

    public SignInWindow(UsageService service, ProviderKind? kind, Account? replacing = null)
    {
        _service = service;
        _replacing = replacing;
        Title = "ClaUsage · Add account";
        Width = 440;
        Content = new Border { Padding = new Thickness(22), Child = _page };
        Closed += (_, _) => { _cts?.Cancel(); _apiKey.TrySetCanceled(); _pasted.TrySetCanceled(); };
        if (kind is { } k) Loaded += (_, _) => _ = Start(k);
        else ShowPicker();
    }

    private void ShowPicker()
    {
        var root = new StackPanel();
        root.Children.Add(Heading("Add account"));
        root.Children.Add(Body("Pick a provider. ClaUsage opens its sign-in page in your browser. Tokens are encrypted for your Windows user and only read usage.", 0));
        var list = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        foreach (var provider in Providers.Providers.All)
        {
            list.Children.Add(ProviderTile(provider.Kind, provider.Kind switch
            {
                ProviderKind.Claude => "Session, weekly and model limits, extra usage",
                ProviderKind.Codex => "Session, weekly, Spark, credits",
                ProviderKind.OpenRouter => "API key: balance, spend, key limit, budget",
                ProviderKind.DeepSeek => "API key: balance, granted and topped-up credit",
                _ => "",
            }));
        }
        root.Children.Add(list);
        var cancel = Button("Cancel");
        cancel.IsCancel = true;
        cancel.Click += (_, _) => Close();
        root.Children.Add(ButtonRow(cancel));
        _page.Content = root;
    }

    private UIElement ProviderTile(ProviderKind kind, string subtitle)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var badge = ProviderLogos.Badge(kind, 34);
        badge.Margin = new Thickness(0, 0, 12, 0);
        grid.Children.Add(badge);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = kind.DisplayName(), FontWeight = FontWeights.SemiBold, Foreground = T.Text });
        text.Children.Add(new TextBlock { Text = subtitle, FontSize = 12, Foreground = T.Muted });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var tile = new Border
        {
            Background = T.Card,
            BorderBrush = T.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 8),
            Cursor = Cursors.Hand,
            Child = grid,
        };
        tile.MouseEnter += (_, _) => tile.Background = T.CardHover;
        tile.MouseLeave += (_, _) => tile.Background = T.Card;
        tile.MouseLeftButtonUp += (_, _) => _ = Start(kind);
        return tile;
    }

    private async Task Start(ProviderKind kind)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _pasted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _apiKey = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        ShowWorking(kind, kind.UsesApiKey() ? "Checking the key…" : "Starting sign-in…");
        try
        {
            var account = await Providers.Providers.For(kind).LoginAsync(this, _cts.Token);
            _service.AddOrUpdate(account, _replacing);
            Close();
        }
        catch (OperationCanceledException)
        {
            // Window closed.
        }
        catch (Exception e)
        {
            Log.Write($"{kind} sign-in failed: {e.Message}");
            ShowError(kind, e is ProviderException or SignInExpiredException ? e.Message : $"Sign-in failed: {e.Message}");
        }
    }

    private void ShowWorking(ProviderKind kind, string text)
    {
        var root = new StackPanel();
        root.Children.Add(Heading($"Signing in to {kind.DisplayName()}"));
        root.Children.Add(Body(text));
        root.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 3, Margin = new Thickness(0, 14, 0, 0), Foreground = T.Accent, Background = T.Track, BorderThickness = new Thickness(0) });
        var cancel = Button("Cancel");
        cancel.Click += (_, _) => Close();
        root.Children.Add(ButtonRow(cancel));
        _page.Content = root;
    }

    public void ShowBrowser(Uri url) => Dispatcher.Invoke(() =>
    {
        var root = new StackPanel();
        root.Children.Add(Heading("Finish in your browser"));
        root.Children.Add(Body("Sign in on the page that just opened. ClaUsage picks it up as soon as you approve."));
        root.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 3, Margin = new Thickness(0, 14, 0, 14), Foreground = T.Accent, Background = T.Track, BorderThickness = new Thickness(0) });
        root.Children.Add(Body("If the browser shows a code instead, paste it here:"));
        var box = Input();
        box.Margin = new Thickness(0, 6, 0, 0);
        root.Children.Add(box);

        var reopen = Button("Open browser again");
        reopen.Margin = new Thickness(0);
        reopen.Click += (_, _) => OAuthFlow.OpenBrowser(url);
        var copy = Button("Copy link");
        copy.Click += (_, _) => { try { Clipboard.SetText(url.AbsoluteUri); } catch { /* busy */ } };
        var cont = Button("Continue", primary: true);
        cont.IsDefault = true;
        cont.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(box.Text)) return;
            cont.IsEnabled = false;
            _pasted.TrySetResult(box.Text);
        };
        var cancel = Button("Cancel");
        cancel.Click += (_, _) => Close();

        var rows = new DockPanel { Margin = new Thickness(0, 18, 0, 0), LastChildFill = false };
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(reopen);
        left.Children.Add(copy);
        DockPanel.SetDock(left, Dock.Left);
        rows.Children.Add(left);
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(cancel);
        right.Children.Add(cont);
        DockPanel.SetDock(right, Dock.Right);
        rows.Children.Add(right);
        root.Children.Add(rows);
        _page.Content = root;
    });

    public Task<string> RequestApiKey(Uri createKeyUrl, string hint)
    {
        Dispatcher.Invoke(() =>
        {
            var root = new StackPanel();
            root.Children.Add(Heading(_replacing is null ? "Add API key" : "Replace API key"));
            root.Children.Add(Body(hint));
            var link = new TextBlock { Margin = new Thickness(0, 8, 0, 0) };
            var hyperlink = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run("Create a key on " + createKeyUrl.Host)) { Foreground = T.Accent };
            hyperlink.Click += (_, _) => OAuthFlow.OpenBrowser(createKeyUrl);
            link.Inlines.Add(hyperlink);
            root.Children.Add(link);
            var box = new PasswordBox
            {
                Margin = new Thickness(0, 12, 0, 0),
                Padding = new Thickness(6, 5, 6, 5),
                Background = T.Card,
                Foreground = T.Text,
                CaretBrush = T.Text,
                BorderBrush = T.Border,
            };
            root.Children.Add(box);
            root.Children.Add(Body("The key is checked with one read, then stored encrypted for your Windows user.", 6));
            var add = Button(_replacing is null ? "Add account" : "Replace key", primary: true);
            add.IsDefault = true;
            add.Click += (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(box.Password)) return;
                add.IsEnabled = false;
                _apiKey.TrySetResult(box.Password);
            };
            var cancel = Button("Cancel");
            cancel.Click += (_, _) => Close();
            root.Children.Add(ButtonRow(cancel, add));
            _page.Content = root;
            box.Focus();
        });
        return _apiKey.Task;
    }

    public void ShowDeviceCode(string userCode, Uri verifyUrl) => Dispatcher.Invoke(() =>
    {
        var root = new StackPanel();
        root.Children.Add(Heading("Enter this code"));
        root.Children.Add(Body($"Open {verifyUrl.Host} and enter the code below. ClaUsage continues by itself once you approve."));
        root.Children.Add(new TextBox
        {
            Text = userCode,
            IsReadOnly = true,
            FontSize = 28,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontWeight = FontWeights.SemiBold,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 16, 0, 8),
            Padding = new Thickness(8),
            Background = T.Card,
            Foreground = T.Text,
            BorderBrush = T.Border,
        });
        root.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 3, Margin = new Thickness(0, 6, 0, 0), Foreground = T.Accent, Background = T.Track, BorderThickness = new Thickness(0) });
        var copy = Button("Copy code");
        copy.Click += (_, _) => { try { Clipboard.SetText(userCode); } catch { /* busy */ } };
        var open = Button("Open browser", primary: true);
        open.Click += (_, _) => OAuthFlow.OpenBrowser(verifyUrl);
        var cancel = Button("Cancel");
        cancel.Click += (_, _) => Close();
        root.Children.Add(ButtonRow(cancel, copy, open));
        _page.Content = root;
        OAuthFlow.OpenBrowser(verifyUrl);
    });

    private void ShowError(ProviderKind kind, string message)
    {
        var root = new StackPanel();
        root.Children.Add(Heading("Sign-in didn't finish"));
        root.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = T.Critical });
        var back = Button("Back");
        back.Click += (_, _) => ShowPicker();
        var retry = Button("Try again", primary: true);
        retry.Click += (_, _) => _ = Start(kind);
        root.Children.Add(ButtonRow(back, retry));
        _page.Content = root;
    }
}

public sealed class SettingsWindow : ThemedWindow
{
    private readonly App _app;

    public SettingsWindow(App app)
    {
        _app = app;
        Title = "ClaUsage · Settings";
        Width = 460;
        Build();
    }

    private void Build()
    {
        var settings = _app.Service.Settings;
        var root = new StackPanel { Margin = new Thickness(22) };
        root.Children.Add(Heading("Settings"));

        // Refresh interval.
        var refresh = Combo(new[] { 2, 5, 10, 15, 30, 60 }.Select(m => ($"Every {m} min", (object)m)), settings.RefreshMinutes);
        refresh.SelectionChanged += (_, _) => { settings.RefreshMinutes = (int)((ComboBoxItem)refresh.SelectedItem).Tag; _app.Service.NotifySettingsChanged(); };
        root.Children.Add(Row("Refresh", refresh, "Claude allows about 30 usage reads an hour per account, shared with Claude Code."));

        // Theme: the settings window reopens in the new colors; the panel picks them up when it next opens.
        var theme = Combo(new[] { ("Same as Windows", (object)AppTheme.System), ("Light", AppTheme.Light), ("Dark", AppTheme.Dark) }, settings.AppTheme);
        theme.SelectionChanged += (_, _) =>
        {
            settings.AppTheme = Theme.Mode = (AppTheme)((ComboBoxItem)theme.SelectedItem).Tag;
            _app.Service.NotifySettingsChanged();
            if (Theme.IsDark == T.Dark) return;
            Dispatcher.BeginInvoke(() => { Close(); _app.ShowSettings(); });
        };
        root.Children.Add(Row("Theme", theme, null));

        // Tray.
        var tray = Combo(new[] { ("Highest usage as a number", (object)TrayDisplay.HighestPercent), ("Ring gauge", TrayDisplay.IconOnly) }, settings.TrayDisplay);
        tray.SelectionChanged += (_, _) => { settings.TrayDisplay = (TrayDisplay)((ComboBoxItem)tray.SelectedItem).Tag; _app.Service.NotifySettingsChanged(); };
        root.Children.Add(Row("Tray icon", tray, null));

        // Thresholds.
        var warn = Combo(new[] { 50, 60, 70, 75, 80 }.Select(p => ($"{p}%", (object)p)), settings.WarnPercent);
        warn.SelectionChanged += (_, _) => { settings.WarnPercent = (int)((ComboBoxItem)warn.SelectedItem).Tag; _app.Service.NotifySettingsChanged(); };
        root.Children.Add(Row("Running low at", warn, null));
        var critical = Combo(new[] { 80, 85, 90, 95 }.Select(p => ($"{p}%", (object)p)), settings.CriticalPercent);
        critical.SelectionChanged += (_, _) => { settings.CriticalPercent = (int)((ComboBoxItem)critical.SelectedItem).Tag; _app.Service.NotifySettingsChanged(); };
        root.Children.Add(Row("Almost used up at", critical, null));
        var lowBalance = Combo(new[] { 0d, 1, 2, 5, 10, 20 }.Select(v => (v == 0 ? "Off" : $"Below {v:0}", (object)v)), settings.LowBalance);
        lowBalance.SelectionChanged += (_, _) => { settings.LowBalance = (double)((ComboBoxItem)lowBalance.SelectedItem).Tag; _app.Service.NotifySettingsChanged(); };
        root.Children.Add(Row("Low API balance", lowBalance, "OpenRouter and DeepSeek, in the account's currency."));

        root.Children.Add(Check("Notify when a limit runs low", settings.Notifications, v => settings.Notifications = v));
        root.Children.Add(Check("Show even-pace marker on bars", settings.ShowPace, v => settings.ShowPace = v));
        root.Children.Add(Check("Hide email addresses", settings.HideEmails, v => settings.HideEmails = Account.MaskEmails = v));
        root.Children.Add(Check("Global shortcut Ctrl+Alt+U toggles the panel", settings.HotkeyEnabled, v => { settings.HotkeyEnabled = v; _app.UpdateHotkey(); }));
        var launch = Check("Start ClaUsage when I sign in to Windows", LaunchAtLogin.IsEnabled, v =>
        {
            try { LaunchAtLogin.Set(v); } catch (Exception e) { Log.Write($"launch at login: {e.Message}"); }
        });
        root.Children.Add(launch);

        // Updates.
        root.Children.Add(Check("Check for updates automatically", settings.CheckForUpdates, v => settings.CheckForUpdates = v));
        var updateRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var checkNow = Button("Check now");
        checkNow.Margin = new Thickness(0);
        var status = new TextBlock { Foreground = T.Muted, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        void ShowStatus()
        {
            var u = _app.Updater;
            status.Text = u.Available is { } r ? $"Version {r.Tag} is available. Update from the panel."
                : u.LastError is { } err ? $"Couldn't check: {err}"
                : u.LastCheck is not null ? $"You're up to date ({Updater.Current.ToString(3)})."
                : $"Version {Updater.Current.ToString(3)}.";
        }
        ShowStatus();
        checkNow.Click += async (_, _) =>
        {
            checkNow.IsEnabled = false;
            status.Text = "Checking…";
            await _app.CheckForUpdates(manual: true);
            ShowStatus();
            checkNow.IsEnabled = true;
        };
        updateRow.Children.Add(checkNow);
        updateRow.Children.Add(status);
        root.Children.Add(updateRow);

        // Accounts.
        root.Children.Add(new TextBlock { Text = "ACCOUNTS", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = T.Muted, Margin = new Thickness(0, 18, 0, 6) });
        if (_app.Service.Accounts.Count == 0) root.Children.Add(Body("No accounts yet."));
        foreach (var account in _app.Service.Accounts.ToList())
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = new StackPanel();
            name.Children.Add(new TextBlock { Text = account.DisplayName, Foreground = T.Text, TextTrimming = TextTrimming.CharacterEllipsis });
            name.Children.Add(new TextBlock { Text = account.Kind.DisplayName(), FontSize = 11.5, Foreground = T.Muted });
            grid.Children.Add(name);
            var remove = Button("Remove");
            remove.VerticalAlignment = VerticalAlignment.Center;
            remove.Click += (_, _) =>
            {
                if (!AppDialog.Confirm(this, "Remove account?", $"{account.Kind.DisplayName()} · {account.DisplayName}",
                        "Remove", "Its sign-in is deleted from this PC.", AppDialog.Tone.Danger, account.Kind)) return;
                _app.Service.Remove(account);
                Build();
            };
            Grid.SetColumn(remove, 1);
            grid.Children.Add(remove);
            root.Children.Add(grid);
        }
        var add = Button("Add account…");
        add.Margin = new Thickness(0, 4, 0, 0);
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.Click += (_, _) => _app.ShowSignIn(null);
        root.Children.Add(add);

        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        root.Children.Add(new TextBlock
        {
            Text = $"CLAUSAGE • v{version?.ToString(3)}",
            FontWeight = FontWeights.SemiBold,
            Foreground = T.Text,
            Margin = new Thickness(0, 18, 0, 0),
        });
        root.Children.Add(Body("by MANCIO | hypemancio | @_hypemancio_", 2));
        root.Children.Add(Body($"Data: {AppPaths.DataDir}", 6));
        // MIT asks that the notice of the code ClaUsage builds on stays reachable.
        var licenses = new TextBlock { Margin = new Thickness(0, 4, 0, 0), FontSize = 11.5 };
        var licensesLink = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run("Open-source licenses")) { Foreground = T.Muted };
        licensesLink.Click += (_, _) => OAuthFlow.OpenBrowser(new Uri("https://github.com/hypemancio/ClaUsage/blob/main/THIRD_PARTY_NOTICES.txt"));
        licenses.Inlines.Add(licensesLink);
        root.Children.Add(licenses);
        var close = Button("Close", primary: true);
        close.IsCancel = true;
        close.Click += (_, _) => Close();
        root.Children.Add(ButtonRow(close));
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 760 };
    }

    private ComboBox Combo(IEnumerable<(string Text, object Value)> items, object selected)
    {
        var combo = new ComboBox { MinWidth = 200, Padding = new Thickness(6, 4, 6, 4) };
        foreach (var (text, value) in items)
        {
            var item = new ComboBoxItem { Content = text, Tag = value };
            combo.Items.Add(item);
            if (Equals(value, selected)) combo.SelectedItem = item;
        }
        if (combo.SelectedItem is null) combo.SelectedIndex = 0;
        return combo;
    }

    private UIElement Row(string label, UIElement control, string? hint)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 6, 0, 6) };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = label, Foreground = T.Text, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        stack.Children.Add(grid);
        if (hint is not null) stack.Children.Add(new TextBlock { Text = hint, FontSize = 11.5, Foreground = T.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
        return stack;
    }

    private CheckBox Check(string text, bool value, Action<bool> set)
    {
        var box = new CheckBox { Content = text, IsChecked = value, Foreground = T.Text, Margin = new Thickness(0, 6, 0, 0) };
        box.Checked += (_, _) => { set(true); _app.Service.NotifySettingsChanged(); };
        box.Unchecked += (_, _) => { set(false); _app.Service.NotifySettingsChanged(); };
        return box;
    }
}

/// <summary>
/// The app's own confirm and notice dialogs, in the same look as its other windows (theme, fonts,
/// provider mark), instead of the system message box.
/// </summary>
public sealed class AppDialog : ThemedWindow
{
    public enum Tone { Info, Warning, Danger }

    private bool _confirmed;

    private AppDialog(string title, string message, string? detail, string confirmText, string? cancelText, Tone tone, ProviderKind? provider)
    {
        Title = "ClaUsage";
        Width = 420;
        ShowInTaskbar = false;
        Topmost = true;

        var root = new Grid { Margin = new Thickness(22, 20, 22, 18) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        FrameworkElement mark = provider is { } kind
            ? ProviderLogos.Badge(kind, 36)
            : new Border
            {
                Width = 36,
                Height = 36,
                CornerRadius = new CornerRadius(18),
                Background = tone switch { Tone.Danger => T.Critical, Tone.Warning => T.Warn, _ => T.Accent },
                Child = new TextBlock
                {
                    Text = tone == Tone.Info ? "i" : "!",
                    Foreground = Brushes.White,
                    FontWeight = FontWeights.Bold,
                    FontSize = 18,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
        mark.Margin = new Thickness(0, 2, 14, 0);
        mark.VerticalAlignment = VerticalAlignment.Top;
        root.Children.Add(mark);

        var text = new StackPanel();
        var heading = Heading(title);
        heading.FontSize = 16;
        heading.TextWrapping = TextWrapping.Wrap;
        heading.Margin = new Thickness(0, 0, 0, 6);
        text.Children.Add(heading);
        text.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = T.Text });
        if (!string.IsNullOrWhiteSpace(detail))
            text.Children.Add(new TextBlock
            {
                Text = detail,
                TextWrapping = TextWrapping.Wrap,
                Foreground = tone == Tone.Danger ? T.Critical : T.Muted,
                FontSize = 12,
                Margin = new Thickness(0, 8, 0, 0),
            });

        var confirm = Button(confirmText, primary: true);
        if (tone == Tone.Danger) confirm.Background = T.Critical;
        confirm.Click += (_, _) => { _confirmed = true; Close(); };
        var buttons = new List<UIElement>();
        if (cancelText is not null)
        {
            // Cancel is the default: Enter or Esc never spends or deletes anything by accident.
            var cancel = Button(cancelText);
            cancel.IsDefault = true;
            cancel.IsCancel = true;
            cancel.Click += (_, _) => Close();
            buttons.Add(cancel);
            Loaded += (_, _) => cancel.Focus();
        }
        else
        {
            confirm.IsDefault = true;
            confirm.IsCancel = true;
            Loaded += (_, _) => confirm.Focus();
        }
        buttons.Add(confirm);
        text.Children.Add(ButtonRow(buttons.ToArray()));

        Grid.SetColumn(text, 1);
        root.Children.Add(text);
        Content = root;
    }

    private static bool Show(Window? owner, AppDialog dialog)
    {
        if (owner is { IsVisible: true }) dialog.Owner = owner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.ShowDialog();
        return dialog._confirmed;
    }

    /// <summary>Asks before an action; true only when the user picks <paramref name="confirmText"/>.</summary>
    public static bool Confirm(Window? owner, string title, string message, string confirmText,
        string? detail = null, Tone tone = Tone.Warning, ProviderKind? provider = null, string cancelText = "Cancel") =>
        Show(owner, new AppDialog(title, message, detail, confirmText, cancelText, tone, provider));

    public static void Notice(Window? owner, string title, string message, Tone tone = Tone.Info, ProviderKind? provider = null) =>
        Show(owner, new AppDialog(title, message, null, "OK", null, tone, provider));
}
