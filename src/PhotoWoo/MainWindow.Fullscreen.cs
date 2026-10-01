using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace PhotoWoo;

public partial class MainWindow
{
    private DispatcherTimer? _fullscreenIdleTimer;
    private HwndSource? _fullscreenSource;
    private IntPtr _fullscreenEntryMonitor;
    private Rect _fullscreenRestoreBounds;
    private ResizeMode _fullscreenResizeMode;
    private bool _fullscreenTopmost, _fullscreenChromeShown = true, _fullscreenKeyboardInput;
    private double _fullscreenMinWidth, _fullscreenMinHeight;
    private long _fullscreenLastInput;
    private Point? _fullscreenLastPointer;
    private Cursor? _fullscreenPreviousCursor;
    private bool _fullscreenCursorHidden, _fullscreenPreviousForceCursor;

    private FrameworkElement[] FullscreenChrome =>
        [HeaderChrome, ToolbarChrome, StatusChrome, FilmstripBorder, InfoPanel, PrevButton, NextButton];

    private void InitializeFullscreen()
    {
        _fullscreenIdleTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(180)
        };
        _fullscreenIdleTimer.Tick += FullscreenIdleTick;
        PreviewMouseMove += FullscreenMouseMove;
        PreviewMouseDown += FullscreenMouseDown;
        PreviewMouseWheel += (_, _) => { if (_fullScreen) ShowFullscreenChrome(); };
        PreviewKeyDown += (_, e) =>
        {
            if (!_fullScreen) return;
            if (e.Key == Key.Tab) _fullscreenKeyboardInput = true;
            ShowFullscreenChrome();
        };
        Activated += (_, _) =>
        {
            if (!_fullScreen) return;
            Topmost = true; ApplyFullscreenMonitorBounds(); ShowFullscreenChrome();
        };
        Deactivated += (_, _) =>
        {
            if (!_fullScreen) return;
            // Alt+Tab and owned dialogs must remain usable above the fullscreen viewer.
            Topmost = false; ShowFullscreenChrome(immediate: true);
        };
        SourceInitialized += (_, _) =>
        {
            _fullscreenSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _fullscreenSource?.AddHook(FullscreenWindowHook);
        };
        Closed += (_, _) =>
        {
            _fullscreenIdleTimer.Stop(); RestoreFullscreenCursor();
            try { _fullscreenSource?.RemoveHook(FullscreenWindowHook); }
            catch (ObjectDisposedException) { /* WPF may already have destroyed its HWND source. */ }
        };
    }

    private void ToggleFullscreen()
    {
        EndImageDrag(); StopPhotoArrival();
        if (_fullScreen) LeaveFullscreen();
        else EnterFullscreen();
    }

    private void EnterFullscreen()
    {
        _fullscreenEntryMonitor = FullscreenMonitorFromWindow(new WindowInteropHelper(this).Handle, 2);
        _oldState = WindowState; _oldStyle = WindowStyle;
        _fullscreenRestoreBounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        _fullscreenResizeMode = ResizeMode; _fullscreenTopmost = Topmost;
        _fullscreenMinWidth = MinWidth; _fullscreenMinHeight = MinHeight;
        CompleteFullscreenPanelTransitions();
        _fullScreen = true; _fit = true;

        // The image spans the full client area. Chrome keeps its grid slots as overlays,
        // so showing/hiding a panel cannot change the photograph's size or position.
        Grid.SetRow(PhotoStage, 0); Grid.SetRowSpan(PhotoStage, 5); Grid.SetColumnSpan(PhotoStage, 2);
        // Explicit root dimensions also cover unusually small logical monitor sizes at high DPI,
        // where the fixed overlay rows together could otherwise exceed the available height.
        PhotoStage.HorizontalAlignment = HorizontalAlignment.Left; PhotoStage.VerticalAlignment = VerticalAlignment.Top;
        BindingOperations.SetBinding(PhotoStage, WidthProperty, new Binding(nameof(ActualWidth)) { Source = LayoutRoot });
        BindingOperations.SetBinding(PhotoStage, HeightProperty, new Binding(nameof(ActualHeight)) { Source = LayoutRoot });
        WindowState = WindowState.Normal;
        MinWidth = 0; MinHeight = 0;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        Topmost = IsActive;
        ApplyFullscreenMonitorBounds();
        _fullscreenKeyboardInput = false;
        _fullscreenLastPointer = Mouse.GetPosition(this);
        ShowFullscreenChrome(immediate: true);
        _fullscreenIdleTimer?.Start();
        Viewport.Focus();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (_fullScreen) { ApplyFullscreenMonitorBounds(); FitImage(); }
        }));
    }

    private void LeaveFullscreen()
    {
        _fullscreenIdleTimer?.Stop(); RestoreFullscreenCursor();
        _fullScreen = false;
        SetFullscreenChromeVisible(true, immediate: true);
        CompleteFullscreenPanelTransitions();
        Grid.SetRow(PhotoStage, 1); Grid.SetRowSpan(PhotoStage, 1); Grid.SetColumnSpan(PhotoStage, 1);
        BindingOperations.ClearBinding(PhotoStage, WidthProperty); BindingOperations.ClearBinding(PhotoStage, HeightProperty);
        PhotoStage.HorizontalAlignment = HorizontalAlignment.Stretch; PhotoStage.VerticalAlignment = VerticalAlignment.Stretch;

        WindowState = WindowState.Normal;
        WindowStyle = _oldStyle; ResizeMode = _fullscreenResizeMode;
        Topmost = _fullscreenTopmost;
        MinWidth = _fullscreenMinWidth; MinHeight = _fullscreenMinHeight;
        if (!_fullscreenRestoreBounds.IsEmpty)
        {
            Left = _fullscreenRestoreBounds.Left; Top = _fullscreenRestoreBounds.Top;
            Width = _fullscreenRestoreBounds.Width; Height = _fullscreenRestoreBounds.Height;
        }
        WindowState = _oldState;
        _fit = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!_fullScreen) FitImage();
        }));
    }

    private void CompleteFullscreenPanelTransitions()
    {
        foreach (var panel in new[] { InfoPanel, FilmstripBorder })
        {
            _panelTransitions[panel] = _panelTransitions.GetValueOrDefault(panel) + 1;
            panel.BeginAnimation(WidthProperty, null); panel.BeginAnimation(HeightProperty, null);
            panel.BeginAnimation(OpacityProperty, null);
            panel.Opacity = 1; panel.IsHitTestVisible = true;
        }
        InfoPanel.Width = 240; InfoPanel.Visibility = _infoShown ? Visibility.Visible : Visibility.Collapsed;
        FilmstripBorder.Height = 111; FilmstripBorder.Visibility = _settings.Filmstrip ? Visibility.Visible : Visibility.Collapsed;
    }

    // Called after settings are applied; a manually zoomed photograph keeps its current zoom.
    private void RefreshFullscreenSettings()
    {
        if (!_fullScreen) return;
        ShowFullscreenChrome(immediate: true);
        if (_fit) FitImage();
    }

    private void FullscreenMouseMove(object sender, MouseEventArgs e)
    {
        if (!_fullScreen) return;
        var point = e.GetPosition(this);
        if (_fullscreenLastPointer is { } last && (point - last).Length < .75) return;
        _fullscreenLastPointer = point; _fullscreenKeyboardInput = false;
        ShowFullscreenChrome();
    }

    private void FullscreenMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_fullScreen) return;
        _fullscreenKeyboardInput = false; ShowFullscreenChrome();
        if (Viewport.IsMouseOver) Viewport.Focus();
    }

    private void FullscreenIdleTick(object? sender, EventArgs e)
    {
        if (!_fullScreen) { _fullscreenIdleTimer?.Stop(); return; }
        if (!IsActive || !IsEnabled || _busy || _loading || _dragStart is not null || ModelView.IsMouseCaptured
            || Mouse.Captured is not null || Mouse.LeftButton == MouseButtonState.Pressed
            || Mouse.RightButton == MouseButtonState.Pressed || Mouse.MiddleButton == MouseButtonState.Pressed)
        {
            ShowFullscreenChrome(); return;
        }
        if (_fullscreenChromeShown && FullscreenChrome.Any(element => element.IsVisible
            && (element.IsMouseOver || (_fullscreenKeyboardInput && element.IsKeyboardFocusWithin))))
        {
            _fullscreenLastInput = Stopwatch.GetTimestamp(); return;
        }
        double configured = _settings.FullscreenHideDelaySeconds;
        double delay = double.IsFinite(configured) ? Math.Clamp(configured, .5, 30) : 2;
        if (Stopwatch.GetElapsedTime(_fullscreenLastInput).TotalSeconds < delay) return;
        if (_fullscreenChromeShown) SetFullscreenChromeVisible(false);
        if (Viewport.IsMouseOver) HideFullscreenCursor();
    }

    private void ShowFullscreenChrome(bool immediate = false)
    {
        if (!_fullScreen) return;
        _fullscreenLastInput = Stopwatch.GetTimestamp(); RestoreFullscreenCursor();
        if (!_fullscreenChromeShown || immediate) SetFullscreenChromeVisible(true, immediate);
    }

    private void SetFullscreenChromeVisible(bool show, bool immediate = false)
    {
        _fullscreenChromeShown = show;
        bool animate = !immediate && _settings.Animations && SystemParameters.ClientAreaAnimation;
        foreach (var element in FullscreenChrome)
        {
            double from = element.Opacity;
            element.BeginAnimation(OpacityProperty, null);
            element.Opacity = show ? 1 : 0;
            element.IsHitTestVisible = show;
            if (animate)
                element.BeginAnimation(OpacityProperty, new DoubleAnimation(from, show ? 1 : 0, TimeSpan.FromMilliseconds(show ? 120 : 220))
                {
                    FillBehavior = FillBehavior.Stop,
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
        }
    }

    private void HideFullscreenCursor()
    {
        if (_fullscreenCursorHidden) return;
        _fullscreenPreviousCursor = Viewport.Cursor; _fullscreenPreviousForceCursor = Viewport.ForceCursor;
        _fullscreenCursorHidden = true;
        Viewport.Cursor = Cursors.None; Viewport.ForceCursor = true;
    }

    private void RestoreFullscreenCursor()
    {
        if (!_fullscreenCursorHidden) return;
        _fullscreenCursorHidden = false;
        Viewport.Cursor = _fullscreenPreviousCursor; Viewport.ForceCursor = _fullscreenPreviousForceCursor;
    }

    private void ApplyFullscreenMonitorBounds()
    {
        if (!_fullScreen || WindowState == WindowState.Minimized) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        // Capture the entry monitor before restoring a maximized window to its old normal bounds.
        var monitor = _fullscreenEntryMonitor != IntPtr.Zero ? _fullscreenEntryMonitor : FullscreenMonitorFromWindow(hwnd, 2);
        var info = new FullscreenMonitorInfo { Size = Marshal.SizeOf<FullscreenMonitorInfo>() };
        if (!FullscreenGetMonitorInfo(monitor, ref info)) return;
        var bounds = info.Monitor;
        // Use the physical monitor rectangle, not rcWork (which excludes the taskbar).
        FullscreenSetWindowPos(hwnd, IsActive ? new IntPtr(-1) : new IntPtr(-2), bounds.Left, bounds.Top,
            bounds.Right - bounds.Left, bounds.Bottom - bounds.Top, 0x0010 | 0x0020 | 0x0040 | 0x0200);
        _fullscreenEntryMonitor = IntPtr.Zero;
    }

    private IntPtr FullscreenWindowHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_fullScreen && message is 0x007E or 0x02E0) // WM_DISPLAYCHANGE / WM_DPICHANGED
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ApplyFullscreenMonitorBounds));
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FullscreenRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct FullscreenMonitorInfo
    {
        public int Size;
        public FullscreenRect Monitor, Work;
        public uint Flags;
    }
    [DllImport("user32.dll", EntryPoint = "MonitorFromWindow")]
    private static extern IntPtr FullscreenMonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FullscreenGetMonitorInfo(IntPtr monitor, ref FullscreenMonitorInfo info);
    [DllImport("user32.dll", EntryPoint = "SetWindowPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FullscreenSetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
