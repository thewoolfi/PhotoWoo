using System.Windows;

namespace PhotoWoo.Interactions;

public static class MotionPreferences
{
    public static bool AnimationsEnabled { get; set; } = true;
    public static bool InertiaEnabled { get; set; } = true;
    public static bool CanAnimate => AnimationsEnabled && SystemParameters.ClientAreaAnimation;
    public static bool CanCoast => CanAnimate && InertiaEnabled;
}
