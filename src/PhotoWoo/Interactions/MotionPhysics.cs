using System.Diagnostics;

namespace PhotoWoo.Interactions;

/// <summary>Frame-rate-independent free motion with exponential friction.</summary>
public static class MotionPhysics
{
    /// <summary>Wheel delta to horizontal DIPs: positive Windows delta moves toward the start.</summary>
    public static double WheelDisplacement(int delta, int scrollLines, double viewportWidth)
    {
        if (delta == 0 || scrollLines == 0 || scrollLines < -1) return 0;
        double distance = scrollLines == -1 ? viewportWidth : scrollLines * 36d;
        if (!double.IsFinite(distance) || distance <= 0) return 0;
        return -(delta / 120d) * distance;
    }

    /// <summary>Accumulate matching wheel input; a reversal cancels the untravelled old target.</summary>
    public static double WheelTarget(double current, double pendingTarget, double displacement, double maximum)
    {
        if (!double.IsFinite(maximum) || maximum <= 0) return 0;
        current = double.IsFinite(current) ? Math.Clamp(current, 0, maximum) : 0;
        if (!double.IsFinite(displacement) || displacement == 0) return current;
        pendingTarget = double.IsFinite(pendingTarget) ? Math.Clamp(pendingTarget, 0, maximum) : current;
        double start = Math.Sign(pendingTarget - current) == Math.Sign(displacement) ? pendingTarget : current;
        return Math.Clamp(start + displacement, 0, maximum);
    }

    public static (double Displacement, double Velocity) Step(double velocity, double seconds)
    {
        if (!double.IsFinite(velocity) || !double.IsFinite(seconds)) return (0, 0);
        if (seconds <= 0 || velocity == 0) return (0, velocity);
        const double friction = 6;
        double decay = Math.Exp(-friction * seconds);
        return ((velocity / friction) * (1 - decay), velocity * decay);
    }
}

/// <summary>A short trailing pointer-motion window, measured in pixels per second.</summary>
public sealed class PointerVelocityTracker
{
    private const double WindowSeconds = .09, StaleSeconds = .1;
    private const int SampleLimit = 32;
    private readonly Func<long> _timestamp;
    private readonly double _frequency;
    private readonly List<(double Position, long Ticks)> _samples = new(SampleLimit);

    public PointerVelocityTracker() : this(Stopwatch.GetTimestamp, Stopwatch.Frequency) { }

    // A deterministic clock makes stale-sample and frame-rate checks independent of test timing.
    internal PointerVelocityTracker(Func<long> timestamp, long frequency)
    {
        ArgumentNullException.ThrowIfNull(timestamp);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency);
        _timestamp = timestamp; _frequency = frequency;
    }

    public void Reset(double position)
    {
        _samples.Clear();
        if (double.IsFinite(position)) _samples.Add((position, _timestamp()));
    }

    public void Add(double position)
    {
        if (!double.IsFinite(position)) { _samples.Clear(); return; }
        long now = _timestamp();
        if (_samples.Count > 0)
        {
            double gap = (now - _samples[^1].Ticks) / _frequency;
            if (gap < 0 || gap > StaleSeconds) _samples.Clear();
            else if (gap == 0)
            {
                _samples[^1] = (position, now);
                return;
            }
        }
        _samples.Add((position, now));
        while (_samples.Count > 2 && (now - _samples[1].Ticks) / _frequency > WindowSeconds)
            _samples.RemoveAt(0);
        if (_samples.Count > SampleLimit) _samples.RemoveAt(0);
    }

    public double Velocity
    {
        get
        {
            if (_samples.Count < 2) return 0;
            var first = _samples[0]; var last = _samples[^1];
            double age = (_timestamp() - last.Ticks) / _frequency;
            if (age < 0 || age > StaleSeconds) return 0;
            double seconds = (last.Ticks - first.Ticks) / _frequency;
            if (seconds < .001) return 0;
            double velocity = (last.Position - first.Position) / seconds;
            return double.IsFinite(velocity) ? velocity : 0;
        }
    }
}
