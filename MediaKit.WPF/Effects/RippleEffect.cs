using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;

namespace MediaKit.WPF.Effects;

/// <summary>
/// RippleEffect 的用户自定义逻辑部分。
/// 基础设施（_shader / Input / 标记属性 / Target / 构造函数 / MarkupExtension）
/// 由源生成器产出于 RippleEffect.g.cs。
/// </summary>
public partial class RippleEffect : ShaderEffect
{
    private const int MaxRipples = 4;

    private FrameworkElement? _element;
    private DateTime _startTime;
    private int _rippleIndex;
    private double _lastRippleTime = -10.0;
    private bool _rendering;

    private double Duration => Params.W;

    partial void OnConstructed()
    {
        // 4 个槽初始置于过去时刻，使 age > duration，避免未点击时出现杂波
        Ripple0 = Ripple1 = Ripple2 = Ripple3 = new Vector3D(0.0, 0.0, -10.0);
    }

    #region Attach / Detach

    public void Attach(FrameworkElement element)
    {
        _element = element;
        _startTime = DateTime.Now;
        element.Effect = this;
        UpdateAspectRatio();
        element.MouseLeftButtonDown += OnMouseDown;
        element.SizeChanged += OnSizeChanged;
    }

    public void Detach()
    {
        StopRendering();
        if (_element != null)
        {
            _element.MouseLeftButtonDown -= OnMouseDown;
            _element.SizeChanged -= OnSizeChanged;
            _element.Effect = null;
            _element = null;
        }
    }

    #endregion

    #region Interaction

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_element == null) return;
        var pos = e.GetPosition(_element);
        double w = _element.ActualWidth, h = _element.ActualHeight;
        if (w <= 0 || h <= 0) return;

        double t = GetCurrentTime();
        _lastRippleTime = t;
        // 环形写入下一个槽位：连点时旧涟漪继续存活，最多并存 4 圈
        SetRipple(_rippleIndex % MaxRipples, new Vector3D(pos.X / w, pos.Y / h, t));
        _rippleIndex++;
        StartRendering();
    }

    // 按下标写入对应槽位（直接用生成的公共属性，避免跨 partial 静态字段初始化顺序问题）
    private void SetRipple(int slot, Vector3D value)
    {
        switch (slot)
        {
            case 0: Ripple0 = value; break;
            case 1: Ripple1 = value; break;
            case 2: Ripple2 = value; break;
            case 3: Ripple3 = value; break;
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => UpdateAspectRatio();

    private void UpdateAspectRatio()
    {
        if (_element == null) return;
        double w = _element.ActualWidth, h = _element.ActualHeight;
        if (w > 0 && h > 0)
            AspectRatio = w / h;
    }

    #endregion

    #region Animation

    private double GetCurrentTime() => (DateTime.Now - _startTime).TotalSeconds;

    private void StartRendering()
    {
        if (_rendering) return;
        _rendering = true;
        CompositionTarget.Rendering += OnRendering;
    }

    private void StopRendering()
    {
        if (!_rendering) return;
        _rendering = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        double t = GetCurrentTime();
        Time = t;
        // 最后一次涟漪也播放完毕后停帧，避免持续占用渲染回调
        if (t - _lastRippleTime > Duration)
            StopRendering();
    }

    #endregion
}
