using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using ClaUsage.Core;
using WinForms = System.Windows.Forms;

namespace ClaUsage.UI;

public partial class PanelWindow : Window
{
    private readonly App _app;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(20) };
    private Theme _theme = Theme.Current();

    /// <summary>When the panel last hid itself on losing focus; a tray click right after must not reopen it.</summary>
    public DateTime LastAutoHide { get; private set; } = DateTime.MinValue;

    public PanelWindow(App app)
    {
        _app = app;
        InitializeComponent();
        _app.Service.Changed += () => { if (IsVisible) Render(); };
        _clock.Tick += (_, _) => Render();
        Deactivated += (_, _) =>
        {
            if (!IsVisible || _menuOpen) return;
            LastAutoHide = DateTime.UtcNow;
            HidePanel();
        };
        PreviewKeyDown += OnKey;
        SourceInitialized += (_, _) => ApplyToolWindowStyle();
        // Content grows when fresh numbers land; keep the panel anchored to the taskbar corner.
        SizeChanged += (_, _) => { if (IsVisible) PlaceNearTray(); };
    }

    private bool _menuOpen;

    public void Toggle()
    {
        if (IsVisible) HidePanel();
        else ShowPanel();
    }

    public void ShowPanel()
    {
        _theme = Theme.Current();
        _theme.Apply(Resources);
        Render();
        Opacity = 0;
        Show();
        UpdateLayout();
        PlaceNearTray();
        Opacity = 1;
        Activate();
        Focus();
        _clock.Start();
    }

    public void HidePanel()
    {
        _clock.Stop();
        Hide();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) HidePanel();
        else if (e.Key == Key.F5 || (e.Key == Key.R && Keyboard.Modifiers == ModifierKeys.Control)) _ = _app.Service.RefreshAll(force: true);
        else if (e.Key == Key.D1 && Keyboard.Modifiers == ModifierKeys.Control) TabPriority.IsChecked = true;
        else if (e.Key == Key.D2 && Keyboard.Modifiers == ModifierKeys.Control) TabAll.IsChecked = true;
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) Render();
    }

    // ---------------------------------------------------------------- rendering

    public void Render()
    {
        if (Cards is null) return;
        var service = _app.Service;
        var now = DateTimeOffset.UtcNow;
        Cards.Children.Clear();

        if (_app.Updater.Available is { } release)
            Cards.Children.Add(UpdateBanner(release));

        if (service.Accounts.Count == 0)
        {
            Cards.Children.Add(EmptyState());
        }
        else if (TabPriority.IsChecked == true)
        {
            RenderPriority(now);
        }
        else
        {
            foreach (var account in service.Accounts) Cards.Children.Add(AccountCard(account, now));
        }

        RefreshButton.IsEnabled = !service.IsRefreshing;
        FooterText.Text = service.IsRefreshing
            ? "Refreshing…"
            : service.LastUpdated is { } at ? $"Updated {Format.Ago(at, now)}  ·  Ctrl+Alt+U toggles this panel" : "Ctrl+Alt+U toggles this panel";
    }

    private UIElement UpdateBanner(Updater.Release release)
    {
        var card = CardBorder();
        card.Padding = new Thickness(12, 8, 8, 8);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = $"ClaUsage {release.Tag} is available", Foreground = _theme.Text, FontWeight = FontWeights.SemiBold });
        var notes = new TextBlock { FontSize = 11.5, Foreground = _theme.Accent, Cursor = Cursors.Hand, Text = "What's new" };
        notes.MouseLeftButtonUp += (_, _) => OAuthFlow.OpenBrowser(release.Page);
        text.Children.Add(notes);
        grid.Children.Add(text);
        var button = PrimaryButton(_app.Installing ? "Updating…" : "Update");
        button.IsEnabled = !_app.Installing;
        button.VerticalAlignment = VerticalAlignment.Center;
        button.Click += (_, _) => _ = _app.InstallUpdate();
        Grid.SetColumn(button, 1);
        grid.Children.Add(button);
        card.Child = grid;
        return card;
    }

    private UIElement EmptyState()
    {
        var panel = new StackPanel { Margin = new Thickness(16, 28, 16, 28), HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(new TextBlock
        {
            Text = "No accounts yet",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = _theme.Text,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Sign in to Claude or ChatGPT, or add an OpenRouter or DeepSeek key, to see your limits and spend here.",
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Foreground = _theme.Muted,
            Margin = new Thickness(0, 6, 0, 14),
        });
        var button = PrimaryButton("Add account");
        button.HorizontalAlignment = HorizontalAlignment.Center;
        button.Click += Add_Click;
        panel.Children.Add(button);
        return panel;
    }

    private void RenderPriority(DateTimeOffset now)
    {
        var service = _app.Service;
        var settings = service.Settings;
        var attention = new List<UIElement>();
        var usedUp = new List<UIElement>();
        var low = new List<(double, UIElement)>();
        var burning = new List<(double, UIElement)>();

        foreach (var account in service.Accounts)
        {
            var snap = service.Snapshot(account.Id);
            if (snap?.State == SnapshotState.SignInExpired)
                attention.Add(AttentionRow(account, snap));
            if (snap is null) continue;
            foreach (var line in snap.Lines)
            {
                if (line.Fraction is not { } f || settings.IsHidden(account.Id, line.Label)) continue;
                var percent = f * 100;
                if (percent >= 100) usedUp.Add(MetricRow(account, line, now, withAccount: true));
                else if (percent >= settings.WarnPercent) low.Add((f, MetricRow(account, line, now, withAccount: true)));
                else if (IsBurning(line, now, out var ahead)) burning.Add((ahead, MetricRow(account, line, now, withAccount: true)));
            }
        }

        var any = false;
        void Section(string title, IEnumerable<UIElement> rows)
        {
            var list = rows.ToList();
            if (list.Count == 0) return;
            any = true;
            Cards.Children.Add(SectionTitle(title));
            var card = CardBorder();
            var stack = new StackPanel();
            foreach (var row in list) stack.Children.Add(row);
            card.Child = stack;
            Cards.Children.Add(card);
        }

        Section("Needs attention", attention);
        Section("Used up", usedUp);
        Section("Running low", low.OrderByDescending(x => x.Item1).Select(x => x.Item2));
        Section("Burning fast", burning.OrderByDescending(x => x.Item1).Select(x => x.Item2));

        if (!any)
        {
            Cards.Children.Add(new TextBlock
            {
                Text = "All clear. Nothing is running low.",
                Foreground = _theme.Muted,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 26, 0, 26),
            });
        }
    }

    /// <summary>More than 10 points ahead of an even pace while still under the warn threshold.</summary>
    private static bool IsBurning(MetricLine line, DateTimeOffset now, out double ahead)
    {
        ahead = 0;
        if (line.Fraction is not { } f || line.PaceFraction(now) is not { } pace || pace < 0.05) return false;
        ahead = (f - pace) * 100;
        return ahead >= 10;
    }

    private UIElement SectionTitle(string text) => new TextBlock
    {
        Text = text.ToUpperInvariant(),
        FontSize = 11,
        FontWeight = FontWeights.SemiBold,
        Foreground = _theme.Muted,
        Margin = new Thickness(8, 10, 0, 4),
    };

    private Border CardBorder() => new()
    {
        Background = _theme.Card,
        BorderBrush = _theme.Border,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(12, 10, 12, 6),
        Margin = new Thickness(0, 0, 0, 8),
    };

    private UIElement AttentionRow(Account account, UsageSnapshot snap)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = $"{account.Kind.DisplayName()} · {account.DisplayName}", Foreground = _theme.Text, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = snap.Message ?? "Sign-in expired.", Foreground = _theme.Muted, FontSize = 12 });
        grid.Children.Add(text);
        var fix = PrimaryButton("Fix");
        fix.VerticalAlignment = VerticalAlignment.Center;
        fix.Click += (_, _) => _app.ShowSignIn(account.Kind, account);
        Grid.SetColumn(fix, 1);
        grid.Children.Add(fix);
        return grid;
    }

    private UIElement AccountCard(Account account, DateTimeOffset now)
    {
        var service = _app.Service;
        var snap = service.Snapshot(account.Id);
        var card = CardBorder();
        var stack = new StackPanel();
        card.Child = stack;

        // Header: avatar, name, plan, state.
        var header = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var avatar = ProviderLogos.Badge(account.Kind, 26);
        avatar.Margin = new Thickness(0, 0, 10, 0);
        header.Children.Add(avatar);

        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(new TextBlock { Text = account.Kind.DisplayName(), FontWeight = FontWeights.SemiBold, Foreground = _theme.Text });
        if (!string.IsNullOrEmpty(snap?.Plan))
        {
            titleRow.Children.Add(new Border
            {
                Margin = new Thickness(6, 0, 0, 0),
                Padding = new Thickness(6, 0, 6, 1),
                CornerRadius = new CornerRadius(4),
                Background = _theme.CardHover,
                BorderBrush = _theme.Border,
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = snap!.Plan, FontSize = 11, Foreground = _theme.Muted },
            });
        }
        names.Children.Add(titleRow);
        names.Children.Add(new TextBlock { Text = account.DisplayName, FontSize = 11.5, Foreground = _theme.Muted, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(names, 1);
        header.Children.Add(names);

        var stateText = new TextBlock { FontSize = 11, Foreground = _theme.Muted, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8, 2, 0, 0) };
        if (snap?.State == SnapshotState.Loading) stateText.Text = "Loading…";
        Grid.SetColumn(stateText, 2);
        header.Children.Add(stateText);
        stack.Children.Add(header);

        // State banner.
        if (snap is { State: SnapshotState.SignInExpired or SnapshotState.RateLimited or SnapshotState.Error })
        {
            var banner = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            banner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            banner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var message = snap.Message ?? "Something went wrong.";
            if (snap.RetryAt is { } retry && snap.State != SnapshotState.SignInExpired) message += $" Retrying {Format.ResetIn(retry, now)}.";
            banner.Children.Add(new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Foreground = snap.State == SnapshotState.SignInExpired ? _theme.Critical : _theme.Warn,
                VerticalAlignment = VerticalAlignment.Center,
            });
            if (snap.State == SnapshotState.SignInExpired)
            {
                var again = PrimaryButton(account.Kind.UsesApiKey() ? "Replace key" : "Sign in again");
                again.Margin = new Thickness(8, 0, 0, 0);
                again.Click += (_, _) => _app.ShowSignIn(account.Kind, account);
                Grid.SetColumn(again, 1);
                banner.Children.Add(again);
            }
            stack.Children.Add(banner);
        }

        var lines = snap?.Lines.Where(l => !service.Settings.IsHidden(account.Id, l.Label)).ToList() ?? new();
        if (lines.Count == 0 && snap?.State == SnapshotState.Ok)
            stack.Children.Add(new TextBlock { Text = "This account reports no limits.", Foreground = _theme.Muted, Margin = new Thickness(0, 0, 0, 6) });
        foreach (var line in lines) stack.Children.Add(MetricRow(account, line, now, withAccount: false));

        if (snap?.FetchedAt is { } fetched && snap.State != SnapshotState.Loading)
            stateText.Text = Format.Ago(fetched, now);

        card.ContextMenu = AccountMenu(account, snap);
        return card;
    }

    private UIElement MetricRow(Account account, MetricLine line, DateTimeOffset now, bool withAccount)
    {
        var settings = _app.Service.Settings;
        var root = new StackPanel { Margin = new Thickness(0, 2, 0, 8) };

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = withAccount ? $"{account.Kind.DisplayName()} {line.Label}" : line.Label;
        top.Children.Add(new TextBlock { Text = label, Foreground = _theme.Text, TextTrimming = TextTrimming.CharacterEllipsis });
        var value = new TextBlock
        {
            Text = Format.Value(line),
            Foreground = line.Fraction is { } vf ? _theme.ForFraction(vf, settings) : _theme.Text,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(8, 0, 0, 0),
        };
        if (line.Fraction is { } f0 && f0 < settings.WarnPercent / 100.0) value.Foreground = _theme.Text;
        Grid.SetColumn(value, 1);
        top.Children.Add(value);
        if (line.ActionId is not null && account.Kind == ProviderKind.Claude)
        {
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var use = PrimaryButton("Use");
            use.Padding = new Thickness(10, 1, 10, 2);
            use.Margin = new Thickness(8, 0, 0, 0);
            use.FontSize = 12;
            use.IsEnabled = !_app.Service.IsRefreshing;
            use.ToolTip = line.ActionDetail;
            use.Click += (_, _) => _ = UseClaudeReset(account, line);
            Grid.SetColumn(use, 2);
            top.Children.Add(use);
        }
        root.Children.Add(top);

        if (line.Fraction is { } fraction)
        {
            var pace = settings.ShowPace ? line.PaceFraction(now) : null;
            root.Children.Add(Bar(fraction, pace, _theme.ForFraction(fraction, settings)));
        }

        var details = new List<string>();
        if (withAccount && _app.Service.Accounts.Count(a => a.Kind == account.Kind) > 1) details.Add(account.DisplayName);
        if (line.ResetsAt is { } reset) details.Add($"Resets {Format.ResetIn(reset, now)} · {Format.ResetClock(reset, now)}");
        if (line.Expiries is { Count: > 0 } expiries && expiries.FirstOrDefault(e => e > now) is var next && next > now)
            details.Add($"Next expires {Format.ResetIn(next, now)} · {Format.ResetClock(next, now)}");
        var paceText = PaceText(line, now, out var paceBrush);
        if (details.Count > 0 || paceText is not null)
        {
            var sub = new Grid { Margin = new Thickness(0, 3, 0, 0) };
            sub.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            sub.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            sub.Children.Add(new TextBlock { Text = string.Join("  ·  ", details), FontSize = 11.5, Foreground = _theme.Muted, TextTrimming = TextTrimming.CharacterEllipsis });
            if (paceText is not null)
            {
                var p = new TextBlock { Text = paceText, FontSize = 11.5, Foreground = paceBrush, Margin = new Thickness(8, 0, 0, 0) };
                Grid.SetColumn(p, 1);
                sub.Children.Add(p);
            }
            root.Children.Add(sub);
        }

        root.ToolTip = line.ResetsAt is { } r2 ? "Resets " + r2.ToLocalTime().ToString("dddd d MMM, HH:mm", System.Globalization.CultureInfo.InvariantCulture) : null;
        return root;
    }

    /// <summary>How far usage is from an even pace through the window, and when it would run out at this rate.</summary>
    private string? PaceText(MetricLine line, DateTimeOffset now, out Brush brush)
    {
        brush = _theme.Muted;
        if (line.Kind != MetricKind.Percent || line.PaceFraction(now) is not { } pace || pace < 0.03 || line.ResetsAt is not { } reset || line.PeriodMs is not { } period) return null;
        var used = line.Used;
        if (used >= 100) { brush = _theme.Critical; return "Used up"; }
        var diff = used - pace * 100;
        if (diff <= -5) return $"{-diff:0}% under pace";
        if (diff < 5) return "On pace";
        // Projects the current rate to the end of the window.
        var elapsed = TimeSpan.FromMilliseconds(period * pace);
        var perMs = used / elapsed.TotalMilliseconds;
        var runOut = perMs > 0 ? now + TimeSpan.FromMilliseconds((100 - used) / perMs) : (DateTimeOffset?)null;
        brush = diff >= 15 ? _theme.Critical : _theme.Warn;
        if (runOut is { } ro && ro < reset) return $"Runs out in {Format.Duration(ro - now)}";
        return $"{diff:0}% over pace";
    }

    private Grid Bar(double fraction, double? pace, Brush fill)
    {
        var grid = new Grid { Height = 10, Margin = new Thickness(0, 4, 0, 0) };
        var track = new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = _theme.Track, VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(track);

        var fillGrid = new Grid { Height = 6, VerticalAlignment = VerticalAlignment.Center };
        fillGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(fraction, GridUnitType.Star) });
        fillGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - fraction, GridUnitType.Star) });
        if (fraction > 0) fillGrid.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = fill, MinWidth = 6 });
        grid.Children.Add(fillGrid);

        if (pace is { } p and > 0.01 and < 0.99)
        {
            var paceGrid = new Grid();
            paceGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(p, GridUnitType.Star) });
            paceGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - p, GridUnitType.Star) });
            paceGrid.Children.Add(new Rectangle
            {
                Width = 2,
                Height = 10,
                RadiusX = 1,
                RadiusY = 1,
                Fill = _theme.Pace,
                Opacity = 0.75,
                HorizontalAlignment = HorizontalAlignment.Right,
                ToolTip = "Even pace",
            });
            grid.Children.Add(paceGrid);
        }
        return grid;
    }

    private Button PrimaryButton(string text)
    {
        var button = new Button
        {
            Content = text,
            Padding = new Thickness(12, 4, 12, 5),
            Cursor = Cursors.Hand,
            Foreground = Brushes.White,
            Background = _theme.Accent,
            BorderThickness = new Thickness(0),
            FontWeight = FontWeights.SemiBold,
        };
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        border.AppendChild(presenter);
        template.VisualTree = border;
        button.Template = template;
        return button;
    }

    /// <summary>Spends a Claude reset after an explicit confirmation; the outcome is shown either way.</summary>
    private async Task UseClaudeReset(Account account, MetricLine line)
    {
        _menuOpen = true;
        try
        {
            var left = (int)line.Used;
            if (!AppDialog.Confirm(this,
                    "Use a rate-limit reset?",
                    $"{account.DisplayName} has {left} reset{(left == 1 ? "" : "s")} available.",
                    "Use reset",
                    line.ActionDetail ?? "Refills your limits now. This can't be undone.",
                    AppDialog.Tone.Warning,
                    account.Kind))
                return;
            var result = await _app.Service.ClaimClaudeReset(account);
            var success = result.Outcome == Providers.ClaudeProvider.ClaimOutcome.Success;
            AppDialog.Notice(this, success ? "Reset used" : "Reset not used", result.Message,
                success ? AppDialog.Tone.Info : AppDialog.Tone.Warning, account.Kind);
        }
        finally
        {
            _menuOpen = false;
            if (IsVisible) Activate();
        }
    }

    // ---------------------------------------------------------------- menus

    private ContextMenu AccountMenu(Account account, UsageSnapshot? snap)
    {
        var service = _app.Service;
        var menu = new ContextMenu();
        menu.Opened += (_, _) => _menuOpen = true;
        menu.Closed += (_, _) => { _menuOpen = false; Activate(); };

        menu.Items.Add(Item("Refresh", () => _ = service.Refresh(account, force: true)));
        menu.Items.Add(Item("Rename…", () => Rename(account)));
        menu.Items.Add(Item(account.Kind.UsesApiKey() ? "Replace API key…" : "Sign in again…", () => _app.ShowSignIn(account.Kind, account)));
        if (account.Kind == ProviderKind.OpenRouter)
            menu.Items.Add(Item("Open OpenRouter credits", () => OAuthFlow.OpenBrowser(new Uri("https://openrouter.ai/settings/credits"))));
        if (account.Kind == ProviderKind.DeepSeek)
            menu.Items.Add(Item("Open DeepSeek top up", () => OAuthFlow.OpenBrowser(Providers.DeepSeekProvider.TopUpPage)));

        var labels = snap?.Lines.Select(l => l.Label).Distinct().ToList() ?? new();
        if (labels.Count > 0)
        {
            var show = new MenuItem { Header = "Show metrics" };
            foreach (var label in labels)
            {
                var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = !service.Settings.IsHidden(account.Id, label), StaysOpenOnClick = true };
                item.Click += (_, _) => { service.Settings.SetHidden(account.Id, label, !item.IsChecked); service.NotifySettingsChanged(); };
                show.Items.Add(item);
            }
            menu.Items.Add(show);
        }

        menu.Items.Add(new Separator());
        var up = Item("Move up", () => service.Move(account, -1));
        up.IsEnabled = service.Accounts.IndexOf(account) > 0;
        menu.Items.Add(up);
        var down = Item("Move down", () => service.Move(account, 1));
        down.IsEnabled = service.Accounts.IndexOf(account) < service.Accounts.Count - 1;
        menu.Items.Add(down);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Remove account…", () => RemoveAccount(account)));
        return menu;
    }

    private static MenuItem Item(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private void Rename(Account account)
    {
        _menuOpen = true;
        var name = InputDialog.Ask(this, "Rename account", $"Name for {account.Label}:", account.DisplayName);
        _menuOpen = false;
        if (name is not null) _app.Service.Rename(account, name);
        Activate();
    }

    private void RemoveAccount(Account account)
    {
        _menuOpen = true;
        var remove = AppDialog.Confirm(this, "Remove account?",
            $"{account.Kind.DisplayName()} · {account.DisplayName}",
            "Remove", "Its sign-in is deleted from this PC.", AppDialog.Tone.Danger, account.Kind);
        _menuOpen = false;
        if (remove) _app.Service.Remove(account);
        Activate();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = _app.Service.RefreshAll(force: true);

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        HidePanel();
        _app.ShowSignIn(null);
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        HidePanel();
        _app.ShowSettings();
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
        menu.Opened += (_, _) => _menuOpen = true;
        menu.Closed += (_, _) => { _menuOpen = false; if (IsVisible) Activate(); };
        menu.Items.Add(Item("Copy usage as text", CopyUsage));
        menu.Items.Add(Item("Open data folder", () => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppPaths.DataDir) { UseShellExecute = true })));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Quit ClaUsage", _app.Quit));
        menu.IsOpen = true;
    }

    private void CopyUsage()
    {
        var now = DateTimeOffset.UtcNow;
        var sb = new System.Text.StringBuilder();
        foreach (var account in _app.Service.Accounts)
        {
            var snap = _app.Service.Snapshot(account.Id);
            sb.AppendLine($"{account.Kind.DisplayName()}{(snap?.Plan is { } plan ? $" ({plan})" : "")} - {account.DisplayName}");
            foreach (var line in snap?.Lines ?? new())
            {
                if (_app.Service.Settings.IsHidden(account.Id, line.Label)) continue;
                var reset = line.ResetsAt is { } r ? $", resets {Format.ResetIn(r, now)}" : "";
                sb.AppendLine($"  {line.Label}: {Format.Value(line)}{reset}");
            }
        }
        try { Clipboard.SetText(sb.ToString()); } catch { /* clipboard busy */ }
    }

    // ---------------------------------------------------------------- placement

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int index, int value);

    private void ApplyToolWindowStyle()
    {
        // WS_EX_TOOLWINDOW keeps the panel out of Alt+Tab.
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowLong(hwnd, -20, GetWindowLong(hwnd, -20) | 0x00000080);
    }

    /// <summary>Puts the panel in the corner next to the notification area, on the monitor under the cursor.</summary>
    private void PlaceNearTray()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var screen = WinForms.Screen.FromPoint(WinForms.Cursor.Position);
        var work = screen.WorkingArea;
        var bounds = screen.Bounds;
        // ActualWidth/Height are current inside SizeChanged; the HWND rect may still be the old size.
        var dpi = VisualTreeHelper.GetDpi(this);
        var width = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX);
        var height = (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY);
        if (width <= 0 || height <= 0)
        {
            if (!GetWindowRect(hwnd, out var rect)) return;
            width = rect.Right - rect.Left;
            height = rect.Bottom - rect.Top;
        }

        int x, y;
        if (work.Top > bounds.Top) { x = work.Right - width; y = work.Top; }                 // taskbar on top
        else if (work.Left > bounds.Left) { x = work.Left; y = work.Bottom - height; }       // taskbar on left
        else if (work.Right < bounds.Right) { x = work.Right - width; y = work.Bottom - height; } // right
        else { x = work.Right - width; y = work.Bottom - height; }                          // bottom

        // The window's own margin holds the shadow, so the visible card sits a little inside the corner.
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010); // NOSIZE | NOZORDER | NOACTIVATE
    }
}
