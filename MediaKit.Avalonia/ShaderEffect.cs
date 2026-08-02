using System.ComponentModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Media;

namespace MediaKit.Avalonia.Effects;

/// <summary>
/// 以目标控件自身的渲染结果为输入的后置加工着色器（模糊、波纹、色彩变换一类）。
/// <para>
/// 相对 <see cref="ShaderPainter"/> 只多一件事：attach 时为目标开启位图缓存，好让
/// <c>@surface</c> 快照拿到干净的像素。取表面这件事本身由 sksl 里的 <c>// @surface</c>
/// 标记触发，不靠本类。
/// </para>
/// </summary>
public class ShaderEffect : ShaderPainter
{
    private bool _ownsCacheMode;

    protected ShaderEffect()
    {
    }

    public ShaderEffect(Uri source) : base(source)
    {
    }

    internal override void OnTargetAttached(Control target)
    {
        // 开启位图缓存：lease 出的 SKSurface 变为「目标子树的隔离离屏 layer」，
        // 取到的是父级合成之前、alpha 正确的像素，避免半透明目标串入父级颜色。
        if (target.CacheMode != null) return;

        target.CacheMode = new BitmapCache { EnableClearType = true, SnapsToDevicePixels = true };
        _ownsCacheMode = true;
        // CacheModeProperty 不在 AffectsRender 列表内，需主动置脏以触发合成属性同步
        target.InvalidateVisual();
    }

    internal override void OnTargetDetached(Control target)
    {
        // 仅还原本组件设置的 CacheMode，不影响用户原有缓存策略
        if (!_ownsCacheMode) return;

        _ownsCacheMode = false;
        target.CacheMode = null;
        target.InvalidateVisual();
    }
}

/// <summary>
/// 效果描述符，持有工厂方法，通过 Create() 生成实例。
/// </summary>
public sealed class EffectDescriptor
{
    public string Name { get; }
    internal Func<ShaderPainter> Factory { get; }
    internal bool Animate { get; }

    public EffectDescriptor(string name, Func<ShaderPainter> factory, bool animate = false)
    {
        Name = name;
        Factory = factory;
        Animate = animate;
    }

    public ShaderPainter Create()
    {
        var effect = Factory();
        if (Animate) effect._pendingAnimate = true;
        return effect;
    }

    public override string ToString() => Name;
}

/// <summary>
/// 标记着色器效果子类在 XAML 中的可用名称。
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class EffectNameAttribute : Attribute
{
    public string Name { get; }
    public EffectNameAttribute(string name) => Name = name;
}

public class ShaderEffectConverter : TypeConverter
{
    private static readonly Dictionary<string, EffectDescriptor> _registry = new(StringComparer.OrdinalIgnoreCase);

    public static void Register(EffectDescriptor descriptor)
    {
        _registry[descriptor.Name] = descriptor;
    }

    public static IReadOnlyList<string> Names => _registry.Keys.ToList();

    public static ShaderPainter? Create(string name)
    {
        if (_registry.TryGetValue(name, out var descriptor))
            return descriptor.Create();
        return null;
    }

    public override bool CanConvertFrom(ITypeDescriptorContext? ctx, Type sourceType)
        => sourceType == typeof(string) || base.CanConvertFrom(ctx, sourceType);

    public override object? ConvertFrom(ITypeDescriptorContext? ctx, CultureInfo? culture, object value)
    {
        if (value is string s)
        {
            s = s.Trim();

            if (_registry.TryGetValue(s, out var descriptor))
                return descriptor.Create();

            // 未注册的任意来源统一按自生成处理。着色器里的 uniform shader 若没标来源，
            // 或标了 @texture 却没图，Renderer 构造时会给出明确诊断，而不是静默黑屏。
            if (Uri.TryCreate(s, UriKind.Absolute, out var uri))
                return new ShaderPainter(uri);
            if (Path.IsPathRooted(s) && File.Exists(s))
                return new ShaderPainter(new Uri(s));
        }
        return base.ConvertFrom(ctx, culture, value);
    }

    public override bool GetStandardValuesSupported(ITypeDescriptorContext? ctx) => true;
    public override bool GetStandardValuesExclusive(ITypeDescriptorContext? ctx) => false;

    public override StandardValuesCollection? GetStandardValues(ITypeDescriptorContext? ctx)
    {
        return new(_registry.Keys.ToArray());
    }
}
