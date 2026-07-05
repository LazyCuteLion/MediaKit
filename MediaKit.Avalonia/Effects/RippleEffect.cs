using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace MediaKit.Avalonia.Effects;

public partial class RippleEffect
{
    private const int MaxRipples = 4;
    private int _rippleIndex;
    private float _lastRippleTime = -10f;

    public void AddRipple(Point normalizedPosition)
    {
        StartAnimation();

        var time = (float)GetCurrentTime();
        _lastRippleTime = time;
        this[$"iRipple{_rippleIndex % MaxRipples}"] = new[]
        {
            (float)normalizedPosition.X,
            (float)normalizedPosition.Y,
            time
        };
        _rippleIndex++;
    }

    protected override bool OnFrameUpdate()
    {
        return (float)GetCurrentTime() - _lastRippleTime < (float)_duration;
    }

    protected override void OnAttached(Control target)
    {
        target.PointerPressed += OnPointerPressed;
    }

    protected override void OnDetaching()
    {
        if (Target != null)
            Target.PointerPressed -= OnPointerPressed;
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Target == null) return;
        var pos = e.GetPosition(Target);
        var w = Target.Bounds.Width;
        var h = Target.Bounds.Height;
        if (w <= 0 || h <= 0) return;

        AddRipple(new Point(pos.X / w, pos.Y / h));
    }
}
