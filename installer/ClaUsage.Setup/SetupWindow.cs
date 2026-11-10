using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClaUsage.Setup;

/// <summary>One borderless window with three pages: options, progress, done (or the error).</summary>
public sealed class SetupWindow : Window
{
    private readonly Installer _installer = new();
    private readonly bool _dark = Styles.Dark;
    private readonly ContentControl _page = new();
    private readonly CancellationTokenSource _cts = new();
    private bool _busy;

    public SetupWindow()
    {
        Title = "ClaUsage Setup";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        UseLayoutRounding = true;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 13;
        Icon = AppImage();
        Resources = Styles.Create(_dark);
        SetResourceReference(BackgroundProperty, "BgBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        SourceInitialized += (_, _) => RoundCorners();
        Closing += (_, e) => { if (_busy) e.Cancel = true; };
        Closed += (_, _) => _cts.Cancel();

        var root = new DockPanel { Margin = new Thickness(26, 18, 18, 24) };
        root.Children.Add(Header());
        DockPanel.SetDock(root.Children[0], Dock.Top);
        _page.Margin = new Thickness(0, 0, 8, 0);
        root.Children.Add(_page);
        Content = new Border
        {
            BorderThickness = new Thickness(1),
            BorderBrush = Styles.Brush(_dark ? "#3A3A3E" : "#D8D8DE"),
            Child = root,
        };
        ShowOptions();
    }

    private UIElement Header()
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 18), Background = Brushes.Transparent };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };

        var logo = new Image { Source = AppImage(), Width = 44, Height = 44, Margin = new Thickness(0, 6, 14, 0) };
        RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
        grid.Children.Add(logo);

        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
        titles.Children.Add(new TextBlock { Text = "ClaUsage", FontSize = 20, FontWeight = FontWeights.SemiBold });
        titles.Children.Add(Muted($"Version {Installer.Version} · AI subscription limits in the tray"));
        Grid.SetColumn(titles, 1);
        grid.Children.Add(titles);

        var close = new Button { Style = Look("CloseButton"), VerticalAlignment = VerticalAlignment.Top, ToolTip = "Close" };
        close.Click += (_, _) => Close();
        Grid.SetColumn(close, 2);
        grid.Children.Add(close);
        return grid;
    }

    private void ShowOptions()
    {
        var existing = Installer.Existing;
        var panel = new StackPanel();
        panel.Children.Add(Body(existing is { } e
            ? $"ClaUsage {(e.Version is { } v ? v + " " : "")}is installed. The setup puts version {Installer.Version} in its place. Your accounts and settings are kept."
            : "Your Claude, ChatGPT, OpenRouter and DeepSeek limits, a click away in the tray. Installs for your Windows user only, no administrator rights needed."));

        panel.Children.Add(Label("Install folder", 18));
        var folderRow = new Grid();
        folderRow.ColumnDefinitions.Add(new ColumnDefinition());
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var folderBox = new Border
        {
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(10, 7, 10, 7),
            Child = new TextBlock { Text = _installer.Folder, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = _installer.Folder },
        };
        folderBox.SetResourceReference(Border.BackgroundProperty, "CardBrush");
        folderBox.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        folderBox.BorderThickness = new Thickness(1);
        folderRow.Children.Add(folderBox);
        if (existing is null)
        {
            var change = new Button { Content = "Change…", Style = Look("Button"), Margin = new Thickness(8, 0, 0, 0), MinWidth = 0 };
            change.Click += (_, _) =>
            {
                using var dialog = new System.Windows.Forms.FolderBrowserDialog
                {
                    Description = "Choose where ClaUsage is installed. A ClaUsage folder is created inside.",
                    SelectedPath = Path.GetDirectoryName(_installer.Folder),
                };
                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                _installer.Folder = Path.Combine(dialog.SelectedPath, Installer.AppName);
                ((TextBlock)folderBox.Child).Text = _installer.Folder;
                ((TextBlock)folderBox.Child).ToolTip = _installer.Folder;
            };
            Grid.SetColumn(change, 1);
            folderRow.Children.Add(change);
        }
        panel.Children.Add(folderRow);

        panel.Children.Add(Label("Options", 18));
        panel.Children.Add(Check("Start ClaUsage when I sign in to Windows", _installer.StartWithWindows, v => _installer.StartWithWindows = v));
        panel.Children.Add(Check("Add a desktop shortcut", _installer.DesktopShortcut, v => _installer.DesktopShortcut = v));
        panel.Children.Add(Muted("A Start menu shortcut is always added. Uninstall from Windows Settings › Apps."));

        if (!Installer.HasDesktopRuntime())
            panel.Children.Add(Body("The .NET 10 runtime isn't installed, so the setup downloads the standalone ClaUsage (about 75 MB) from GitHub.", 12));

        var cancel = new Button { Content = "Cancel", Style = Look("Button"), IsCancel = true };
        cancel.Click += (_, _) => Close();
        var install = new Button { Content = existing is null ? "Install" : "Update", Style = Look("PrimaryButton"), IsDefault = true };
        install.Click += (_, _) => _ = Install();
        panel.Children.Add(Buttons(cancel, install));
        _page.Content = panel;
    }

    private async System.Threading.Tasks.Task Install()
    {
        if (Installer.IsRunning(_installer.Folder))
        {
            // The running copy is replaced, so it has to close first.
            var ask = new StackPanel();
            ask.Children.Add(Label("ClaUsage is running", 0));
            ask.Children.Add(Body("It closes now and reopens when the update is done. Nothing you saved is lost."));
            var back = new Button { Content = "Back", Style = Look("Button"), IsCancel = true };
            back.Click += (_, _) => ShowOptions();
            var go = new Button { Content = "Close and update", Style = Look("PrimaryButton"), IsDefault = true };
            var proceed = new System.Threading.Tasks.TaskCompletionSource<bool>();
            go.Click += (_, _) => proceed.TrySetResult(true);
            ask.Children.Add(Buttons(back, go));
            _page.Content = ask;
            await proceed.Task;
        }

        var status = Body("Starting…");
        var bar = new ProgressBar { Style = Look("Progress"), Maximum = 1, Margin = new Thickness(0, 14, 0, 0) };
        var page = new StackPanel { MinHeight = 120 };
        page.Children.Add(Label(Installer.Existing is null ? "Installing" : "Updating", 0));
        page.Children.Add(status);
        page.Children.Add(bar);
        _page.Content = page;

        _busy = true;
        try
        {
            var progress = new Progress<(double Fraction, string Status)>(p => { bar.Value = p.Fraction; status.Text = p.Status; });
            await System.Threading.Tasks.Task.Run(() => _installer.RunAsync(progress, _cts.Token));
            _busy = false;
            ShowDone();
        }
        catch (Exception e)
        {
            _busy = false;
            ShowError(e.Message);
        }
    }

    private void ShowDone()
    {
        var panel = new StackPanel();
        panel.Children.Add(Label("ClaUsage is ready", 0));
        panel.Children.Add(Body("It lives in the tray next to the clock. Click its icon, or press Ctrl+Alt+U, to see your limits. If the icon is hidden, drag it out of the ^ overflow onto the taskbar."));
        var launch = true;
        var spacer = Check("Open ClaUsage now", true, v => launch = v);
        spacer.Margin = new Thickness(0, 16, 0, 0);
        panel.Children.Add(spacer);
        var finish = new Button { Content = "Finish", Style = Look("PrimaryButton"), IsDefault = true };
        finish.Click += (_, _) =>
        {
            if (launch) try { _installer.Launch(); } catch { /* the Start menu shortcut is there */ }
            Close();
        };
        panel.Children.Add(Buttons(finish));
        _page.Content = panel;
    }

    private void ShowError(string message)
    {
        var panel = new StackPanel();
        var title = Label("Setup didn't finish", 0);
        title.SetResourceReference(TextBlock.ForegroundProperty, "CriticalBrush");
        panel.Children.Add(title);
        panel.Children.Add(Body(message));
        panel.Children.Add(Body("Nothing was removed: a version that was installed keeps working.", 8));
        var close = new Button { Content = "Close", Style = Look("Button"), IsCancel = true };
        close.Click += (_, _) => Close();
        var retry = new Button { Content = "Try again", Style = Look("PrimaryButton"), IsDefault = true };
        retry.Click += (_, _) => ShowOptions();
        panel.Children.Add(Buttons(close, retry));
        _page.Content = panel;
    }

    private Style Look(string key) => (Style)Resources[key];

    private TextBlock Label(string text, double top) => new()
    {
        Text = text,
        FontSize = 14,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, top, 0, 8),
    };

    private static TextBlock Body(string text, double top = 0) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        LineHeight = 19,
        Margin = new Thickness(0, top, 0, 0),
    };

    private static TextBlock Muted(string text)
    {
        var block = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        block.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        return block;
    }

    private CheckBox Check(string text, bool value, Action<bool> set)
    {
        var box = new CheckBox { Content = text, IsChecked = value, Style = Look("Check") };
        box.Checked += (_, _) => set(true);
        box.Unchecked += (_, _) => set(false);
        return box;
    }

    private static UIElement Buttons(params Button[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0) };
        foreach (var button in buttons)
        {
            button.Margin = new Thickness(8, 0, 0, 0);
            row.Children.Add(button);
        }
        return row;
    }

    private static BitmapImage AppImage()
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri("pack://application:,,,/Assets/app.png");
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return image;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Windows 11 rounds the borderless window and gives it the system shadow; older Windows keeps square corners.</summary>
    private void RoundCorners()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var round = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int));
            var dark = _dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int));
        }
        catch
        {
            // Pre-Windows 11.
        }
    }
}
