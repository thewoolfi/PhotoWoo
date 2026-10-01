using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace PhotoWoo.Interactions;

/// <summary>Horizontal direct manipulation for a filmstrip; a drag never selects a photograph.</summary>
public static class DragScrollBehavior
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(DragScrollBehavior), new PropertyMetadata(false, EnabledChanged));
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(DragState), typeof(DragScrollBehavior));

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);
    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    private static void EnabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not ListBox list) return;
        if (list.GetValue(StateProperty) is DragState previous) previous.Detach();
        list.ClearValue(StateProperty);
        if (!(bool)e.NewValue) return;
        // Pixel offsets preserve VirtualizingStackPanel recycling; CanContentScroll remains enabled.
        list.SetCurrentValue(VirtualizingPanel.ScrollUnitProperty, ScrollUnit.Pixel);
        var state = new DragState(list);
        list.SetValue(StateProperty, state);
        state.Attach();
    }

    private sealed class DragState(ListBox list)
    {
        private const double DragThreshold = 6;
        private readonly PointerVelocityTracker _velocity = new();
        private ScrollViewer? _viewer;
        private ScrollViewer? _inertiaViewer;
        private ScrollViewer? _immediateWheelViewer;
        private Window? _inputWindow;
        private IInputElement? _captureElement;
        private Point _start;
        private double _startOffset, _dragOffset, _inertiaOffset, _inertiaVelocity;
        private double _wheelFrom, _wheelTarget, _immediateWheelOffset;
        private long _lastFrameAt, _wheelStartedAt;
        private object? _pressedItem;
        private bool _pressed, _dragging, _animating, _wheelAnimating;
        private Cursor? _previousCursor;

        public void Attach()
        {
            list.PreviewMouseDown += MouseDown;
            list.PreviewMouseMove += MouseMove;
            list.PreviewMouseUp += MouseUp;
            list.LostMouseCapture += LostCapture;
            list.PreviewMouseWheel += MouseWheel;
            list.PreviewKeyDown += InterruptKey;
            list.IsVisibleChanged += VisibilityChanged;
            list.Unloaded += Unloaded;
        }

        public void Detach()
        {
            Reset();
            list.PreviewMouseDown -= MouseDown;
            list.PreviewMouseMove -= MouseMove;
            list.PreviewMouseUp -= MouseUp;
            list.LostMouseCapture -= LostCapture;
            list.PreviewMouseWheel -= MouseWheel;
            list.PreviewKeyDown -= InterruptKey;
            list.IsVisibleChanged -= VisibilityChanged;
            list.Unloaded -= Unloaded;
        }

        private void MouseDown(object sender, MouseButtonEventArgs e)
        {
            StopInertia();
            ClearImmediateWheel();
            if (e.ChangedButton != MouseButton.Left || e.Handled || e.OriginalSource is not DependencyObject source || IsInteractive(source)
                || Mouse.Captured is not null) return;
            list.ApplyTemplate();
            _viewer = FindScrollViewer(list);
            if (_viewer is null) return;
            _pressedItem = ItemsControl.ContainerFromElement(list, source) is ListBoxItem item
                ? list.ItemContainerGenerator.ItemFromContainer(item) : null;
            if (_pressedItem == DependencyProperty.UnsetValue) _pressedItem = null;
            _start = e.GetPosition(list); _startOffset = _viewer.HorizontalOffset;
            _dragOffset = _startOffset; _velocity.Reset(_start.X);
            _pressed = true;
            // Capturing ListBox itself enables WPF's MouseEnter selection and auto-scroll timer.
            // Capture its viewer instead; events still route through the list, but native
            // selection paths explicitly require Mouse.Captured == list and stay inactive.
            _captureElement = _viewer;
            if (!Mouse.Capture(_captureElement, CaptureMode.Element)) { Reset(); return; }
            // Delay ListBoxItem selection until mouse-up; default mouse-down would change the photo.
            e.Handled = true;
        }

        private void MouseMove(object sender, MouseEventArgs e)
        {
            if (!_pressed || _viewer is null) return;
            if (e.LeftButton != MouseButtonState.Pressed) { Reset(); return; }
            var pointer = e.GetPosition(list);
            _velocity.Add(pointer.X);
            var delta = pointer - _start;
            if (!_dragging && Math.Max(Math.Abs(delta.X), Math.Abs(delta.Y)) >= DragThreshold)
            {
                _dragging = true;
                _previousCursor = list.Cursor;
                list.SetCurrentValue(FrameworkElement.CursorProperty, Cursors.SizeWE);
            }
            if (_dragging)
            {
                _dragOffset = Math.Clamp(_startOffset - delta.X, 0, _viewer.ScrollableWidth);
                _viewer.ScrollToHorizontalOffset(_dragOffset);
            }
            e.Handled = true;
        }

        private void MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left || !_pressed) return;
            var clickedItem = _dragging ? null : _pressedItem;
            var viewer = _viewer;
            bool wasDragging = _dragging;
            _velocity.Add(e.GetPosition(list).X);
            double releaseVelocity = -_velocity.Velocity;
            double offset = _dragOffset;
            Reset();
            e.Handled = true;
            if (wasDragging && viewer is not null)
            {
                StartInertia(viewer, offset, releaseVelocity);
                return;
            }
            if (clickedItem is null || !list.Items.Contains(clickedItem)) return;
            // Normal clicks still select/focus the item; keyboard navigation is untouched.
            list.SetCurrentValue(Selector.SelectedItemProperty, clickedItem);
            if (Equals(list.SelectedItem, clickedItem)
                && list.ItemContainerGenerator.ContainerFromItem(clickedItem) is ListBoxItem item) item.Focus();
            else list.Focus();
        }

        private void LostCapture(object sender, MouseEventArgs e)
        {
            if (_pressed && Mouse.Captured != _captureElement) Reset();
        }

        private void Unloaded(object sender, RoutedEventArgs e) => Reset();
        private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) { if (!list.IsVisible) Reset(); }
        private void InterruptKey(object sender, KeyEventArgs e) => Reset();

        private void MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Handled) return;
            list.ApplyTemplate();
            var viewer = FindScrollViewer(list);
            if (viewer is null) return;
            // Consume even zero/no-room requests so this gesture never zooms or selects a photo.
            e.Handled = true;
            double current = _animating && _inertiaViewer == viewer ? _inertiaOffset
                : _immediateWheelViewer == viewer ? _immediateWheelOffset : viewer.HorizontalOffset;
            double pending = _wheelAnimating && _inertiaViewer == viewer ? _wheelTarget : current;
            double distance = MotionPhysics.WheelDisplacement(e.Delta, SystemParameters.WheelScrollLines, viewer.ViewportWidth);
            double target = MotionPhysics.WheelTarget(current, pending, distance, viewer.ScrollableWidth);
            Reset();
            if (distance == 0 || Math.Abs(target - current) < .001) return;
            if (!MotionPreferences.CanAnimate)
            {
                // ScrollTo queues work. Keep its requested offset until applied so several
                // high-resolution events before the next layout do not overwrite each other.
                ApplyFinalWheelOffset(viewer, target);
                return;
            }
            _wheelFrom = current; _wheelTarget = target; _inertiaOffset = current;
            _wheelStartedAt = Stopwatch.GetTimestamp(); _wheelAnimating = true;
            AttachAnimation(viewer);
        }

        private void ImmediateWheelApplied(object sender, ScrollChangedEventArgs e)
        {
            if (Math.Abs(e.HorizontalOffset - _immediateWheelOffset) < .001
                || e.ExtentWidthChange != 0 || e.ViewportWidthChange != 0) ClearImmediateWheel();
        }

        private void ApplyFinalWheelOffset(ScrollViewer viewer, double offset)
        {
            ClearImmediateWheel();
            if (Math.Abs(viewer.HorizontalOffset - offset) >= .001)
            {
                _immediateWheelViewer = viewer; _immediateWheelOffset = offset;
                viewer.ScrollChanged += ImmediateWheelApplied;
            }
            viewer.ScrollToHorizontalOffset(offset);
        }

        private void ClearImmediateWheel()
        {
            if (_immediateWheelViewer is not null) _immediateWheelViewer.ScrollChanged -= ImmediateWheelApplied;
            _immediateWheelViewer = null;
        }

        private void Reset()
        {
            StopInertia();
            ClearImmediateWheel();
            var restoreCursor = _dragging;
            var captured = _captureElement;
            _pressed = false; _dragging = false; _pressedItem = null; _viewer = null;
            _captureElement = null;
            if (restoreCursor) list.SetCurrentValue(FrameworkElement.CursorProperty, _previousCursor);
            if (captured is not null && Mouse.Captured == captured) Mouse.Capture(null);
        }

        private void StartInertia(ScrollViewer viewer, double offset, double velocity)
        {
            if (!MotionPreferences.CanCoast || !list.IsVisible || Math.Abs(velocity) < 45
                || viewer.ScrollableWidth <= 0 || (offset <= 0 && velocity < 0)
                || (offset >= viewer.ScrollableWidth && velocity > 0)) return;
            _inertiaOffset = offset; _wheelAnimating = false;
            _inertiaVelocity = Math.Clamp(velocity, -4000, 4000);
            AttachAnimation(viewer);
        }

        private void AttachAnimation(ScrollViewer viewer)
        {
            _inertiaViewer = viewer;
            _lastFrameAt = Stopwatch.GetTimestamp(); _animating = true;
            _inputWindow = Window.GetWindow(list);
            if (_inputWindow is not null)
            {
                // A new action anywhere in the viewer takes over immediately.
                _inputWindow.PreviewMouseDown += WindowMouseDown;
                _inputWindow.PreviewMouseWheel += WindowMouseWheel;
                _inputWindow.PreviewKeyDown += InterruptKey;
                _inputWindow.Deactivated += WindowDeactivated;
            }
            viewer.ScrollChanged += InertiaScrollChanged;
            CompositionTarget.Rendering += RenderInertia;
        }

        private void RenderInertia(object? sender, EventArgs e)
        {
            if (!_animating || _inertiaViewer is not { } viewer) return;
            if (!list.IsVisible || !viewer.IsVisible)
            {
                StopInertia(); return;
            }
            if (!MotionPreferences.CanAnimate)
            {
                if (_wheelAnimating) ApplyFinalWheelOffset(viewer, _wheelTarget);
                StopInertia(); return;
            }
            long now = Stopwatch.GetTimestamp();
            if (_wheelAnimating)
            {
                double t = Math.Clamp(Stopwatch.GetElapsedTime(_wheelStartedAt, now).TotalSeconds / .15, 0, 1);
                double eased = 1 - Math.Pow(1 - t, 3);
                _inertiaOffset = _wheelFrom + (_wheelTarget - _wheelFrom) * eased;
                if (t >= 1) { ApplyFinalWheelOffset(viewer, _wheelTarget); StopInertia(); }
                else viewer.ScrollToHorizontalOffset(_inertiaOffset);
                return;
            }
            double seconds = Stopwatch.GetElapsedTime(_lastFrameAt, now).TotalSeconds;
            _lastFrameAt = now;
            // Do not jump after the UI was suspended by a modal dialog or a long stall.
            if (seconds > .25 || !MotionPreferences.CanCoast) { StopInertia(); return; }
            if (seconds <= 0) return;
            var step = MotionPhysics.Step(_inertiaVelocity, seconds);
            double wanted = _inertiaOffset + step.Displacement;
            _inertiaOffset = Math.Clamp(wanted, 0, viewer.ScrollableWidth);
            _inertiaVelocity = step.Velocity;
            viewer.ScrollToHorizontalOffset(_inertiaOffset);
            if (_inertiaOffset != wanted || Math.Abs(_inertiaVelocity) < 10) StopInertia();
        }

        private void InertiaScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (e.ExtentWidthChange != 0 || e.ViewportWidthChange != 0) StopInertia();
        }
        private void WindowMouseDown(object sender, MouseButtonEventArgs e) => StopInertia();
        private void WindowMouseWheel(object sender, MouseWheelEventArgs e)
        {
            // Window preview runs before the list's preview. Keep the pending target for
            // this list's next wheel event; input elsewhere cancels the animation normally.
            for (DependencyObject? current = e.OriginalSource as DependencyObject; current is not null; current = Parent(current))
                if (current == list) return;
            Reset();
        }
        private void WindowDeactivated(object? sender, EventArgs e) => Reset();

        private void StopInertia()
        {
            if (!_animating) return;
            _animating = false;
            _wheelAnimating = false;
            CompositionTarget.Rendering -= RenderInertia;
            if (_inertiaViewer is not null) _inertiaViewer.ScrollChanged -= InertiaScrollChanged;
            _inertiaViewer = null; _inertiaVelocity = 0;
            if (_inputWindow is not null)
            {
                _inputWindow.PreviewMouseDown -= WindowMouseDown;
                _inputWindow.PreviewMouseWheel -= WindowMouseWheel;
                _inputWindow.PreviewKeyDown -= InterruptKey;
                _inputWindow.Deactivated -= WindowDeactivated;
                _inputWindow = null;
            }
        }

        private bool IsInteractive(DependencyObject source)
        {
            for (DependencyObject? current = source; current is not null && current != list; current = Parent(current))
                if (current is ScrollBar or ButtonBase or TextBoxBase or ComboBox or Slider) return true;
            return false;
        }
    }

    private static DependencyObject? Parent(DependencyObject element) => element is Visual or Visual3D
        ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);

    private static ScrollViewer? FindScrollViewer(DependencyObject element)
    {
        if (element is ScrollViewer viewer) return viewer;
        if (element is not Visual && element is not Visual3D) return null;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            var found = FindScrollViewer(VisualTreeHelper.GetChild(element, i));
            if (found is not null) return found;
        }
        return null;
    }
}
