using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ROInstances;

public sealed class UpdatesPanel : StackPanel
{
    readonly TextBlock status = LauncherWindow.Text("", 11, "#AAB6CC");
    readonly Button check, install, cancel, access;
    readonly Func<bool> canInstall;
    readonly Action shutdown;
    readonly DispatcherTimer schedule = new() { Interval = TimeSpan.FromHours(6) };
    readonly Settings settings;
    CancellationTokenSource? operation;
    LauncherRelease? release;
    bool downloading;
    static Button ActionButton(string label, Action action)
    {
        var button = new Button { Content = label, Padding = new Thickness(10,5,10,5), Margin = new Thickness(0,7,8,0), Background = new SolidColorBrush(Color.FromRgb(38,54,79)), Foreground = Brushes.White, BorderThickness = new Thickness(0) };
        button.Click += (_, _) => action(); return button;
    }
    public UpdatesPanel(Settings settings, bool preview, Func<bool> canInstall, Action shutdown)
    {
        this.settings = settings; this.canInstall = canInstall; this.shutdown = shutdown;
        Margin = new Thickness(0,12,0,0);
        status.Text = $"Launcher v{Updates.CurrentVersion}";
        Children.Add(status);
        var actions = new WrapPanel();
        check = ActionButton("Check for updates", () => { _ = CheckAsync(); });
        install = ActionButton("Update and restart", () => { _ = InstallAsync(); });
        cancel = ActionButton("Cancel download", () => operation?.Cancel());
        access = ActionButton("GitHub access", ConfigureAccess);
        var notes = ActionButton("Release notes", () => Process.Start(new ProcessStartInfo(Updates.ReleasePage) { UseShellExecute = true }));
        install.Visibility = cancel.Visibility = Visibility.Collapsed;
        foreach (var button in new[] { check, install, cancel, access, notes }) actions.Children.Add(button);
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
        check.Visibility = access.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        install.Visibility = !busy && release != null ? Visibility.Visible : Visibility.Collapsed;
        cancel.Visibility = downloading && busy ? Visibility.Visible : Visibility.Collapsed;
    }
    async Task CheckAsync()
    {
        if (operation != null) return;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30)); operation = cancellation;
        SetBusy(true); status.Text = $"Launcher v{Updates.CurrentVersion} · Checking for updates…";
        try
        {
            release = await Updates.CheckAsync(cancellation.Token);
            status.Text = release == null ? $"Launcher v{Updates.CurrentVersion} · You’re up to date." : $"Launcher v{Updates.CurrentVersion} · Version {release.Version} is available. Game windows stay open during the update.";
        }
        catch (OperationCanceledException) { status.Text = "Update check timed out. Try again when you’re connected."; }
        catch (Exception e) { status.Text = "Could not check for updates. " + e.Message; }
        finally { operation = null; SetBusy(false); }
    }
    async Task InstallAsync()
    {
        if (operation != null || release == null) return;
        if (!canInstall()) { status.Text = "Wait for the current game launch to finish, then update the launcher."; return; }
        if (MessageBox.Show(Window.GetWindow(this), $"Install launcher v{release.Version} and restart the launcher?\n\nYour saved profiles and running Eternal Love game windows will be preserved.", Branding.AppName, MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes) return;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10)); operation = cancellation;
        downloading = true; SetBusy(true);
        try
        {
            var download = await Updates.DownloadAsync(release, new Progress<int>(percent => status.Text = $"Downloading launcher v{release.Version}… {percent}%"), cancellation.Token);
            if (!canInstall()) { status.Text = "Download verified. Finish launching games, then choose Update and restart again."; return; }
            downloading = false; SetBusy(true); status.Text = "Update verified. Preparing to restart the launcher…";
            await Updates.PrepareInstallAsync(download.Path, download.Hash);
            shutdown();
        }
        catch (OperationCanceledException) { status.Text = "Download cancelled. Your launcher is unchanged."; }
        catch (Exception e) { status.Text = "Update could not be installed. " + e.Message; }
        finally { operation = null; downloading = false; SetBusy(false); }
    }
    void ConfigureAccess()
    {
        var dialog = new Window { Owner = Window.GetWindow(this), Title = "GitHub access for launcher updates", Icon = Branding.Icon, Width = 540, Height = 330, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = new SolidColorBrush(Color.FromRgb(13,22,38)) };
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(LauncherWindow.Text("This release repository is private. Use your existing GitHub CLI sign-in, or save a fine-grained token with Contents: Read access to yajra/ROInstanceLauncher.", 13));
        var password = new PasswordBox { Margin = new Thickness(0,16,0,8), Padding = new Thickness(8), MaxLength = 1000 };
        System.Windows.Automation.AutomationProperties.SetName(password, "GitHub read-only access token"); panel.Children.Add(password);
        panel.Children.Add(LauncherWindow.Text("Saved in Windows Credential Manager for your Windows account. Never included in profiles or releases.", 11));
        var actions = new WrapPanel { Margin = new Thickness(0,12,0,0) };
        actions.Children.Add(ActionButton("Save token", () => { try { UpdateCredentials.Save(password.Password); password.Clear(); dialog.DialogResult = true; } catch (Exception e) { MessageBox.Show(dialog, e.Message); } }));
        actions.Children.Add(ActionButton("Remove saved token", () => { try { UpdateCredentials.Remove(); dialog.DialogResult = true; } catch (Exception e) { MessageBox.Show(dialog, e.Message); } }));
        actions.Children.Add(ActionButton("Cancel", dialog.Close)); panel.Children.Add(actions); dialog.Content = panel;
        if (dialog.ShowDialog() == true) _ = CheckAsync();
    }
}
