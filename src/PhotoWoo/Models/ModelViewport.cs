using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using PhotoWoo.Interactions;

namespace PhotoWoo.Models;

public sealed class ModelViewport : Border
{
    private readonly Viewport3D _viewport = new() { ClipToBounds = true, IsHitTestVisible = false };
    private readonly PerspectiveCamera _camera = new() { FieldOfView = 45, NearPlaneDistance = .005, FarPlaneDistance = 200 };
    private readonly ModelVisual3D _model = new();
    private Point3D _target;
    private Point? _pointer;
    private MouseButton _button;
    private Vector _velocity;
    private double _yaw = .6, _pitch = .25, _distance = 4, _distanceTarget = 4, _fitDistance = 4;
    private long _lastPointerTime, _lastFrame;
    private bool _rendering, _fitted = true;
    public bool HasModel => _model.Content is not null;
    public event EventHandler? ViewChanged;
    public double ZoomPercent => _fitDistance / _distance * 100;

    public ModelViewport()
    {
        Background = Brushes.Transparent;
        Focusable = true; FocusVisualStyle = null; ClipToBounds = true;
        Child = _viewport; _viewport.Camera = _camera;
        var lights = new Model3DGroup();
        lights.Children.Add(new AmbientLight(Color.FromRgb(100, 100, 100)));
        lights.Children.Add(new DirectionalLight(Color.FromRgb(210, 210, 210), new Vector3D(-1, -2, -3)));
        lights.Children.Add(new DirectionalLight(Color.FromRgb(105, 116, 126), new Vector3D(2, 1, 2)));
        lights.Freeze();
        _viewport.Children.Add(new ModelVisual3D { Content = lights });
        _viewport.Children.Add(_model);
        SizeChanged += (_, _) => { if (_fitted) Fit(); };
        Unloaded += (_, _) => Stop();
        IsVisibleChanged += (_, _) => { if (!IsVisible) Stop(); };
    }

    public void SetModel(LoadedModel? model)
    {
        Stop();
        _model.Content = null;
        if (model is null) return;
        var bounds = model.Scene.Bounds;
        var radius = new Vector3D(bounds.SizeX, bounds.SizeY, bounds.SizeZ).Length / 2;
        if (!double.IsFinite(radius) || radius <= 0) throw new InvalidOperationException("The model has no visible bounds.");
        var transform = new Transform3DGroup();
        transform.Children.Add(new TranslateTransform3D(-bounds.X - bounds.SizeX / 2, -bounds.Y - bounds.SizeY / 2, -bounds.Z - bounds.SizeZ / 2));
        transform.Children.Add(new ScaleTransform3D(1 / radius, 1 / radius, 1 / radius));
        transform.Freeze();
        _model.Transform = transform; _model.Content = model.Scene;
        ResetView();
    }

    public void ResetView()
    {
        Stop(); _target = new Point3D(); _yaw = .6; _pitch = .25; _fitted = true; Fit();
    }

    private void Fit()
    {
        var aspect = Math.Max(.1, ActualWidth / Math.Max(1, ActualHeight));
        var horizontal = _camera.FieldOfView * Math.PI / 360;
        var vertical = Math.Atan(Math.Tan(horizontal) / aspect);
        _fitDistance = 1.12 / Math.Sin(Math.Min(horizontal, vertical));
        _distance = _distanceTarget = _fitDistance;
        UpdateCamera();
    }

    public void Zoom(double factor)
    {
        if (!HasModel) return;
        _fitted = false;
        _distanceTarget = Math.Clamp(_distanceTarget / factor, .08, 100);
        if (MotionPreferences.AnimationsEnabled) StartFrames();
        else { _distance = _distanceTarget; UpdateCamera(); }
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (!HasModel || e.ChangedButton is not (MouseButton.Left or MouseButton.Right or MouseButton.Middle)) return;
        Focus(); Stop();
        if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2) { ResetView(); e.Handled = true; return; }
        _button = e.ChangedButton; _pointer = e.GetPosition(this); _lastPointerTime = Stopwatch.GetTimestamp();
        _fitted = false; CaptureMouse(); Cursor = Cursors.SizeAll; e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_pointer is not { } previous) return;
        var current = e.GetPosition(this);
        var delta = current - previous;
        var now = Stopwatch.GetTimestamp();
        var seconds = Math.Max(.008, (now - _lastPointerTime) / (double)Stopwatch.Frequency);
        _lastPointerTime = now; _pointer = current;
        if (_button == MouseButton.Left && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            _yaw -= delta.X * .008; _pitch = Math.Clamp(_pitch + delta.Y * .008, -1.5, 1.5);
            _velocity = new Vector(Math.Clamp(-delta.X * .008 / seconds, -6, 6), Math.Clamp(delta.Y * .008 / seconds, -6, 6));
        }
        else
        {
            _velocity = default;
            var forward = _camera.LookDirection; forward.Normalize();
            var right = Vector3D.CrossProduct(forward, new Vector3D(0, 1, 0)); right.Normalize();
            var up = Vector3D.CrossProduct(right, forward); up.Normalize();
            var scale = 2 * _distance * Math.Tan(_camera.FieldOfView * Math.PI / 360) / Math.Max(1, ActualWidth);
            _target += (-right * delta.X + up * delta.Y) * scale;
        }
        UpdateCamera(); e.Handled = true;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (_pointer is null || e.ChangedButton != _button) return;
        var velocity = _velocity;
        if (Stopwatch.GetElapsedTime(_lastPointerTime).TotalMilliseconds > 100) velocity = default;
        _pointer = null; ReleaseMouseCapture(); Cursor = Cursors.Arrow;
        _velocity = MotionPreferences.InertiaEnabled ? velocity : default;
        if (_velocity.Length > .02) StartFrames();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _pointer = null; _velocity = default; Cursor = Cursors.Arrow;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        Zoom(Math.Pow(1.15, e.Delta / 120d)); e.Handled = true;
    }

    public void Stop()
    {
        _pointer = null; _velocity = default; _distanceTarget = _distance;
        if (IsMouseCaptured) ReleaseMouseCapture();
        Cursor = Cursors.Arrow;
        if (_rendering) CompositionTarget.Rendering -= Advance;
        _rendering = false;
    }

    private void StartFrames()
    {
        if (_rendering) return;
        _lastFrame = Stopwatch.GetTimestamp(); _rendering = true; CompositionTarget.Rendering += Advance;
    }

    private void Advance(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        var dt = Math.Clamp((now - _lastFrame) / (double)Stopwatch.Frequency, 0, .05);
        _lastFrame = now;
        _distance += (_distanceTarget - _distance) * (1 - Math.Exp(-18 * dt));
        if (_pointer is null)
        {
            _yaw += _velocity.X * dt; _pitch = Math.Clamp(_pitch + _velocity.Y * dt, -1.5, 1.5);
            _velocity *= Math.Exp(-7 * dt);
        }
        UpdateCamera();
        if (_velocity.Length < .01 && Math.Abs(_distanceTarget - _distance) < .0005) Stop();
    }

    private void UpdateCamera()
    {
        var offset = new Vector3D(Math.Sin(_yaw) * Math.Cos(_pitch), Math.Sin(_pitch), Math.Cos(_yaw) * Math.Cos(_pitch)) * _distance;
        _camera.Position = _target + offset; _camera.LookDirection = -offset; _camera.UpDirection = new Vector3D(0, 1, 0);
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }
}
