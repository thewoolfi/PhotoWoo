using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PhotoWoo.Interactions;

/// <summary>Short visual feedback for template layers; never changes layout or input handling.</summary>
public static class ControlFeedback
{
    public static readonly DependencyProperty FadeToProperty = DependencyProperty.RegisterAttached(
        "FadeTo", typeof(double), typeof(ControlFeedback), new PropertyMetadata(1d, FadeChanged));

    public static readonly DependencyProperty PressOffsetProperty = DependencyProperty.RegisterAttached(
        "PressOffset", typeof(double), typeof(ControlFeedback), new PropertyMetadata(0d, PressChanged));

    public static void SetFadeTo(DependencyObject element, double value) => element.SetValue(FadeToProperty, value);
    public static double GetFadeTo(DependencyObject element) => (double)element.GetValue(FadeToProperty);
    public static void SetPressOffset(DependencyObject element, double value) => element.SetValue(PressOffsetProperty, value);
    public static double GetPressOffset(DependencyObject element) => (double)element.GetValue(PressOffsetProperty);

    private static void FadeChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is FrameworkElement element)
            Animate(element, element, UIElement.OpacityProperty, Math.Clamp((double)e.NewValue, 0, 1));
    }

    private static void PressChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not FrameworkElement element) return;
        // This property is applied only to the template's content, leaving the button's hit box fixed.
        if (element.RenderTransform is not TranslateTransform translation || translation.IsFrozen)
        {
            translation = new TranslateTransform();
            element.SetCurrentValue(UIElement.RenderTransformProperty, translation);
        }
        Animate(element, translation, TranslateTransform.YProperty, (double)e.NewValue);
    }

    private static void Animate(FrameworkElement owner, DependencyObject target, DependencyProperty property, double value)
    {
        var current = (double)target.GetValue(property);
        var animated = (IAnimatable)target;
        animated.BeginAnimation(property, null);
        // These properties belong to our dedicated template layers. SetValue supplies a real
        // animation base value; SetCurrentValue's coerced value can reset when the clock stops.
        target.SetValue(property, value);

        // Recycled/unloaded controls and reduced-motion preferences receive their final state immediately.
        if (!owner.IsLoaded || !MotionPreferences.CanAnimate || Math.Abs(current - value) < 0.001)
            return;

        animated.BeginAnimation(property, new DoubleAnimation(current, value, TimeSpan.FromMilliseconds(110))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        }, HandoffBehavior.SnapshotAndReplace);
    }
}
