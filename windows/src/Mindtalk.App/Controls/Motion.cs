namespace Mindtalk.App.Controls;

/// <summary>
/// A value that moves to its target the way SwiftUI animates: a spring given by its
/// duration and bounce (stiffness (2π/d)², damping 4π(1−b)/d — SwiftUI's own mapping),
/// or an ease-out tween. Stepped once per frame.
/// </summary>
public sealed class Animated
{
    private double _velocity, _from, _elapsed, _duration;
    private double _stiffness, _damping;
    private bool _spring, _moving;

    public Animated(double value) { Value = Target = value; }

    public double Value { get; private set; }
    public double Target { get; private set; }
    public bool IsMoving => _moving;

    public void Spring(double target, double duration, double bounce)
    {
        if (target == Target && _moving) return;
        Target = target;
        _spring = true;
        _stiffness = Math.Pow(2 * Math.PI / duration, 2);
        _damping = 4 * Math.PI * (1 - bounce) / duration;
        _moving = Value != target || _velocity != 0;
    }

    public void EaseOut(double target, double duration)
    {
        if (target == Target && _moving) return;
        Target = target;
        _spring = false;
        _velocity = 0;
        _from = Value;
        _elapsed = 0;
        _duration = duration;
        _moving = Value != target;
    }

    public void Set(double value)
    {
        Value = Target = value;
        _velocity = 0;
        _moving = false;
    }

    /// Advances by <paramref name="dt"/> seconds; false once it has come to rest.
    public bool Step(double dt)
    {
        if (!_moving) return false;
        if (_spring)
        {
            // Small fixed steps keep a stiff spring stable at any frame rate.
            for (var t = 0.0; t < dt; t += 1 / 240.0)
            {
                var h = Math.Min(1 / 240.0, dt - t);
                var force = -_stiffness * (Value - Target) - _damping * _velocity;
                _velocity += force * h;
                Value += _velocity * h;
            }
            if (Math.Abs(Value - Target) < 0.01 && Math.Abs(_velocity) < 0.05) { Value = Target; _velocity = 0; _moving = false; }
        }
        else
        {
            _elapsed += dt;
            var p = Math.Min(1, _elapsed / _duration);
            // SwiftUI's easeOut: cubic-bezier(0, 0, 0.58, 1).
            Value = _from + (Target - _from) * Ease.Out(p);
            if (p >= 1) _moving = false;
        }
        return true;
    }
}

public static class Ease
{
    /// cubic-bezier(0, 0, 0.58, 1) at time <paramref name="t"/>.
    public static double Out(double t) => Bezier(t, 0, 0, 0.58, 1);

    /// cubic-bezier(0.42, 0, 0.58, 1).
    public static double InOut(double t) => Bezier(t, 0.42, 0, 0.58, 1);

    private static double Bezier(double t, double x1, double y1, double x2, double y2)
    {
        if (t <= 0) return 0;
        if (t >= 1) return 1;
        // Solve x(u) = t for u (Newton), then return y(u).
        var u = t;
        for (var i = 0; i < 8; i++)
        {
            var x = Cubic(u, x1, x2) - t;
            var dx = 3 * (1 - u) * (1 - u) * x1 + 6 * (1 - u) * u * (x2 - x1) + 3 * u * u * (1 - x2);
            if (Math.Abs(dx) < 1e-6) break;
            u = Math.Clamp(u - x / dx, 0, 1);
        }
        return Cubic(u, y1, y2);
    }

    private static double Cubic(double u, double p1, double p2) =>
        3 * (1 - u) * (1 - u) * u * p1 + 3 * (1 - u) * u * u * p2 + u * u * u;
}
