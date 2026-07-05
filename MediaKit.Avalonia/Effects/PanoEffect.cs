using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace MediaKit.Avalonia.Effects;

public partial class PanoEffect
{
    private double _initialFov, _initialZoom, _initialRotationX, _initialRotationY;
    private bool _isPressed;
    private Point _lastPos;
    private DateTimeOffset _lastTime;
    private double _velocityX, _velocityY;
    private bool _inertiaActive;
    private double _lastFrameTime;

    // 惯性阈值（单位/秒），与 WPF 对齐：启动与停止共用同一阈值
    private const double InertiaThreshold = 0.0005;

    public void Reset()
    {
        StopInertia();
        Fov = _initialFov;
        Zoom = _initialZoom;
        RotationX = _initialRotationX;
        RotationY = _initialRotationY;
    }

    protected override void OnAttached(Control target)
    {
        _initialFov = _fov;
        _initialZoom = _zoom;
        _initialRotationX = _rotationX;
        _initialRotationY = _rotationY;

        target.PointerPressed += OnPointerPressed;
        target.PointerMoved += OnPointerMoved;
        target.PointerReleased += OnPointerReleased;
        target.PointerWheelChanged += OnPointerWheel;
    }

    protected override void OnDetaching()
    {
        StopInertia();
        if (Target == null) return;
        Target.PointerPressed -= OnPointerPressed;
        Target.PointerMoved -= OnPointerMoved;
        Target.PointerReleased -= OnPointerReleased;
        Target.PointerWheelChanged -= OnPointerWheel;
    }

    protected override bool OnFrameUpdate()
    {
        if (!_inertiaActive) return false;

        var elapsed = GetCurrentTime();
        var dt = elapsed - _lastFrameTime;
        _lastFrameTime = elapsed;
        if (dt <= 0 || dt > 0.1) return true;

        var rx = _rotationX + _velocityX * dt;
        var ry = _rotationY + _velocityY * dt;

        const double friction = 0.95;
        var decay = Math.Pow(friction, dt / 0.016);
        _velocityX *= decay;
        _velocityY *= decay;

        if (Math.Abs(_velocityX) < InertiaThreshold && Math.Abs(_velocityY) < InertiaThreshold)
        {
            _inertiaActive = false;
            return false;
        }

        if (rx > 1.0) rx -= 1.0;
        if (rx < 0.0) rx += 1.0;
        ry = Math.Max(0.01, Math.Min(0.99, ry));

        var oldRx = _rotationX;
        var oldRy = _rotationY;
        _rotationX = rx;                 // 直接更新后备字段
        _rotationY = ry;
        this["rotationX"] = (float)rx;   // 推给着色器（当前帧渲染必需）
        this["rotationY"] = (float)ry;

        Target!.Dispatcher.InvokeAsync(() =>
        {
            // 仅发出变更通知供绑定刷新，不再走 setter，避免重复推参
            RaisePropertyChanged(RotationXProperty, oldRx, _rotationX);
            RaisePropertyChanged(RotationYProperty, oldRy, _rotationY);
        });

        return true;
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Target == null || !e.GetCurrentPoint(Target).Properties.IsLeftButtonPressed) return;
        _isPressed = true;
        _lastPos = e.GetPosition(Target);
        _lastTime = DateTimeOffset.Now;
        _velocityX = 0;
        _velocityY = 0;
        StopInertia();
        e.Pointer.Capture(Target);
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (Target == null || !_isPressed) return;
        if (!e.GetCurrentPoint(Target).Properties.IsLeftButtonPressed)
        {
            _isPressed = false;
            return;
        }

        var pos = e.GetPosition(Target);
        var now = DateTimeOffset.Now;
        var dt = Math.Max(1, (now - _lastTime).TotalMilliseconds);

        var dx = pos.X - _lastPos.X;
        var dy = pos.Y - _lastPos.Y;
        var w = Target.Bounds.Width;
        var h = Target.Bounds.Height;
        if (w <= 0 || h <= 0) return;

        var instantVx = dx / w * 0.5 / (dt / 1000.0);
        var instantVy = -dy / h * 0.5 / (dt / 1000.0);
        const double smoothing = 0.4;
        _velocityX = _velocityX * (1 - smoothing) + instantVx * smoothing;
        _velocityY = _velocityY * (1 - smoothing) + instantVy * smoothing;

        var rx = _rotationX + dx / w * 0.5;
        var ry = _rotationY - dy / h * 0.5;

        if (rx > 1.0) rx -= 1.0;
        if (rx < 0.0) rx += 1.0;
        ry = Math.Max(0.01, Math.Min(0.99, ry));

        RotationX = rx;
        RotationY = ry;

        _lastPos = pos;
        _lastTime = now;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isPressed) return;
        _isPressed = false;
        e.Pointer.Capture(null);
        BeginInertia();
    }

    private void OnPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        Zoom = Math.Max(0.1, Math.Min(2.0, _zoom + (e.Delta.Y > 0 ? 0.05 : -0.05)));
    }

    private void BeginInertia()
    {
        var speed = Math.Sqrt(_velocityX * _velocityX + _velocityY * _velocityY);
        if (speed < InertiaThreshold) return;

        _inertiaActive = true;
        StartAnimation();
        _lastFrameTime = GetCurrentTime();
    }

    private void StopInertia()
    {
        _inertiaActive = false;
    }
}
