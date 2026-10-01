namespace PhotoWoo.Interactions;

public static class ViewerGestures
{
    /// <summary>One horizontal drag advances at most one image; vertical/short drags do nothing.</summary>
    public static int NavigationStep(double deltaX, double deltaY)
    {
        if (!double.IsFinite(deltaX) || !double.IsFinite(deltaY)) return 0;
        if (Math.Abs(deltaX) < 64 || Math.Abs(deltaX) < Math.Abs(deltaY) * 1.4) return 0;
        return deltaX < 0 ? 1 : -1;
    }
}
