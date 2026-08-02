using MediaKit.Avalonia.Effects;

namespace MediaKit.Avalonia.Demo;

/// <summary>
/// 反色效果，用于验证 CacheMode 隔离层是否修复了半透明目标的串色问题。
/// 与 demo-cachemode 里的探测着色器同一套算法：预乘 alpha 下反色，强度由 amount 线性控制。
/// amount=0 时应与原始画面完全一致（恒等），amount=1 时完全反色。
/// </summary>
public sealed class InvertEffect : ShaderEffect
{
    private const string Sksl = """
        // @surface
        uniform shader iImage;
        uniform float amount;

        half4 main(float2 fragCoord) {
            half4 c = iImage.eval(fragCoord);
            half3 inv = half3(c.a) - c.rgb;   // 预乘 alpha 下的反色
            half3 outc = mix(c.rgb, inv, amount);
            return half4(outc, c.a);          // 保留 alpha
        }
        """;

    private double _amount = 1d;

    protected override string ProvideSksl() => Sksl;

    protected override void CollectUniforms(Dictionary<string, object> sink)
    {
        base.CollectUniforms(sink);
        sink["amount"] = (float)_amount;
    }

    public double Amount
    {
        get => _amount;
        set
        {
            _amount = value;
            SetUniform("amount", (float)value);
        }
    }
}
