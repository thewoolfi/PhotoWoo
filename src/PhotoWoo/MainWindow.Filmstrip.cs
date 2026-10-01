using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace PhotoWoo;

public partial class MainWindow
{
    private const int FilmstripOverscan = 2, FilmstripCacheLimit = 128;
    private readonly SemaphoreSlim _filmstripDecodeGate = new(1, 1);
    private readonly HashSet<int> _filmstripFailed = [];
    private DispatcherTimer? _filmstripTimer;
    private CancellationTokenSource? _filmstripCts;
    private VirtualizingStackPanel? _filmstripPanel;
    private ScrollViewer? _filmstripViewer;
    private Task? _filmstripTask;
    private int _filmstripVersion, _filmstripFirst = -1, _filmstripLast = -1;
    private bool _filmstripClosed;

    private void Thumbnails_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is not ScrollViewer viewer) return;
        _filmstripViewer = viewer;
        if (e.HorizontalChange != 0 || e.ViewportWidthChange != 0 || e.ExtentWidthChange != 0)
            RequestFilmstripThumbnails();
    }

    private void RequestFilmstripThumbnails()
    {
        if (_filmstripClosed || _thumbnails.Count == 0 || FilmstripBorder.Visibility != Visibility.Visible) return;
        if (_filmstripTimer is null)
        {
            _filmstripTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(65)
            };
            _filmstripTimer.Tick += (_, _) =>
            {
                _filmstripTimer.Stop();
                if (_filmstripClosed || !ThumbnailsList.IsVisible) return;
                // Let the chosen photograph and explicit save/print work take priority.
                if (_loading || _busy) { RequestFilmstripThumbnails(); return; }
                RefreshFilmstripRange();
            };
        }
        // Throttle rather than debounce: long continuous drags still receive thumbnails.
        if (!_filmstripTimer.IsEnabled) _filmstripTimer.Start();
    }

    private void RefreshFilmstripRange()
    {
        _filmstripPanel ??= FindFilmstripVisual<VirtualizingStackPanel>(ThumbnailsList);
        _filmstripViewer ??= FindFilmstripVisual<ScrollViewer>(ThumbnailsList);
        if (_filmstripPanel is null || _filmstripViewer is null) return;

        int first = int.MaxValue, last = -1;
        double viewportWidth = _filmstripViewer.ViewportWidth;
        // Visit only realized containers, never all paths or all data items on a pointer move.
        foreach (UIElement child in _filmstripPanel.Children)
        {
            if (child is not ListBoxItem container || container.ActualWidth <= 0) continue;
            Rect bounds;
            try { bounds = container.TransformToAncestor(_filmstripViewer).TransformBounds(new Rect(container.RenderSize)); }
            catch (InvalidOperationException) { continue; }
            if (bounds.Right <= 0 || bounds.Left >= viewportWidth) continue;
            int index = ThumbnailsList.ItemContainerGenerator.IndexFromContainer(container);
            if (index < 0) continue;
            first = Math.Min(first, index); last = Math.Max(last, index);
        }
        if (last < 0) return;
        int visibleFirst = first, visibleLast = last;
        first = Math.Max(0, first - FilmstripOverscan);
        last = Math.Min(_thumbnails.Count - 1, last + FilmstripOverscan);
        // At 192 px this hard limit is under 19 MiB of 32-bit thumbnail pixels.
        last = Math.Min(last, first + FilmstripCacheLimit - 1);

        if (first == _filmstripFirst && last == _filmstripLast
            && _filmstripCts is { IsCancellationRequested: false } && _filmstripTask is { IsCompleted: false }) return;

        var wanted = new HashSet<int>();
        for (int i = first; i <= last; i++) wanted.Add(i);
        foreach (var old in _thumbnailIndices.Where(i => !wanted.Contains(i)).ToArray())
        {
            if (old >= 0 && old < _thumbnails.Count) _thumbnails[old].Thumb = null;
            _thumbnailIndices.Remove(old);
        }
        _filmstripFailed.RemoveWhere(i => !wanted.Contains(i));

        // Visible items precede the two-item buffer; existing thumbnails are retained while moving.
        var targets = wanted.Where(i => _thumbnails[i].Thumb is null && !_filmstripFailed.Contains(i))
            .OrderBy(i => i >= visibleFirst && i <= visibleLast ? 0 : 1).ThenBy(i => i).ToArray();
        _filmstripCts?.Cancel(); _filmstripCts?.Dispose(); _filmstripCts = null;
        _filmstripFirst = first; _filmstripLast = last;
        int version = ++_filmstripVersion;
        if (targets.Length == 0) return;
        _filmstripCts = CancellationTokenSource.CreateLinkedTokenSource(_folderCts.Token, _loadCts.Token);
        _filmstripTask = LoadFilmstripThumbnailsAsync(targets, version, _filmstripCts.Token);
    }

    private async Task LoadFilmstripThumbnailsAsync(int[] targets, int version, CancellationToken token)
    {
        bool entered = false;
        try
        {
            await _filmstripDecodeGate.WaitAsync(token); entered = true;
            foreach (int index in targets)
            {
                token.ThrowIfCancellationRequested();
                if (_filmstripClosed || version != _filmstripVersion || !ThumbnailsList.IsVisible) return;
                if (_loading || _busy) { RequestFilmstripThumbnails(); return; }
                if (index >= _thumbnails.Count) return;
                var item = _thumbnails[index];
                if (PhotoWoo.Imaging.SupportedFiles.IsModel(item.Path)) continue;
                if (item.Thumb is not null) continue;
                try
                {
                    var loaded = await _images.LoadAsync(item.Path, 192, false, token);
                    token.ThrowIfCancellationRequested();
                    if (version != _filmstripVersion || index >= _thumbnails.Count
                        || !ReferenceEquals(_thumbnails[index], item)) return;
                    item.Thumb = loaded.Bitmap; _thumbnailIndices.Add(index);
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    if (version == _filmstripVersion) _filmstripFailed.Add(index);
                    // One unreadable file must not stall the rest of the visible strip.
                }
            }
        }
        catch (OperationCanceledException) { }
        finally { if (entered) _filmstripDecodeGate.Release(); }
    }

    private void ResetFilmstripThumbnails(bool closing = false)
    {
        _filmstripClosed = closing;
        _filmstripTimer?.Stop();
        _filmstripCts?.Cancel(); _filmstripCts?.Dispose(); _filmstripCts = null;
        ++_filmstripVersion;
        _filmstripFirst = _filmstripLast = -1;
        _filmstripFailed.Clear();
        _filmstripPanel = null; _filmstripViewer = null;
    }

    private static T? FindFilmstripVisual<T>(DependencyObject node) where T : DependencyObject
    {
        if (node is T match) return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var found = FindFilmstripVisual<T>(VisualTreeHelper.GetChild(node, i));
            if (found is not null) return found;
        }
        return null;
    }
}
