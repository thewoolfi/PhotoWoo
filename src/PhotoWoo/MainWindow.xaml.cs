using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using PhotoWoo.Imaging;
using PhotoWoo.Interactions;
using PhotoWoo.Localization;

namespace PhotoWoo;

public partial class MainWindow : Window
{
    private readonly ImageService _images = new();
    private readonly ObservableCollection<ThumbnailItem> _thumbnails = [];
    private readonly Dictionary<string, LoadedImage> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _cacheOrder = new();
    private readonly HashSet<int> _thumbnailIndices = [];
    private List<string> _paths = [];
    private string? _path;
    private LoadedImage? _loaded;
    private CancellationTokenSource _loadCts = new(), _backgroundCts = new(), _folderCts = new();
    private int _index, _turns, _request;
    private long _cacheBytes;
    private bool _fit = true, _syncSelection, _busy, _loading, _allowClose, _fullScreen;
    private double _zoom = 1;
    private Point? _dragStart;
    private Vector _pan, _dragPan;
    private bool _dragNavigating;
    private WindowState _oldState;
    private WindowStyle _oldStyle;
    private ViewerSettings _settings;
    private static readonly string SettingsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoWoo", "settings.json");

    public MainWindow(string? startupPath = null)
    {
        _settings = ReadSettings();
        MainTranslations.Register();
        UpdateTranslations.Register();
        L10n.SetLanguage(_settings.Language);
        MotionPreferences.AnimationsEnabled = _settings.Animations;
        MotionPreferences.InertiaEnabled = _settings.Inertia;
        InitializeComponent();
        PhotoImage.RenderTransform = new TransformGroup { Children = [_photoRotation, _photoOffset, _arrivalOffset] };
        Deactivated += (_, _) => { EndImageDrag(); StopPhotoArrival(); };
        PreviewMouseDown += (_, _) => { if (_motionKind == 1) StopImageMotion(); };
        FilmstripBorder.Visibility = _settings.Filmstrip ? Visibility.Visible : Visibility.Collapsed;
        ThumbnailsList.ItemsSource = _thumbnails;
        InitializeFullscreen();
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            int enabled = 1; DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int));
        };
        Loaded += async (_, _) =>
        {
            UpdateControls();
            InitializeUpdates();
            if (!string.IsNullOrWhiteSpace(startupPath) && File.Exists(startupPath)) await OpenPathsAsync([Path.GetFullPath(startupPath)]);
        };
    }

    private static ViewerSettings ReadSettings()
    {
        try { return JsonSerializer.Deserialize<ViewerSettings>(File.ReadAllText(SettingsFile)) ?? new(); }
        catch { return new(); }
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dialog = new OpenFileDialog { Title = L10n.Text("main.open"), Filter = ImageService.OpenFilter.Replace("Изображения и RAW", L10n.Text("main.formats")).Replace("Все файлы", L10n.Text("main.allFiles")), Multiselect = true };
        if (dialog.ShowDialog(this) == true && await ConfirmChangesAsync()) await OpenPathsAsync(dialog.FileNames);
    }

    private async Task OpenPathsAsync(IEnumerable<string> selected)
    {
        var paths = selected.Where(File.Exists).Select(Path.GetFullPath).Where(ImageService.IsSupported).ToList();
        if (paths.Count == 0) { ShowError(L10n.Text("main.unsupported")); return; }
        _folderCts.Cancel(); _folderCts.Dispose(); _folderCts = new();
        var token = _folderCts.Token;
        _paths = paths; _index = 0; _incomingDirection = 0; RebuildThumbnails();
        // Display the requested image before enumerating a potentially large directory.
        await LoadCurrentAsync();
        if (paths.Count != 1 || token.IsCancellationRequested) return;
        var first = paths[0];
        try
        {
            var siblings = await Task.Run(() =>
            {
                var result = new List<string>();
                foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(first)!))
                {
                    token.ThrowIfCancellationRequested();
                    if (ImageService.IsSupported(file)) result.Add(file);
                }
                result.Sort((a, b) => StrCmpLogicalW(Path.GetFileName(a), Path.GetFileName(b)));
                return result;
            }, token);
            if (token.IsCancellationRequested || _path != first) return;
            var found = siblings.FindIndex(p => string.Equals(p, first, StringComparison.OrdinalIgnoreCase));
            if (found >= 0) { _paths = siblings; _index = found; RebuildThumbnails(); UpdateControls(); _ = WarmNeighborsAsync(_backgroundCts.Token); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText.Text = L10n.Format("main.folderUnavailable", ex.Message); }
    }

    private void RebuildThumbnails()
    {
        ResetFilmstripThumbnails();
        _syncSelection = true;
        _thumbnailIndices.Clear();
        _thumbnails.Clear(); foreach (var file in _paths) _thumbnails.Add(new(file));
        ThumbnailsList.SelectedIndex = _index; _syncSelection = false;
        RequestFilmstripThumbnails();
    }

    private string CacheKey(string path)
    {
        var file = new FileInfo(path);
        return $"{path}|{file.LastWriteTimeUtc.Ticks}|{file.Length}";
    }

    private void AddCache(string key, LoadedImage image)
    {
        if (_cache.ContainsKey(key)) return;
        long bytes = (long)image.Bitmap.PixelWidth * image.Bitmap.PixelHeight * 4;
        if (bytes > 96L * 1024 * 1024) return;
        while (_cacheBytes + bytes > 96L * 1024 * 1024 && _cacheOrder.TryDequeue(out var old))
            if (_cache.Remove(old, out var removed)) _cacheBytes -= (long)removed.Bitmap.PixelWidth * removed.Bitmap.PixelHeight * 4;
        _cache[key] = image; _cacheOrder.Enqueue(key); _cacheBytes += bytes;
    }

    private async Task LoadCurrentAsync(bool full = false)
    {
        if (_paths.Count == 0) return;
        EndImageDrag();
        StopPhotoArrival();
        _loadCts.Cancel(); _loadCts.Dispose(); _loadCts = new();
        _backgroundCts.Cancel(); _backgroundCts.Dispose(); _backgroundCts = new();
        var token = _loadCts.Token; var request = ++_request; var path = _paths[_index];
        bool same = string.Equals(_path, path, StringComparison.OrdinalIgnoreCase);
        int arrivalDirection = _incomingDirection; _incomingDirection = 0;
        if (!same) { _turns = 0; _loaded = null; PhotoImage.Source = null; }
        _path = path; _loading = true;
        EmptyState.Visibility = Visibility.Collapsed;
        LoadingText.Text = L10n.Text(full ? "main.fullLoading" : "main.opening");
        StatusOverlay.Visibility = Visibility.Visible;
        UpdateControls();
        var clock = Stopwatch.StartNew();
        try
        {
            LoadedImage result;
            var key = CacheKey(path);
            if (!full && _cache.TryGetValue(key, out var cached)) result = cached;
            else { result = await _images.LoadAsync(path, 2560, full, token); if (!full && !token.IsCancellationRequested) AddCache(key, result); }
            if (token.IsCancellationRequested || request != _request) return;
            _loaded = result; PhotoImage.Source = result.Bitmap;
            _fit = true; _pan = default; FitImage();
            if (!same) AnimatePhotoArrival(arrivalDirection);
            StatusText.Text = L10n.Format("main.timing", L10n.Text(result.IsPreview ? "main.preview" : "main.image"), clock.Elapsed.TotalMilliseconds);
            UpdateControls();
            _ = WarmNeighborsAsync(_backgroundCts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (request != _request) return;
            StatusText.Text = L10n.Text("main.openFailed");
            ShowError($"{Path.GetFileName(path)}\n\n{ex.Message}");
        }
        finally { if (request == _request) { _loading = false; StatusOverlay.Visibility = Visibility.Collapsed; UpdateControls(); } }
    }

    private async Task WarmNeighborsAsync(CancellationToken token)
    {
        var snapshot = _paths.ToArray(); var selected = _index;
        RequestFilmstripThumbnails();
        try
        {
            foreach (var i in new[] { selected + 1, selected - 1 })
            {
                token.ThrowIfCancellationRequested();
                if (i < 0 || i >= snapshot.Length) continue;
                try
                {
                    var key = CacheKey(snapshot[i]); if (_cache.ContainsKey(key)) continue;
                    var result = await _images.LoadAsync(snapshot[i], 2560, false, token);
                    if (!token.IsCancellationRequested) AddCache(key, result);
                }
                catch (OperationCanceledException) { throw; }
                catch { }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void UpdateControls()
    {
        var hasImage = _loaded is not null;
        FileNameText.Text = _path is null ? L10n.Text("main.viewer") : Path.GetFileName(_path);
        Title = _path is null ? "PhotoWoo" : $"{Path.GetFileName(_path)} — PhotoWoo";
        DirtyText.Visibility = _turns != 0 ? Visibility.Visible : Visibility.Collapsed;
        SaveButton.IsEnabled = hasImage && _turns != 0 && !_busy && !_loading;
        SaveCopyButton.IsEnabled = hasImage && !_busy && !_loading;
        UndoButton.IsEnabled = hasImage && _turns != 0 && !_busy;
        PrintButton.IsEnabled = hasImage && !_busy && !_loading;
        PrevButton.IsEnabled = _index > 0 && !_busy;
        NextButton.IsEnabled = _index + 1 < _paths.Count && !_busy;
        OpenButton.IsEnabled = !_busy;
        FullQualityButton.IsEnabled = hasImage && !_busy && !_loading && (_loaded!.IsPreview || _loaded.Bitmap.PixelWidth < _loaded.SourceWidth || _loaded.Bitmap.PixelHeight < _loaded.SourceHeight);
        PositionText.Text = _paths.Count == 0 ? "" : $"{_index + 1} / {_paths.Count}";
        if (hasImage)
        {
            var image = _loaded!; var w = image.SourceWidth; var h = image.SourceHeight;
            if (_turns % 2 != 0) (w, h) = (h, w);
            DetailsText.Text = $"{image.Format.ToUpperInvariant()}    {w:N0} × {h:N0}";
            InfoNameText.Text = Path.GetFileName(_path);
            string fileSize;
            try { fileSize = L10n.Format("main.fileSize", new FileInfo(_path!).Length / 1048576d); } catch { fileSize = L10n.Text("main.sourceMissing"); }
            var previewNote = L10n.Text(ImageService.IsRaw(_path!) && image.IsPreview ? "main.rawPreview" : image.IsPreview ? "main.fastPreview" : "main.fullQuality");
            InfoDetailsText.Text = L10n.Format("main.infoDetails", image.Format.ToUpperInvariant(), w, h, fileSize, _turns * 90, previewNote);
        }
        else { DetailsText.Text = "JPEG · PNG · TIFF · WebP · HEIC · RAW"; InfoNameText.Text = L10n.Text("main.noImage"); InfoDetailsText.Text = L10n.Text("main.noInfo"); }
        _syncSelection = true; ThumbnailsList.SelectedIndex = _index; _syncSelection = false;
        if (_index < _thumbnails.Count && _index >= 0 && FilmstripBorder.Visibility == Visibility.Visible) ThumbnailsList.ScrollIntoView(_thumbnails[_index]);
        if (_index < _thumbnails.Count && _index >= 0) _thumbnails[_index].Rotation = _turns * 90;
    }

    private void FitImage(bool animate = false)
    {
        if (_loaded is null || Viewport.ActualWidth <= 0 || Viewport.ActualHeight <= 0) return;
        var w = (double)_loaded.Bitmap.PixelWidth; var h = (double)_loaded.Bitmap.PixelHeight;
        if (_turns % 2 != 0) (w, h) = (h, w);
        var targetZoom = _fullScreen
            ? (_settings.FullscreenFill ? Math.Max(Viewport.ActualWidth / w, Viewport.ActualHeight / h)
                : Math.Min(Viewport.ActualWidth / w, Viewport.ActualHeight / h))
            : Math.Min(1, Math.Min(Math.Max(1, Viewport.ActualWidth - 100) / w, Math.Max(1, Viewport.ActualHeight - 40) / h));
        if (animate) AnimateView(targetZoom, default);
        else { StopImageMotion(); _zoom = targetZoom; _pan = default; RenderPhoto(); }
    }

    private void RenderPhoto()
    {
        if (_loaded is null) return;
        PhotoImage.Width = _loaded.Bitmap.PixelWidth * _zoom; PhotoImage.Height = _loaded.Bitmap.PixelHeight * _zoom;
        // Canvas keeps the full bitmap unclipped until the viewport clips the final transform.
        Canvas.SetLeft(PhotoImage, (Viewport.ActualWidth - PhotoImage.Width) / 2);
        Canvas.SetTop(PhotoImage, (Viewport.ActualHeight - PhotoImage.Height) / 2);
        _photoRotation.Angle = _turns * 90;
        _photoOffset.X = _pan.X; _photoOffset.Y = _pan.Y;
        ZoomText.Text = $"{_zoom * 100:N0}%";
    }

    private async Task MoveAsync(int next)
    {
        if (_busy || next < 0 || next >= _paths.Count || next == _index) return;
        var target = _paths[next];
        if (!await ConfirmChangesAsync()) { UpdateControls(); return; }
        _incomingDirection = Math.Sign(next - _index);
        _index = _paths.FindIndex(p => string.Equals(p, target, StringComparison.OrdinalIgnoreCase));
        if (_index >= 0) await LoadCurrentAsync();
    }

    private async void Prev_Click(object sender, RoutedEventArgs e) => await MoveAsync(_index - 1);
    private async void Next_Click(object sender, RoutedEventArgs e) => await MoveAsync(_index + 1);
    private async void Thumbnails_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncSelection || ThumbnailsList.SelectedIndex < 0) return;
        var next = ThumbnailsList.SelectedIndex;
        await MoveAsync(next); UpdateControls();
    }
    private void Rotate(int direction)
    {
        if (_busy || _loaded is null) return;
        double fromAngle = _photoRotation.Angle;
        EndImageDrag();
        _turns = ((_turns + direction) % 4 + 4) % 4; _fit = true; FitImage(animate: true); UpdateControls();
        AnimateRotation(fromAngle, direction);
    }
    private void RotateLeft_Click(object sender, RoutedEventArgs e) => Rotate(-1);
    private void RotateRight_Click(object sender, RoutedEventArgs e) => Rotate(1);
    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        double fromAngle = _photoRotation.Angle;
        EndImageDrag(); _turns = 0; _fit = true; FitImage(animate: true); UpdateControls();
        AnimateRotation(fromAngle);
    }
    private void Fit_Click(object sender, RoutedEventArgs e) { EndImageDrag(); _fit = true; FitImage(animate: true); }
    private void ZoomBy(double factor)
    {
        if (_loaded is null) return;
        // Repeated wheel ticks build on the destination, while starting from the visible scale.
        double previousTarget = _motionKind == 2 ? _motionToZoom : _zoom;
        EndImageDrag(); _fit = false;
        double target = Math.Clamp(previousTarget * factor, .01, 8);
        AnimateView(target, ClampPan(_pan, target));
    }
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ZoomBy(1 / 1.25);
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ZoomBy(1.25);
    private void Viewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        EndImageDrag();
        if (_fit) FitImage(); else { _pan = ClampPan(_pan, _zoom); RenderPhoto(); }
    }
    private void Viewport_MouseWheel(object sender, MouseWheelEventArgs e) { ZoomBy(e.Delta > 0 ? 1.12 : 1 / 1.12); e.Handled = true; }
    private void Viewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_loaded is null || _busy || _loading) return;
        EndImageDrag();
        StopPhotoArrival();
        if (e.ClickCount == 2) { _fit = !_fit; if (_fit) FitImage(animate: true); else AnimateView(1, default); e.Handled = true; return; }
        _dragNavigating = _fit;
        _dragStart = e.GetPosition(Viewport); _dragPan = _pan;
        _pointerX.Reset(_dragStart.Value.X); _pointerY.Reset(_dragStart.Value.Y);
        Viewport.CaptureMouse(); Viewport.Cursor = _dragNavigating ? Cursors.ScrollWE : Cursors.SizeAll;
        e.Handled = true;
    }
    private void Viewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is null) return;
        if (e.LeftButton != MouseButtonState.Pressed) { EndImageDrag(); return; }
        var delta = e.GetPosition(Viewport) - _dragStart.Value;
        var point = e.GetPosition(Viewport);
        _pointerX.Add(point.X); _pointerY.Add(point.Y);
        _pan = _dragNavigating ? new Vector(Math.Clamp(delta.X * .65, -220, 220), 0) : ClampPan(_dragPan + delta, _zoom);
        RenderPhoto(); e.Handled = true;
    }
    private async void Viewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragStart is null) return;
        var release = e.GetPosition(Viewport);
        var delta = release - _dragStart.Value;
        _pointerX.Add(release.X); _pointerY.Add(release.Y);
        bool navigating = _dragNavigating;
        var step = _dragNavigating ? ViewerGestures.NavigationStep(delta.X, delta.Y) : 0;
        var velocity = new Vector(_pointerX.Velocity, _pointerY.Velocity);
        EndImageDrag(animateReturn: navigating && step == 0); e.Handled = true;
        if (!navigating) StartPanInertia(velocity);
        if (step != 0) await MoveAsync(_index + step);
    }
    private void Viewport_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_dragStart is not null) EndImageDrag();
    }
    private void EndImageDrag(bool animateReturn = false)
    {
        StopImageMotion();
        _photoRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        if (_dragStart is null) return;
        var reset = _dragNavigating;
        _dragStart = null; _dragNavigating = false;
        if (Viewport.IsMouseCaptured) Viewport.ReleaseMouseCapture();
        Viewport.Cursor = Cursors.Arrow;
        if (reset)
        {
            if (animateReturn) AnimateView(_zoom, _dragPan, allowOverscroll: true);
            else { _pan = _dragPan; RenderPhoto(); }
        }
    }
    private void Filmstrip_Click(object sender, RoutedEventArgs e)
    {
        _settings.Filmstrip = !_settings.Filmstrip;
        AnimatePanel(FilmstripBorder, _settings.Filmstrip, HeightProperty, 111);
        FilmstripButton.Foreground = new SolidColorBrush(_settings.Filmstrip ? Color.FromRgb(169, 200, 189) : Color.FromRgb(213, 218, 219));
        if (_settings.Filmstrip) _ = WarmNeighborsAsync(_backgroundCts.Token);
    }
    private void Info_Click(object sender, RoutedEventArgs e) => ShowInformation(!_infoShown);
    private void Settings_Click(object sender, RoutedEventArgs e) => ShowSettings();
    private async void FullQuality_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _loading || _loaded is null) return;
        // Keep the current user rotation while replacing the preview with full-resolution data.
        await LoadCurrentAsync(true);
    }

    private async Task<bool> SaveAsync(bool copy, Window? owner = null)
    {
        if (_busy || _loading || _path is null || _loaded is null) return false;
        EndImageDrag();
        if (!copy && _turns == 0) return true;
        var source = _path; var destination = source;
        var extension = Path.GetExtension(source).ToLowerInvariant();
        bool canOverwrite = !ImageService.IsRaw(source) && new[] { ".jpg", ".jpeg", ".png", ".tif", ".tiff", ".webp" }.Contains(extension);
        if (copy || !canOverwrite)
        {
            var dialog = new SaveFileDialog
            {
                Title = L10n.Text(ImageService.IsRaw(source) ? "main.rawExport" : "main.saveCopyTitle"),
                FileName = Path.GetFileNameWithoutExtension(source) + L10n.Text("main.copyName"),
                InitialDirectory = Path.GetDirectoryName(source),
                Filter = "JPEG (*.jpg)|*.jpg|PNG (*.png)|*.png|TIFF (*.tif)|*.tif|WebP (*.webp)|*.webp",
                FilterIndex = extension == ".png" ? 2 : ImageService.IsRaw(source) ? 3 : 1,
                OverwritePrompt = true, AddExtension = true
            };
            if (dialog.ShowDialog(owner ?? this) != true) return false;
            destination = dialog.FileName;
            if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase) && (!canOverwrite || copy)) { ShowError(L10n.Text("main.differentName"), owner); return false; }
        }
        _busy = true; _backgroundCts.Cancel(); UpdateControls(); StatusText.Text = L10n.Text("main.saving");
        try
        {
            await _images.SaveRotatedAsync(source, destination, _turns, CancellationToken.None);
            _cache.Clear(); _cacheOrder.Clear(); _cacheBytes = 0;
            _turns = 0;
            if (!string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            {
                var existing = _paths.FindIndex(p => string.Equals(p, destination, StringComparison.OrdinalIgnoreCase));
                if (existing < 0) { _paths.Insert(_index + 1, destination); _index++; } else _index = existing;
            }
            RebuildThumbnails();
            await LoadCurrentAsync(); StatusText.Text = L10n.Format("main.saved", Path.GetFileName(destination)); return true;
        }
        catch (Exception ex) { ShowError(L10n.Format("main.saveFailed", ex.Message), owner); return false; }
        finally { _busy = false; UpdateControls(); }
    }
    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveAsync(false);
    private async void SaveCopy_Click(object sender, RoutedEventArgs e) => await SaveAsync(true);
    private async Task<bool> ConfirmChangesAsync(Window? owner = null)
    {
        if (_turns == 0) return true;
        var choice = MessageBox.Show(owner ?? this, L10n.Text("main.saveQuestion"), L10n.Text("main.unsavedTitle"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (choice == MessageBoxResult.Cancel) return false;
        if (choice == MessageBoxResult.Yes) return await SaveAsync(false, owner);
        _turns = 0; _fit = true; FitImage(); UpdateControls(); return true;
    }

    private async void Print_Click(object sender, RoutedEventArgs e)
    {
        if (_loaded is null || _path is null || _busy || _loading) return;
        EndImageDrag();
        _busy = true; _backgroundCts.Cancel(); UpdateControls(); StatusText.Text = L10n.Text("main.preparingPrint");
        try
        {
            var full = await _images.LoadAsync(_path, 2560, true, CancellationToken.None);
            BitmapSource printable = full.Bitmap;
            if (_turns != 0) { var rotated = new TransformedBitmap(printable, new RotateTransform(_turns * 90)); rotated.Freeze(); printable = rotated; }
            new PrintPreviewWindow(printable, Path.GetFileName(_path)) { Owner = this }.ShowDialog();
            StatusText.Text = L10n.Text("main.ready");
        }
        catch (Exception ex) { ShowError(L10n.Format("main.printFailed", ex.Message)); }
        finally { _busy = false; UpdateControls(); }
    }
    private void Full_Click(object sender, RoutedEventArgs e)
    {
        ToggleFullscreen();
    }
    private async void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;
        if (e.Key is not (Key.OemPlus or Key.Add or Key.OemMinus or Key.Subtract)) StopImageMotion();
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            if (e.Key == Key.O) { Open_Click(this, e); e.Handled = true; }
            if (e.Key == Key.S) { await SaveAsync((Keyboard.Modifiers & ModifierKeys.Shift) != 0); e.Handled = true; }
            if (e.Key == Key.P) { Print_Click(this, e); e.Handled = true; }
            if (e.Key == Key.Z) { Undo_Click(this, e); e.Handled = true; }
            return;
        }
        switch (e.Key)
        {
            case Key.F1: ShowSettings("controls"); break;
            case Key.Left: await MoveAsync(_index - 1); break;
            case Key.Right: await MoveAsync(_index + 1); break;
            case Key.R: Rotate((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1); break;
            case Key.T: Filmstrip_Click(this, e); break;
            case Key.I: Info_Click(this, e); break;
            case Key.F: case Key.F11: Full_Click(this, e); break;
            case Key.D0: case Key.NumPad0: Fit_Click(this, e); break;
            case Key.OemPlus: case Key.Add: ZoomBy(1.25); break;
            case Key.OemMinus: case Key.Subtract: ZoomBy(1 / 1.25); break;
            case Key.Escape: if (_fullScreen) Full_Click(this, e); else { ShowInformation(false); _fit = true; FitImage(animate: true); } break;
            default: return;
        }
        e.Handled = true;
    }
    private void Window_DragOver(object sender, DragEventArgs e) { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (_busy || !await ConfirmChangesAsync()) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) await OpenPathsAsync(paths);
    }
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_busy) { e.Cancel = true; return; }
        if (!_allowClose && _turns != 0) { e.Cancel = true; if (await ConfirmChangesAsync()) { _allowClose = true; Close(); } return; }
        ResetFilmstripThumbnails(closing: true);
        _loadCts.Cancel(); _backgroundCts.Cancel(); _folderCts.Cancel();
        EndImageDrag(); StopPhotoArrival();
        try { Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!); File.WriteAllText(SettingsFile, JsonSerializer.Serialize(_settings)); } catch { }
    }
    private void ShowError(string message, Window? owner = null) => MessageBox.Show(owner ?? this, message, "PhotoWoo", MessageBoxButton.OK, MessageBoxImage.Information);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] private static extern int StrCmpLogicalW(string a, string b);
}

public sealed class ThumbnailItem(string path) : INotifyPropertyChanged
{
    private BitmapSource? _thumb;
    private int _rotation;
    public string Path { get; } = path;
    public string Name => System.IO.Path.GetFileName(Path);
    public BitmapSource? Thumb { get => _thumb; set { _thumb = value; PropertyChanged?.Invoke(this, new(nameof(Thumb))); } }
    public int Rotation { get => _rotation; set { _rotation = value; PropertyChanged?.Invoke(this, new(nameof(Rotation))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}
