using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using PhotoWoo.Localization;
using PhotoWoo.Updates;

namespace PhotoWoo;

public partial class MainWindow
{
    private readonly UpdateService _updates = new();
    private readonly CancellationTokenSource _updatesLifetime = new();
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromMinutes(30) };
    private UpdateWindow? _updateWindow;
    private UpdateRelease? _availableUpdate;
    private PreparedUpdate? _preparedUpdate;
    private bool _checkingAutomatically, _installStarted;
    private static Version ApplicationVersion
    {
        get
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            return new Version(version.Major, version.Minor, Math.Max(0, version.Build));
        }
    }

    private void InitializeUpdates()
    {
        _updateTimer.Tick += async (_, _) => await CheckAutomaticallyAsync();
        Closed += (_, _) =>
        {
            _updateTimer.Stop(); _updatesLifetime.Cancel();
            if (!_installStarted && _preparedUpdate is not null) _updates.CleanupPrepared(_preparedUpdate);
            _updates.Dispose();
        };
        RefreshUpdateSchedule();
        _ = StartupUpdateCheckAsync();
    }

    private async Task StartupUpdateCheckAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), _updatesLifetime.Token);
            var result = UpdateInstaller.ReadAndClearResult(AppContext.BaseDirectory);
            if (result is not null)
            {
                StatusText.Text = result.Success ? L10n.Format("updates.success", result.Version ?? ApplicationVersion.ToString(3)) : L10n.Text("updates.failed");
                await Task.Run(() => UpdateInstaller.CleanupCompletedStaging(result));
            }
            await CheckAutomaticallyAsync();
        }
        catch (OperationCanceledException) { }
        catch { /* An invalid or inaccessible receipt must not prevent startup. */ }
    }

    private void RefreshUpdateSchedule()
    {
        if (_settings.CheckUpdatesAutomatically) _updateTimer.Start(); else _updateTimer.Stop();
    }

    private async Task CheckAutomaticallyAsync()
    {
        if (!_settings.CheckUpdatesAutomatically || _checkingAutomatically || _updateWindow is not null || _updatesLifetime.IsCancellationRequested) return;
        _checkingAutomatically = true;
        try
        {
            var release = await _updates.CheckAsync(ApplicationVersion, _updatesLifetime.Token);
            if (_settings.CheckUpdatesAutomatically && _updateWindow is null && !_updatesLifetime.IsCancellationRequested) SetAvailableUpdate(release);
        }
        catch { /* Background network errors must not interrupt image viewing. Manual checks show errors. */ }
        finally { _checkingAutomatically = false; }
    }

    private void SetAvailableUpdate(UpdateRelease? release)
    {
        if (_updatesLifetime.IsCancellationRequested || _installStarted) return;
        if (_preparedUpdate is not null && release?.Version != _preparedUpdate.Release.Version)
        {
            _updates.CleanupPrepared(_preparedUpdate); _preparedUpdate = null;
        }
        _availableUpdate = release;
        UpdateButton.Visibility = release is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateButton.ToolTip = release is null ? L10n.Text("updates.title") : L10n.Format("updates.notice", release.Version.ToString(3));
    }

    private void Update_Click(object sender, RoutedEventArgs e) => ShowUpdates(this, useKnownRelease: true);

    private void ShowUpdates(Window owner, bool useKnownRelease = false)
    {
        if (_updateWindow is not null) { _updateWindow.Activate(); return; }
        EndImageDrag();
        // Keep an already downloaded package available for a later, explicitly confirmed restart.
        var release = useKnownRelease ? _availableUpdate : null;
        _updateWindow = new UpdateWindow(ApplicationVersion, _updates, release, _preparedUpdate,
            SetAvailableUpdate, package =>
            {
                if (_updatesLifetime.IsCancellationRequested) _updates.CleanupPrepared(package);
                else _preparedUpdate = package;
            }, InstallUpdateAsync) { Owner = owner };
        try { _updateWindow.ShowDialog(); }
        finally { _updateWindow = null; }
    }

    private async Task<bool> InstallUpdateAsync(PreparedUpdate package, Window dialog)
    {
        if (_busy || _loading) return false;
        if (!await ConfirmChangesAsync(dialog)) return false;
        SaveViewerSettings();
        _busy = true; UpdateControls();
        try
        {
            string? reopen = _path;
            using var helper = await Task.Run(() => UpdateInstaller.Start(package, AppContext.BaseDirectory, reopen));
            _installStarted = true; _allowClose = true;
            // Close after the modal update dialog has unwound; the helper waits for this process to exit.
            _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(Close));
            return true;
        }
        finally { _busy = false; UpdateControls(); }
    }

    private void SaveViewerSettings()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(_settings));
    }
}
