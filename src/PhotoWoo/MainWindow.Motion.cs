using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using PhotoWoo.Interactions;

namespace PhotoWoo;

public partial class MainWindow
{
    private readonly PointerVelocityTracker _pointerX = new(), _pointerY = new();
    private readonly RotateTransform _photoRotation = new();
    private readonly TranslateTransform _photoOffset = new(), _arrivalOffset = new();
    private Vector _panVelocity, _motionFromPan, _motionToPan;
    private double _motionFromZoom, _motionToZoom, _motionDuration;
    private long _motionStart, _motionLastFrame;
    private int _motionKind, _incomingDirection; // 0: stopped, 1: inertia, 2: short view transition
    private bool _motionFitsAtEnd, _motionClampPan;

    private Vector ClampPan(Vector pan, double zoom)
    {
        if (_loaded is null) return default;
        double width = _loaded.Bitmap.PixelWidth, height = _loaded.Bitmap.PixelHeight;
        if (_turns % 2 != 0) (width, height) = (height, width);
        double x = Math.Max(0, (width * zoom - Viewport.ActualWidth) / 2);
        double y = Math.Max(0, (height * zoom - Viewport.ActualHeight) / 2);
        return new Vector(Math.Clamp(pan.X, -x, x), Math.Clamp(pan.Y, -y, y));
    }

    private void StartPanInertia(Vector velocity)
    {
        StopImageMotion();
        if (!MotionPreferences.CanCoast || velocity.Length < 45 || _fit) return;
        _panVelocity = new Vector(Math.Clamp(velocity.X, -3500, 3500), Math.Clamp(velocity.Y, -3500, 3500));
        _motionKind = 1;
        _motionLastFrame = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += AdvanceImageMotion;
    }

    private void AnimateView(double zoom, Vector pan, double duration = .16, bool allowOverscroll = false)
    {
        StopImageMotion();
        if (!MotionPreferences.CanAnimate)
        {
            _zoom = zoom; _pan = pan; RenderPhoto(); return;
        }
        _motionFromZoom = _zoom; _motionToZoom = zoom;
        _motionFromPan = _pan; _motionToPan = pan; _motionDuration = duration;
        _motionFitsAtEnd = _fit; _motionClampPan = !allowOverscroll;
        // Until fitting finishes, a pointer interruption must still pan the enlarged image.
        if (_fit && Math.Abs(zoom - _zoom) > .00001) _fit = false;
        _motionStart = _motionLastFrame = Stopwatch.GetTimestamp();
        _motionKind = 2;
        CompositionTarget.Rendering += AdvanceImageMotion;
    }

    private void AdvanceImageMotion(object? sender, EventArgs e)
    {
        if (_loaded is null || !IsVisible) { StopImageMotion(); return; }
        long now = Stopwatch.GetTimestamp();
        double elapsed = (double)(now - _motionLastFrame) / Stopwatch.Frequency;
        if (elapsed <= 0) return;
        _motionLastFrame = now;
        if (_motionKind == 1)
        {
            if (elapsed > .25 || !MotionPreferences.CanCoast) { StopImageMotion(); return; }
            var horizontal = MotionPhysics.Step(_panVelocity.X, elapsed);
            var vertical = MotionPhysics.Step(_panVelocity.Y, elapsed);
            var desired = _pan + new Vector(horizontal.Displacement, vertical.Displacement);
            _pan = ClampPan(desired, _zoom);
            _panVelocity = new Vector(_pan.X == desired.X ? horizontal.Velocity : 0,
                _pan.Y == desired.Y ? vertical.Velocity : 0);
            if (_panVelocity.Length < 12) StopImageMotion();
        }
        else if (_motionKind == 2)
        {
            double t = Math.Clamp((double)(now - _motionStart) / Stopwatch.Frequency / _motionDuration, 0, 1);
            double eased = 1 - Math.Pow(1 - t, 3);
            _zoom = _motionFromZoom + (_motionToZoom - _motionFromZoom) * eased;
            _pan = _motionFromPan + (_motionToPan - _motionFromPan) * eased;
            if (_motionClampPan) _pan = ClampPan(_pan, _zoom);
            if (t >= 1) { _fit = _motionFitsAtEnd; StopImageMotion(); }
        }
        RenderPhoto();
    }

    private void StopImageMotion()
    {
        if (_motionKind == 0) return;
        CompositionTarget.Rendering -= AdvanceImageMotion;
        _motionKind = 0; _panVelocity = default;
    }

    private void AnimatePhotoArrival(int direction)
    {
        StopPhotoArrival();
        if (!MotionPreferences.CanAnimate) return;
        var duration = TimeSpan.FromMilliseconds(150);
        PhotoImage.BeginAnimation(OpacityProperty, new DoubleAnimation(.78, 1, duration) { FillBehavior = FillBehavior.Stop });
        if (direction != 0)
            _arrivalOffset.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(direction * 18, 0, duration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop
            });
    }

    private void StopPhotoArrival()
    {
        PhotoImage.BeginAnimation(OpacityProperty, null);
        _arrivalOffset.BeginAnimation(TranslateTransform.XProperty, null);
    }
}
