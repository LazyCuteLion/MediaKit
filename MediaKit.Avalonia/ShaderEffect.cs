using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.VisualTree;

namespace MediaKit.Avalonia.Effects;

/// <summary>
/// 基于 SkSL 的着色器效果。独立绘制整个目标区域，或以目标控件自身的渲染结果为输入做后置加工。
/// <para>
/// 本类只负责 UI 线程一侧：属性、生命周期、把变更送往合成线程。着色器的编译产物、uniform 值
/// 与全部 GPU 资源都归 <see cref="ShaderRenderer"/> 独占，两侧没有共享可变状态
/// （所以本文件不引用 SkiaSharp）。
/// </para>
/// SkSL 着色器约定以下名称（可不声明）：
///   uniform float2 iResolution;  // 视口设备像素尺寸
///   uniform float  iTime;        // 动画时间（秒）
/// <para>
/// 每个 <c>uniform shader</c> 都要在声明处标注来源（<c>// @surface</c> 或 <c>// @texture</c>），
/// 纹理的像素尺寸走伴生 uniform <c>&lt;名字&gt;Size</c>，详见 Shaders/README.md。
/// </para>
/// </summary>
[TypeConverter(typeof(ShaderEffectConverter))]
public class ShaderEffect : AvaloniaObject, IEffect
{
    #region Static: EffectProperty subscription + Registry

    static ShaderEffect()
    {
        ShaderEffectPatch.Apply();
        Visual.EffectProperty.Changed.AddClassHandler<Control>(OnEffectChanged);
    }

    private static void OnEffectChanged(Control target, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.OldValue is ShaderEffect oldEffect)
            oldEffect.Detach();
        if (e.NewValue is ShaderEffect newEffect)
            newEffect.Attach(target);
    }

    private static readonly Dictionary<string, EffectDescriptor> _registry = new(StringComparer.OrdinalIgnoreCase);

    public static void Register(EffectDescriptor descriptor)
    {
        _registry[descriptor.Name] = descriptor;
    }

    public static IReadOnlyList<string> Names => _registry.Keys.ToList();

    public static ShaderEffect? Create(string name)
    {
        if (_registry.TryGetValue(name, out var descriptor))
            return descriptor.Create();
        return null;
    }

    #endregion



    private CompositionCustomVisual? _customVisual;

    /// <summary>UI 侧的动画意图。合成线程的实际帧回调状态由 <see cref="ShaderRenderer"/> 自己维护。</summary>
    private bool _isAnimating;

    internal bool _pendingAnimate;

    /// <summary>本组件是否为目标设置了 CacheMode，detach 时还原。</summary>
    private bool _ownsCacheMode;

#if DEBUG
    private string[]? _uniformNames;
    private string[]? _textureNames;
#endif

    public Control? Target { get; private set; }

    /// <summary>着色器来源，同时用于诊断信息。内联源码的子类为 <c>memory:///inline.sksl</c>。</summary>
    public Uri Source { get; }

    /// <summary>供内联源码的子类使用，<see cref="ProvideSksl"/> 必须被重写。</summary>
    protected ShaderEffect()
    {
        Source = new Uri("memory:///inline.sksl");
    }

    /// <remarks>
    /// 只记下来源，不读文件、不编译。编译推迟到 attach 时在 <see cref="ShaderRenderer"/> 构造函数里完成，
    /// 因此纯粹的对象构造（XAML 解析、描述符工厂）不再需要资源系统就绪。
    /// </remarks>
    public ShaderEffect(Uri source)
    {
        Source = source;
    }

    #region Subclass extension points

    /// <summary>
    /// 提供 SkSL 源码，在 UI 线程于 attach 时调用一次。内联源码的子类重写本方法。
    /// <para><b>不要</b>在构造函数里调用：子类字段此时尚未初始化。</para>
    /// </summary>
    protected virtual string ProvideSksl() => LoadSkslFromUri(Source);

    /// <summary>
    /// 收集 attach 时的初始 uniform 值。子类把自己属性字段的当前值写入 <paramref name="sink"/>，
    /// 这是 UI 侧属性向新建 Renderer 交接初值的唯一途径。
    /// </summary>
    protected virtual void CollectUniforms(Dictionary<string, object> sink) { }

    /// <summary>
    /// 收集 attach 时各 <c>@texture</c> 槽的图片来源，键是 uniform 名。与 <see cref="CollectUniforms"/>
    /// 分开是因为纹理不走 uniform 通道：它在渲染侧要加载图片、管 GPU 生命周期。
    /// </summary>
    protected virtual void CollectTextures(Dictionary<string, Uri?> sink) { }

    /// <summary>创建渲染器。子类借此换用带自定义帧逻辑的渲染器。</summary>
    internal virtual ShaderRenderer CreateRenderer(string sksl, Dictionary<string, object> uniforms,
        Dictionary<string, Uri?> textures)
        => new(this, sksl, uniforms, textures);

    /// <summary>目标控件挂载完成。子类在此订阅目标的事件。</summary>
    protected virtual void OnAttached(Control target) { }

    /// <summary>即将从目标控件卸下。此时 <see cref="Target"/> 仍然可用。</summary>
    protected virtual void OnDetaching() { }

    #endregion

    #region Uniforms

    /// <summary>
    /// 推送单个 uniform 到合成线程。未 attach 时丢弃——此时真值在子类的属性字段里，
    /// attach 时经 <see cref="CollectUniforms"/> 一次性交接。
    /// </summary>
    protected void SetUniform(string name, object value)
    {
        var visual = _customVisual;
        if (visual == null) return;
#if DEBUG
        ValidateUniformName(name);
#endif
        visual.SendHandlerMessage(new SetUniformMessage(name, value));
    }

    /// <summary>
    /// 推送某个 <c>@texture</c> 槽的新图片。未 attach 时丢弃——此时真值在子类的属性字段里，
    /// attach 时经 <see cref="CollectTextures"/> 一次性交接。
    /// </summary>
    protected void SetTexture(string name, Uri? source)
    {
        var visual = _customVisual;
        if (visual == null) return;
#if DEBUG
        ValidateTextureName(name);
#endif
        visual.SendHandlerMessage(new SetTextureMessage(name, source));
    }

    /// <summary>向渲染器投递子类自定义的消息。未 attach 时丢弃。</summary>
    protected void SendMessage(object message) => _customVisual?.SendHandlerMessage(message);

#if DEBUG
    /// <summary>
    /// 错名会在 Renderer 侧被静默丢弃，表现为「属性设了但画面没反应」。DEBUG 下就地抛出，
    /// 让调用栈直指写错名字的那个 setter。
    /// </summary>
    private void ValidateUniformName(string name)
    {
        if (_uniformNames == null) return;
        if (Array.IndexOf(_uniformNames, name) >= 0) return;
        throw new ArgumentException(
            $"Uniform '{name}' is not declared in shader '{Source}'. " +
            $"Declared: {string.Join(", ", _uniformNames)}");
    }

    private void ValidateTextureName(string name)
    {
        if (_textureNames == null) return;
        if (Array.IndexOf(_textureNames, name) >= 0) return;
        throw new ArgumentException(
            $"'{name}' is not a texture slot of shader '{Source}'. " +
            $"Declared: {string.Join(", ", _textureNames)}");
    }
#endif

    #endregion

    #region Animation

    public void StartAnimation()
    {
        _isAnimating = true;
        _customVisual?.SendHandlerMessage(ShaderRenderer.StartMessage);
    }

    /// <summary>请求重绘一帧。</summary>
    public void Invalidate()
    {
        _customVisual?.SendHandlerMessage(ShaderRenderer.InvalidateMessage);
    }

    /// <summary>由 <see cref="ShaderRenderer"/> 经 UI 线程回调，通知动画已自然结束。</summary>
    internal void NotifyAnimationStopped() => _isAnimating = false;

    #endregion

    #region Lifecycle

    public void Attach(Control target)
    {
        Target = target;

        // 开启位图缓存：lease 出的 SKSurface 变为「目标子树的隔离离屏 layer」，
        // 取到的是父级合成之前、alpha 正确的像素，避免半透明目标串入父级颜色。
        if (target.CacheMode == null)
        {
            target.CacheMode = new BitmapCache { EnableClearType = true, SnapsToDevicePixels = true };
            _ownsCacheMode = true;
            // CacheModeProperty 不在 AffectsRender 列表内，需主动置脏以触发合成属性同步
            target.InvalidateVisual();
        }

        if (TopLevel.GetTopLevel(target) != null)
            AttachCompositor();
        else
            target.AttachedToVisualTree += OnAttachedToVisualTree;

        target.DetachedFromVisualTree += OnDetachedFromVisualTree;
        OnAttached(target);
    }

    public void Detach()
    {
        if (Target == null) return;

        OnDetaching();
        Target.AttachedToVisualTree -= OnAttachedToVisualTree;
        Target.DetachedFromVisualTree -= OnDetachedFromVisualTree;
        DetachCompositor();

        // 仅还原本组件设置的 CacheMode，不影响用户原有缓存策略
        if (_ownsCacheMode)
        {
            _ownsCacheMode = false;
            Target.CacheMode = null;
            Target.InvalidateVisual();
        }

        Target = null;
    }

    private void AttachCompositor()
    {
        if (Target == null || _customVisual != null) return;
        var targetVisual = ElementComposition.GetElementVisual(Target);
        if (targetVisual == null) return;

        // 在创建 Visual 前激活动画意图，好让首帧就带上 StartMessage
        if (_pendingAnimate)
        {
            _pendingAnimate = false;
            _isAnimating = true;
        }

        var uniforms = new Dictionary<string, object>(StringComparer.Ordinal);
        CollectUniforms(uniforms);

        var textures = new Dictionary<string, Uri?>(StringComparer.Ordinal);
        CollectTextures(textures);

        // 编译在这里发生：失败的异常直接冒泡到 attach 调用点
        var renderer = CreateRenderer(ProvideSksl(), uniforms, textures);
#if DEBUG
        _uniformNames = renderer.GetUniformNamesSnapshot();
        _textureNames = renderer.GetTextureNamesSnapshot();
#endif

        _customVisual = targetVisual.Compositor.CreateCustomVisual(renderer);
        UpdateVisualSize();
        ElementComposition.SetElementChildVisual(Target, _customVisual);

        if (_isAnimating)
            _customVisual.SendHandlerMessage(ShaderRenderer.StartMessage);

        Target.SizeChanged += OnTargetSizeChanged;
    }

    private void DetachCompositor()
    {
        if (Target == null) return;

        _isAnimating = false;
        Target.SizeChanged -= OnTargetSizeChanged;

        if (_customVisual != null)
        {
            // 先让 Renderer 在合成线程释放自己的资源，再摘掉 visual
            _customVisual.SendHandlerMessage(ShaderRenderer.DisposeMessage);
            ElementComposition.SetElementChildVisual(Target, null);
            _customVisual = null;
        }
#if DEBUG
        _uniformNames = null;
        _textureNames = null;
#endif
    }

    private void OnAttachedToVisualTree(object? s, VisualTreeAttachmentEventArgs e) => AttachCompositor();

    private void OnDetachedFromVisualTree(object? s, VisualTreeAttachmentEventArgs e) => DetachCompositor();

    private void OnTargetSizeChanged(object? sender, SizeChangedEventArgs e) => UpdateVisualSize();

    private void UpdateVisualSize()
    {
        if (_customVisual == null || Target == null) return;
        _customVisual.Size = new Vector(Target.Bounds.Width, Target.Bounds.Height);
    }

    #endregion

    protected static string LoadSkslFromUri(Uri source)
    {
        if (source.Scheme == "avares")
        {
            using var stream = AssetLoader.Open(source);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        string path;
        if (source.Scheme == "file")
            path = source.LocalPath;
        else if (Path.IsPathRooted(source.OriginalString))
            path = source.OriginalString;
        else
            throw new InvalidOperationException(
                $"Unsupported URI scheme '{source.Scheme}'. Use avares:// or a local file path.");

        if (!File.Exists(path))
            throw new FileNotFoundException($"Shader file not found: {path}");

        return File.ReadAllText(path);
    }
}

/// <summary>
/// 效果描述符，持有工厂方法，通过 Create() 生成实例。
/// </summary>
public sealed class EffectDescriptor
{
    public string Name { get; }
    internal Func<ShaderEffect> Factory { get; }
    internal bool Animate { get; }

    public EffectDescriptor(string name, Func<ShaderEffect> factory, bool animate = false)
    {
        Name = name;
        Factory = factory;
        Animate = animate;
    }

    public ShaderEffect Create()
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
    public override bool CanConvertFrom(ITypeDescriptorContext? ctx, Type sourceType)
        => sourceType == typeof(string) || base.CanConvertFrom(ctx, sourceType);

    public override object? ConvertFrom(ITypeDescriptorContext? ctx, CultureInfo? culture, object value)
    {
        if (value is string s)
        {
            s = s.Trim();

            var effect = ShaderEffect.Create(s);
            if (effect != null)
                return effect;

            // 未注册的任意来源统一按自生成处理。着色器里的 uniform shader 若没标来源，
            // 或标了 @texture 却没图，Renderer 构造时会给出明确诊断，而不是静默黑屏。
            if (Uri.TryCreate(s, UriKind.Absolute, out var uri))
                return new ShaderEffect(uri);
            if (Path.IsPathRooted(s) && File.Exists(s))
                return new ShaderEffect(new Uri(s));
        }
        return base.ConvertFrom(ctx, culture, value);
    }

    public override bool GetStandardValuesSupported(ITypeDescriptorContext? ctx) => true;
    public override bool GetStandardValuesExclusive(ITypeDescriptorContext? ctx) => false;

    public override StandardValuesCollection? GetStandardValues(ITypeDescriptorContext? ctx)
    {
        return new(ShaderEffect.Names.ToArray());
    }
}
