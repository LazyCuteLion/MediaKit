using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace MediaKit.Avalonia.Effects;

/// <summary>
/// 抬手后开始惯性滑动。UI 线程 → 合成线程。带上抬手瞬间的角度与角速度：
/// 惯性期间 UI 不会再改 rotation，所以这一瞬的角度就是渲染侧积分的起点。
/// </summary>
internal sealed record BeginInertiaMessage(double RotationX, double RotationY, double VelocityX, double VelocityY);

public partial class PanoEffect
{
    private double _initialFov, _initialZoom, _initialRotationX, _initialRotationY;
    private bool _isPressed;
    private Point _lastPos;
    private DateTimeOffset _lastTime;
    private double _velocityX, _velocityY;

    // 惯性阈值（单位/秒），与 WPF 对齐：启动与停止共用同一阈值
    internal const double InertiaThreshold = 0.0005;

    public void Reset()
    {
        StopInertia();
        Fov = _initialFov;
        Zoom = _initialZoom;
        RotationX = _initialRotationX;
        RotationY = _initialRotationY;
    }

    internal override ShaderRenderer CreateRenderer(string sksl, Dictionary<string, object> uniforms,
        Dictionary<string, Uri?> textures)
        => new PanoRenderer(this, sksl, uniforms, textures);

    /// <summary>
    /// 由 <see cref="PanoRenderer"/> 经 UI 线程回调：惯性推进后的角度回写。
    /// 走 <c>SetAndRaise</c> 而不走属性 setter——值本来就是渲染侧算的，不必再推回去。
    /// </summary>
    internal void UpdateRotationFromRenderer(double rotationX, double rotationY)
    {
        SetAndRaise(RotationXProperty, ref _rotationX, rotationX);
        SetAndRaise(RotationYProperty, ref _rotationY, rotationY);
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

        RotationX = PanoRenderer.WrapRotationX(rx);
        RotationY = PanoRenderer.ClampRotationY(ry);

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

        StartAnimation();
        SendMessage(new BeginInertiaMessage(_rotationX, _rotationY, _velocityX, _velocityY));
    }

    private void StopInertia() => SendMessage(PanoRenderer.StopInertiaMessage);
}

/// <summary>
/// 在合成线程推进惯性滑动：角速度按帧间隔衰减，每帧直接写入 rotationX / rotationY，
/// 只把结果回写 UI 供绑定刷新。
/// </summary>
internal sealed class PanoRenderer : ShaderRenderer
{
    public static readonly object StopInertiaMessage = new();

    private readonly PanoEffect _pano;

    private bool _inertiaActive;
    private double _velocityX, _velocityY;
    private double _rotationX, _rotationY;
    private double _lastFrameTime;

    internal PanoRenderer(PanoEffect owner, string sksl, Dictionary<string, object> uniforms,
        Dictionary<string, Uri?> textures)
        : base(owner, sksl, uniforms, textures)
    {
        _pano = owner;
    }

    internal static double WrapRotationX(double value)
    {
        if (value > 1.0) value -= 1.0;
        if (value < 0.0) value += 1.0;
        return value;
    }

    internal static double ClampRotationY(double value) => Math.Max(0.01, Math.Min(0.99, value));

    public override void OnMessage(object message)
    {
        if (message is BeginInertiaMessage b)
        {
            _rotationX = b.RotationX;
            _rotationY = b.RotationY;
            _velocityX = b.VelocityX;
            _velocityY = b.VelocityY;
            _inertiaActive = true;
            _lastFrameTime = Elapsed;
        }
        else if (ReferenceEquals(message, StopInertiaMessage))
        {
            _inertiaActive = false;
        }
        else if (message is SetUniformMessage u && (u.Name == "rotationX" || u.Name == "rotationY"))
        {
            _inertiaActive = false;
            base.OnMessage(message);
        }
        else
        {
            base.OnMessage(message);
        }
    }

    protected override bool OnFrameUpdate(double elapsed)
    {
        if (!_inertiaActive) return false;

        var dt = elapsed - _lastFrameTime;
        _lastFrameTime = elapsed;
        // 首帧或卡顿后的长间隔：只重置时基，不按异常 dt 积分
        if (dt <= 0 || dt > 0.1) return true;

        var rx = _rotationX + _velocityX * dt;
        var ry = _rotationY + _velocityY * dt;

        const double friction = 0.95;
        var decay = Math.Pow(friction, dt / 0.016);
        _velocityX *= decay;
        _velocityY *= decay;

        if (Math.Abs(_velocityX) < PanoEffect.InertiaThreshold &&
            Math.Abs(_velocityY) < PanoEffect.InertiaThreshold)
        {
            _inertiaActive = false;
            return false;
        }

        _rotationX = WrapRotationX(rx);
        _rotationY = ClampRotationY(ry);

        SetUniform("rotationX", (float)_rotationX);
        SetUniform("rotationY", (float)_rotationY);

        var notifyX = _rotationX;
        var notifyY = _rotationY;
        Dispatcher.UIThread.Post(() => _pano.UpdateRotationFromRenderer(notifyX, notifyY));

        return true;
    }
}
