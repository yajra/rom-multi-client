using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace ROInstances;

public sealed class UpdatesPanel : StackPanel
{
    readonly TextBlock status = LauncherWindow.Text("", 11, "#AAB6CC");
    readonly Button check, install, cancel;
    readonly Func<bool> canInstall;
    readonly Action shutdown;
    readonly DispatcherTimer schedule = new() { Interval = TimeSpan.FromHours(6) };
    readonly Settings settings;
    CancellationTokenSource? operation;
    LauncherRelease? release;
    bool downloading;
    readonly WrapPanel actions = new();
    Window? settingsDialog;
    public void ShowControls(bool visible) => actions.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    public TextBlock CreateSummary()
    {
        var summary = LauncherWindow.Text("", 14, "#BDCAE1");
        summary.SetBinding(TextBlock.TextProperty, new Binding(nameof(TextBlock.Text)) { Source = status });
        summary.ToolTip = "Open Settings to check for updates or install an available update.";
        summary.VerticalAlignment = VerticalAlignment.Top;
        return summary;
    }
    public void ShowSettings(Window owner)
    {
        var dialog = new Window {
            Owner = owner, Title = "Settings", Icon = Branding.Icon, Width = 580,
            SizeToContent = SizeToContent.Height, MaxHeight = SystemParameters.WorkArea.Height - 40,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = LauncherTheme.Surface("#0B152B", "#070F22"), FontFamily = new FontFamily("Segoe UI")
        };
        dialog.Resources.Add(typeof(CheckBox), LauncherTheme.CheckBoxStyle);
        var body = new StackPanel { Margin = new Thickness(28) };
        var heading = LauncherWindow.Text("Settings", 26); heading.FontWeight = FontWeights.Bold;
        body.Children.Add(heading);
        var hint = LauncherWindow.Text("Keep your launcher up to date. Your profiles and running games are preserved during updates.", 14, "#AAB6CC");
        hint.Margin = new Thickness(0,10,0,20); body.Children.Add(hint);
        var summary = CreateSummary(); summary.Margin = new Thickness(0,0,0,12); body.Children.Add(summary);
        Children.Remove(actions); ShowControls(true); body.Children.Add(actions);
        var labels = LauncherWindow.Text("Profile labels identify your game windows. Sign in through the game; remembered login is shared.", 12, "#AAB6CC");
        labels.Margin = new Thickness(0,24,0,20); body.Children.Add(labels);
        var close = ActionButton("Done", dialog.Close, true); close.IsCancel = true; close.IsDefault = true;
        close.HorizontalAlignment = HorizontalAlignment.Right; close.MinWidth = 90; body.Children.Add(close);
        dialog.Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        settingsDialog = dialog;
        try { dialog.ShowDialog(); }
        finally { body.Children.Remove(actions); ShowControls(false); Children.Add(actions); settingsDialog = null; }
    }
    static Button ActionButton(string label, Action action, bool primary = false)
    {
        var button = LauncherWindow.Button(label, action, primary);
        button.FontSize = 13; button.Padding = new Thickness(14,8,14,8); button.Margin = new Thickness(0,7,8,0);
        return button;
    }
    public UpdatesPanel(Settings settings, bool preview, Func<bool> canInstall, Action shutdown, bool compact = false)
    {
        this.settings = settings; this.canInstall = canInstall; this.shutdown = shutdown;
        Margin = new Thickness(0);
        status.Text = $"v{Updates.CurrentVersion}";
        if (compact) status.Visibility = Visibility.Collapsed;
        Children.Add(status);
        ShowControls(!compact);
        check = ActionButton("Check for updates", () => { _ = CheckAsync(); });
        install = ActionButton("Update and restart", () => { _ = InstallAsync(); }, true);
        cancel = ActionButton("Cancel download", () => operation?.Cancel());
        var notes = ActionButton("Release notes", () => Process.Start(new ProcessStartInfo(Updates.ReleasePage) { UseShellExecute = true }));
        install.Visibility = cancel.Visibility = Visibility.Collapsed;
        foreach (var button in new[] { check, install, cancel, notes }) actions.Children.Add(button);
        var automatic = new CheckBox { Content = "Check automatically", IsChecked = settings.CheckForUpdates, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,7,0,0), ToolTip = "Check at startup and every six hours. Installation starts only when you choose Update and restart." };
        automatic.Click += (_, _) => {
            var previous = settings.CheckForUpdates;
            settings.CheckForUpdates = automatic.IsChecked == true;
            try { if (!preview) Store.Save(settings); }
            catch { settings.CheckForUpdates = previous; automatic.IsChecked = previous; status.Text = "Could not save the automatic update preference."; }
        };
        actions.Children.Add(automatic); Children.Add(actions);
        if (!preview)
        {
            Loaded += async (_, _) => { if (settings.CheckForUpdates) await CheckAsync(); schedule.Start(); };
            schedule.Tick += async (_, _) => { if (settings.CheckForUpdates) await CheckAsync(); };
            Unloaded += (_, _) => { schedule.Stop(); operation?.Cancel(); };
        }
    }
    void SetBusy(bool busy)
    {
        check.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        install.Visibility = !busy && release != null ? Visibility.Visible : Visibility.Collapsed;
        cancel.Visibility = downloading && busy ? Visibility.Visible : Visibility.Collapsed;
    }
    async Task CheckAsync()
    {
        if (operation != null) return;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30)); operation = cancellation;
        SetBusy(true); status.Text = $"Launcher v{Updates.CurrentVersion} � Checking for updates.";
        try
        {
            release = await Updates.CheckAsync(cancellation.Token);
            status.Text = release == null ? $"Launcher v{Updates.CurrentVersion} � You're up to date." : $"Launcher v{Updates.CurrentVersion} � Version {release.Version} is available. Game windows stay open during the update.";
        }
        catch (OperationCanceledException) { status.Text = "Update check timed out. Try again when you're connected."; }
        catch (Exception e) { status.Text = "Could not check for updates. " + e.Message; }
        finally { operation = null; SetBusy(false); }
    }
    async Task InstallAsync()
    {
        if (operation != null || release == null) return;
        if (!canInstall()) { status.Text = "Wait for the current game launch to finish, then update the launcher."; return; }
        if (MessageBox.Show(settingsDialog ?? Window.GetWindow(this), $"Install launcher v{release.Version} and restart the launcher?\n\nYour saved profiles and running Eternal Love game windows will be preserved.", Branding.AppName, MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes) return;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10)); operation = cancellation;
        downloading = true; SetBusy(true);
        try
        {
            var download = await Updates.DownloadAsync(release, new Progress<int>(percent => status.Text = $"Downloading launcher v{release.Version}. {percent}%"), cancellation.Token);
            if (!canInstall()) { status.Text = "Download verified. Finish launching games, then choose Update and restart again."; return; }
            downloading = false; SetBusy(true); status.Text = "Update verified. Preparing to restart the launcher.";
            await Updates.PrepareInstallAsync(download.Path, download.Hash);
            shutdown();
        }
        catch (OperationCanceledException) { status.Text = "Download cancelled. Your launcher is unchanged."; }
        catch (Exception e) { status.Text = "Update could not be installed. " + e.Message; }
        finally { operation = null; downloading = false; SetBusy(false); }
    }
}
