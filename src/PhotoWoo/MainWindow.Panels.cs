using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using PhotoWoo.Interactions;

namespace PhotoWoo;

public partial class MainWindow
{
    private bool _infoShown;
    private readonly Dictionary<FrameworkElement, int> _panelTransitions = [];

    private void ShowInformation(bool show)
    {
        _infoShown = show;
        AnimatePanel(InfoPanel, show, WidthProperty, 240);
        InfoButton.Foreground = new SolidColorBrush(show ? Color.FromRgb(169, 200, 189) : Color.FromRgb(213, 218, 219));
    }

    private void AnimatePanel(FrameworkElement panel, bool show, DependencyProperty dimension, double expanded)
    {
        EndImageDrag();
        int revision = _panelTransitions.GetValueOrDefault(panel) + 1;
        _panelTransitions[panel] = revision;
        if (_fullScreen)
        {
            panel.BeginAnimation(dimension, null); panel.BeginAnimation(OpacityProperty, null);
            panel.SetValue(dimension, expanded); panel.Opacity = 1; panel.IsHitTestVisible = show;
            panel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            ShowFullscreenChrome(immediate: true);
            if (show && panel == FilmstripBorder) RequestFilmstripThumbnails();
            return;
        }
        double from = panel.Visibility == Visibility.Collapsed ? 0
            : dimension == WidthProperty ? panel.ActualWidth : panel.ActualHeight;
        double fromOpacity = panel.Visibility == Visibility.Collapsed ? 0 : panel.Opacity;
        panel.BeginAnimation(dimension, null);
        panel.BeginAnimation(OpacityProperty, null);
        panel.IsHitTestVisible = show;
        if (!MotionPreferences.CanAnimate)
        {
            panel.SetValue(dimension, expanded); panel.Opacity = 1;
            panel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (show && panel == FilmstripBorder) RequestFilmstripThumbnails();
            return;
        }
        panel.Visibility = Visibility.Visible;
        panel.SetValue(dimension, show ? expanded : 0d);
        panel.Opacity = show ? 1 : 0;
        var time = TimeSpan.FromMilliseconds(180);
        var extent = new DoubleAnimation(from, show ? expanded : 0, time)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        extent.Completed += (_, _) =>
        {
            if (_panelTransitions.GetValueOrDefault(panel) != revision) return;
            panel.BeginAnimation(dimension, null); panel.BeginAnimation(OpacityProperty, null);
            panel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            panel.SetValue(dimension, expanded); panel.Opacity = 1;
            if (show && panel == FilmstripBorder) RequestFilmstripThumbnails();
        };
        panel.BeginAnimation(dimension, extent);
        panel.BeginAnimation(OpacityProperty, new DoubleAnimation(fromOpacity, show ? 1 : 0, time)
        {
            FillBehavior = FillBehavior.Stop
        });
    }

    private void AnimateRotation(double start, int direction = 0)
    {
        _photoRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        if (!MotionPreferences.CanAnimate) return;
        double target = _turns * 90;
        if (direction > 0) { while (target < start) target += 360; }
        else if (direction < 0) { while (target > start) target -= 360; }
        else target += Math.Round((start - target) / 360) * 360;
        _photoRotation.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(start, target, TimeSpan.FromMilliseconds(170))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            });
    }
}
