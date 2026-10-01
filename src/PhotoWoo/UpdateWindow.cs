using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using PhotoWoo.Localization;
using PhotoWoo.Updates;

namespace PhotoWoo;

public sealed class UpdateWindow : Window
{
    private readonly Version _current;
    private readonly UpdateService _service;
    private readonly Func<PreparedUpdate, Window, Task<bool>> _install;
    private readonly Action<UpdateRelease?> _found;
    private readonly Action<PreparedUpdate> _prepared;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operation;
    private UpdateRelease? _release;
    private PreparedUpdate? _package;
    private bool _working, _installing, _closed;
    private readonly TextBlock _heading = new(), _detail = new(), _status = new();
    private readonly TextBlock _notes = new();
    private readonly ProgressBar _progress = new();
    private readonly Button _primary, _secondary, _releaseLink;
    private readonly Border _notesBox;

    public UpdateWindow(Version current, UpdateService service, UpdateRelease? release, PreparedUpdate? package,
        Action<UpdateRelease?> found, Action<PreparedUpdate> prepared, Func<PreparedUpdate, Window, Task<bool>> install)
    {
        _current = current; _service = service; _release = release; _package = package;
        _found = found; _prepared = prepared; _install = install;
        Title = L10n.Text("updates.title"); Width = 630; Height = 490; MinWidth = 540; MinHeight = 430;
        MaxHeight = SystemParameters.WorkArea.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("#1D2022"); Foreground = Brush("#E5E9E8");
        FontFamily = new FontFamily("Segoe UI Variable, Segoe UI"); FontSize = 13; UseLayoutRounding = true;
        SourceInitialized += (_, _) => { int dark = 1; DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int)); };
        Closing += OnClosing;
        Closed += (_, _) => { _closed = true; _lifetime.Cancel(); _operation?.Cancel(); };
        var root = new Grid { Margin = new Thickness(28) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new StackPanel();
        _heading.FontSize = 23; _heading.FontWeight = FontWeights.Light; _heading.TextWrapping = TextWrapping.Wrap;
        _detail.Foreground = Brush("#98A4A0"); _detail.Margin = new Thickness(0, 10, 0, 0); _detail.TextWrapping = TextWrapping.Wrap;
        header.Children.Add(_heading); header.Children.Add(_detail); root.Children.Add(header);
        _notes.TextWrapping = TextWrapping.Wrap; _notes.Foreground = Brush("#BAC5C0"); _notes.LineHeight = 20;
        _notesBox = new Border { Background = Brush("#22282A"), CornerRadius = new CornerRadius(5), Padding = new Thickness(16), Margin = new Thickness(0, 22, 0, 18) };
        var notesLayout = new Grid();
        notesLayout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        notesLayout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        notesLayout.Children.Add(new ScrollViewer { Content = _notes, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        _notesBox.Child = notesLayout;
        Grid.SetRow(_notesBox, 1); root.Children.Add(_notesBox);
        var footer = new StackPanel();
        _status.TextWrapping = TextWrapping.Wrap; _status.Foreground = Brush("#A9C8BD"); _status.Margin = new Thickness(0, 0, 0, 12);
        footer.Children.Add(_status);
        _progress.Height = 3; _progress.Foreground = Brush("#A9C8BD"); _progress.Background = Brush("#343C39");
        _progress.BorderThickness = new Thickness(0); _progress.Margin = new Thickness(0, 0, 0, 16);
        footer.Children.Add(_progress);
        var buttons = new DockPanel { LastChildFill = false };
        _releaseLink = MakeButton("updates.openRelease");
        _releaseLink.HorizontalAlignment = HorizontalAlignment.Left; _releaseLink.Margin = new Thickness(0, 10, 0, 0);
        _releaseLink.Click += (_, _) =>
        {
            if (_release is null) return;
            try { Process.Start(new ProcessStartInfo(_release.ReleasePage.AbsoluteUri) { UseShellExecute = true }); }
            catch { _status.Text = L10n.Text("updates.checkFailed"); }
        };
        Grid.SetRow(_releaseLink, 1); notesLayout.Children.Add(_releaseLink);
        _primary = MakeButton("updates.check", true); DockPanel.SetDock(_primary, Dock.Right);
        _primary.Click += async (_, _) => await PrimaryAsync(); buttons.Children.Add(_primary);
        _secondary = MakeButton("updates.later"); _secondary.Margin = new Thickness(0, 0, 8, 0);
        _secondary.Click += (_, _) => { if (_working) _operation?.Cancel(); else Close(); };
        DockPanel.SetDock(_secondary, Dock.Right); buttons.Children.Add(_secondary);
        footer.Children.Add(buttons); Grid.SetRow(footer, 2); root.Children.Add(footer); Content = root;
        Loaded += async (_, _) => { if (_release is null) await CheckAsync(); else ShowAvailable(); };
    }

    private async Task PrimaryAsync()
    {
        if (_working) return;
        if (_release is null) { await CheckAsync(); return; }
        if (_package is not null)
        {
            _installing = true; SetWorking(true, "updates.installing"); _secondary.IsEnabled = false;
            try
            {
                if (await _install(_package, this)) { _installing = false; Close(); }
                else if (!_closed) ShowAvailable();
            }
            catch (UnauthorizedAccessException) { _status.Text = L10n.Text("updates.unsupported"); }
            catch (UpdateInUseException) { _status.Text = L10n.Text("updates.installBlocked"); }
            catch { _status.Text = L10n.Text("updates.failed"); }
            finally
            {
                _installing = false;
                if (!_closed) { SetWorking(false); _primary.Content = L10n.Text("updates.install"); }
            }
            return;
        }
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operation = operation; SetWorking(true, "updates.downloading");
        _status.Text = L10n.Format("updates.downloading", 0);
        try
        {
            var progress = new Progress<UpdateProgress>(p =>
            {
                if (_closed || operation.IsCancellationRequested) return;
                _progress.IsIndeterminate = p.Phase != "downloading" || p.TotalBytes is null or <= 0;
                if (!_progress.IsIndeterminate) _progress.Value = Math.Clamp(100d * p.BytesReceived / p.TotalBytes!.Value, 0, 100);
                _status.Text = p.Phase switch
                {
                    "downloading" => L10n.Format("updates.downloading", _progress.Value),
                    "verifying" => L10n.Text("updates.verifying"),
                    "extracting" => L10n.Text("updates.extracting"),
                    _ => L10n.Text("updates.ready")
                };
            });
            _package = await _service.PrepareAsync(_release, progress, operation.Token);
            _prepared(_package);
            if (!_closed) ShowAvailable();
        }
        catch (OperationCanceledException) { if (!_closed) _status.Text = L10n.Text("updates.cancelled"); }
        catch { if (!_closed) _status.Text = L10n.Text("updates.failed"); }
        finally
        {
            _operation = null;
            if (!_closed) { SetWorking(false); _primary.Content = L10n.Text(_package is null ? "updates.download" : "updates.install"); }
        }
    }

    private async Task CheckAsync()
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operation = operation; SetWorking(true, "updates.checking");
        _heading.Text = L10n.Text("updates.title"); _detail.Text = L10n.Format("updates.installedVersion", _current.ToString(3));
        _notes.Text = L10n.Text("updates.confirm"); _releaseLink.Visibility = Visibility.Collapsed;
        try
        {
            _release = await _service.CheckAsync(_current, operation.Token);
            if (_package is not null && _package.Release.Version != _release?.Version) _package = null;
            _found(_release);
            if (_closed) return;
            if (_release is not null) ShowAvailable();
            else { _status.Text = L10n.Text("updates.upToDate"); _primary.Content = L10n.Text("updates.check"); }
        }
        catch (OperationCanceledException) { if (!_closed) _status.Text = L10n.Text("updates.cancelled"); }
        catch { if (!_closed) { _status.Text = L10n.Text("updates.checkFailed"); _primary.Content = L10n.Text("updates.retry"); } }
        finally { _operation = null; if (!_closed) SetWorking(false); }
    }

    private void ShowAvailable()
    {
        if (_release is null) return;
        _heading.Text = L10n.Format("updates.available", _release.Version.ToString(3));
        _detail.Text = L10n.Format("updates.releaseInfo", _current.ToString(3), _release.AssetSize / 1048576d);
        _notes.Text = string.IsNullOrWhiteSpace(_release.ReleaseNotes) ? L10n.Text("updates.confirm") : L10n.Text("updates.releaseNotes") + "\n\n" + _release.ReleaseNotes;
        _status.Text = L10n.Text(_package is null ? "updates.confirm" : "updates.ready");
        _primary.Content = L10n.Text(_package is null ? "updates.download" : "updates.install");
        _releaseLink.Visibility = Visibility.Visible; _progress.Visibility = Visibility.Collapsed;
    }

    private void SetWorking(bool value, string? statusKey = null)
    {
        _working = value; _primary.IsEnabled = !value; _secondary.IsEnabled = true;
        _secondary.Content = L10n.Text(value ? "updates.cancel" : "updates.later");
        _progress.Visibility = value ? Visibility.Visible : Visibility.Collapsed; _progress.IsIndeterminate = value;
        if (statusKey is not null) _status.Text = L10n.Text(statusKey);
    }

    private void OnClosing(object? sender, CancelEventArgs e) { if (_installing) e.Cancel = true; }
    private static Brush Brush(string color) => (Brush)new BrushConverter().ConvertFromString(color)!;
    private static Button MakeButton(string key, bool primary = false)
    {
        var button = new Button { Content = L10n.Text(key), Height = 36, Padding = new Thickness(12, 0, 12, 0), Cursor = System.Windows.Input.Cursors.Hand };
        if (Application.Current?.MainWindow?.TryFindResource("QuietButton") is Style style) button.Style = style;
        if (primary) { button.Foreground = Brush("#A9C8BD"); button.BorderBrush = Brush("#42574D"); }
        return button;
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
