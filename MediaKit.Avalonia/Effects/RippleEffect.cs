using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace MediaKit.Avalonia.Effects;

/// <summary>
/// 新增一处波纹。UI 线程 → 合成线程。起始时间不在这里带上，由渲染侧用自己的时钟填，
/// 与 <c>iTime</c> 保证同源。
/// </summary>
internal sealed record AddRippleMessage(float X, float Y);

public partial class RippleEffect
{
    /// <param name="normalizedPosition">波纹中心，取值 0~1 的归一化坐标。</param>
    public void AddRipple(Point normalizedPosition)
    {
        StartAnimation();
        SendMessage(new AddRippleMessage((float)normalizedPosition.X, (float)normalizedPosition.Y));
    }

    internal override ShaderRenderer CreateRenderer(string sksl, Dictionary<string, object> uniforms,
        Dictionary<string, Uri?> textures)
        => new RippleRenderer(this, sksl, uniforms, textures);

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

/// <summary>
/// 维护有限个波纹槽位：每处波纹占一个槽，循环覆盖最旧的那个；
/// 全部波纹都超过存活时长后结束动画。
/// <para>
/// 槽位存在 <c>iRipple0..2</c> 三个 <c>float3x3</c> 里，一列一处波纹，共 9 圈并存。
/// 波纹在后 1/3 寿命里已几乎不可见（timeFade 线性归零、波前又在出画），
/// 再多的槽只是在为隐形波纹付渲染成本。
/// </para>
/// </summary>
internal sealed class RippleRenderer : ShaderRenderer
{
    /// <summary>列主序的 <c>float3x3</c>，每个 uniform 装 3 列。</summary>
    private const int SlotsPerUniform = 3;

    private static readonly string[] UniformNames = { "iRipple0", "iRipple1", "iRipple2" };

    private static readonly int MaxRipples = SlotsPerUniform * UniformNames.Length;

    /// <summary>空槽的起始时间，早于任何真实波纹，与 Ripple.sksl 的注释一致。</summary>
    private const float NoRipple = -10f;

    /// <summary>每个 uniform 一份常驻缓冲，9 个 float 整块推送。</summary>
    private readonly float[][] _slots = new float[UniformNames.Length][];

    private int _rippleIndex;

    private float _lastRippleTime = NoRipple;

    private float _duration = 3f;

    internal RippleRenderer(ShaderPainter owner, string sksl, Dictionary<string, object> uniforms,
        Dictionary<string, Uri?> textures)
        : base(owner, sksl, uniforms, textures)
    {
        if (uniforms.TryGetValue("duration", out var d) && d is float f)
            _duration = f;

        // 不写的 uniform 是全零，startTime=0 会让全部空槽在 iTime≈0 时被当成刚触发的波纹
        for (var u = 0; u < _slots.Length; u++)
        {
            var m = new float[SlotsPerUniform * 3];
            for (var c = 0; c < SlotsPerUniform; c++)
                m[c * 3 + 2] = NoRipple;
            _slots[u] = m;
            SetUniform(UniformNames[u], m);
        }
    }

    public override void OnMessage(object message)
    {
        switch (message)
        {
            case AddRippleMessage r:
                AddRipple(r.X, r.Y);
                return;

            // 存活时长由本类判断动画何时结束，所以要跟着属性走；仍需交给 base 写入 uniform
            case SetUniformMessage { Name: "duration", Value: float d }:
                _duration = d;
                break;
        }

        base.OnMessage(message);
    }

    private void AddRipple(float x, float y)
    {
        var time = (float)Elapsed;
        _lastRippleTime = time;

        var u = _rippleIndex / SlotsPerUniform;
        var column = _rippleIndex % SlotsPerUniform;
        _rippleIndex = (_rippleIndex + 1) % MaxRipples;

        var m = _slots[u];
        m[column * 3] = x;
        m[column * 3 + 1] = y;
        m[column * 3 + 2] = time;
        SetUniform(UniformNames[u], m);
        Invalidate();
    }

    protected override bool OnFrameUpdate(double elapsed)
        => elapsed - _lastRippleTime < _duration;
}
