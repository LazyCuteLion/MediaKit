using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace MediaKit.ShaderEffectGenerators;

/// <summary>
/// WPF（HLSL）着色器效果生成器。读取 <c>.fx</c>，为标记了 <c>// @property</c> 的常量寄存器
/// 1:1 生成公共 <c>DependencyProperty</c>（直接携带 <c>PixelShaderConstantCallback</c>），
/// 并生成 <c>_shader</c>、<c>Input</c> 采样器 DP、<c>Target</c> 宿主绑定 DP、构造函数与配套 MarkupExtension。
/// <para>支持两种效果标记：</para>
/// <list type="bullet">
/// <item><c>// @effect</c>：生成 <c>partial</c> 类，Attach/Detach 与 OnConstructed 由手写 partial 提供，适合有交互/计算的效果。</item>
/// <item><c>// @register</c>：生成自包含 <c>sealed</c> 类，内联 Attach/Detach、无 OnConstructed 钩子、无需手写 partial，适合无交互的纯参数着色器。</item>
/// </list>
/// 未标记寄存器（打包 / 运行时计算 / 枚举映射）由手写 partial 负责（仅 @effect）。
/// </summary>
internal static class WpfEmitter
{
    // 消费方程序集名（WPF 库固定为 MediaKit.WPF）
    private const string AssemblyName = "MediaKit.WPF";

    private static readonly DiagnosticDescriptor DuplicateEffectName = new(
        "WPFSHADER001",
        "重复的效果名称",
        "效果名称 '{0}' 在多个 .fx 文件中重复定义：{1}",
        "ShaderEffect",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static void Emit(SourceProductionContext ctx, List<AdditionalText> files)
    {
        var effects = new List<EffectModel>();

        foreach (var file in files)
        {
            var text = file.GetText(ctx.CancellationToken)?.ToString();
            if (string.IsNullOrEmpty(text)) continue;

            var fileName = Path.GetFileNameWithoutExtension(file.Path);
            var effect = ParseFx(text!, fileName);
            if (effect != null)
                effects.Add(effect);
        }

        // 检测重复类名
        var nameToFiles = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in effects)
        {
            if (!nameToFiles.TryGetValue(e.ClassName, out var list))
                nameToFiles[e.ClassName] = list = new List<string>();
            list.Add(e.FileName + ".fx");
        }

        bool hasDuplicate = false;
        foreach (var kv in nameToFiles)
        {
            if (kv.Value.Count > 1)
            {
                ctx.ReportDiagnostic(Diagnostic.Create(
                    DuplicateEffectName, Location.None, kv.Key, string.Join(", ", kv.Value)));
                hasDuplicate = true;
            }
        }
        if (hasDuplicate) return;

        foreach (var e in effects)
            ctx.AddSource($"{e.ClassName}.g.cs", SourceText.From(GenerateEffectClass(e), Encoding.UTF8));
    }

    #region Parsing

    // 常量寄存器行： float4 params : register(C0) ...
    private static readonly Regex ConstRegisterRegex = new(
        @"^(float[234]?)\s+(\w+)\s*:\s*register\s*\(\s*[cC](\d+)\s*\)",
        RegexOptions.Compiled);

    // 采样器行： sampler2D input : register(S0);
    private static readonly Regex SamplerRegex = new(
        @"^sampler2D\s+(\w+)\s*:\s*register\s*\(\s*[sS](\d+)\s*\)",
        RegexOptions.Compiled);

    private static EffectModel? ParseFx(string text, string fileName)
    {
        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.None);

        string? effectName = null;
        bool isRegister = false;
        bool isAnimated = false;
        int? samplerIndex = null;
        int? timeRegister = null;
        var properties = new List<PropertyModel>();
        string? pendingMarker = null;
        bool pendingProperty = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("// @effect"))
            {
                var val = ExtractMarkerValue(line, "@effect");
                if (string.Equals(val, "default", StringComparison.OrdinalIgnoreCase))
                {
                    // @effect: default → 仅注册（自包含 sealed 类，无需手写 partial）
                    isRegister = true;
                    effectName = Capitalize(fileName) + "Effect";
                }
                else
                {
                    // @effect / @effect: Name → 生成 partial 类（配手写 partial）
                    isRegister = false;
                    effectName = val ?? (Capitalize(fileName) + "Effect");
                }
                continue;
            }
            if (line.StartsWith("// @animate"))
            {
                isAnimated = true;
                continue;
            }
            if (line.StartsWith("// @property"))
            {
                pendingProperty = true;
                pendingMarker = ExtractMarkerValue(line, "@property");
                continue;
            }

            // 其它注释行不打断 pending 标记（允许标记与寄存器行之间存在注释）
            if (line.StartsWith("//")) continue;

            // 采样器行
            var sm = SamplerRegex.Match(line);
            if (sm.Success)
            {
                if (samplerIndex == null)
                    samplerIndex = int.Parse(sm.Groups[2].Value);
                pendingProperty = false;
                pendingMarker = null;
                continue;
            }

            // 常量寄存器行
            var rm = ConstRegisterRegex.Match(line);
            if (rm.Success)
            {
                // 保留名 time：由 @animate 驱动，不生成公共属性
                if (string.Equals(rm.Groups[2].Value, "time", StringComparison.OrdinalIgnoreCase))
                {
                    timeRegister = int.Parse(rm.Groups[3].Value);
                    pendingProperty = false;
                    pendingMarker = null;
                    continue;
                }
                if (pendingProperty)
                {
                    var registerDefault = ExtractRegisterDefault(line);
                    var parsed = ParseProperty(pendingMarker, rm.Groups[1].Value, rm.Groups[2].Value,
                        int.Parse(rm.Groups[3].Value), registerDefault);
                    if (parsed != null)
                        properties.Add(parsed);
                }
                pendingProperty = false;
                pendingMarker = null;
                continue;
            }

            // 遇到其它代码行，丢弃悬挂标记
            pendingProperty = false;
            pendingMarker = null;
        }

        if (effectName == null) return null;

        return new EffectModel
        {
            ClassName = effectName,
            FileName = fileName,
            SamplerIndex = samplerIndex,
            Properties = properties,
            IsRegister = isRegister,
            IsAnimated = isAnimated,
            TimeRegister = timeRegister
        };
    }

    /// <summary>
    /// 解析 <c>// @property[: PropName] [: ClrType] [= Default]</c>。
    /// 裸标记（无 PropName）时属性名由 HLSL 变量名派生（首字母大写）；
    /// 未显式给默认值时回退到寄存器行的初始化式。
    /// </summary>
    private static PropertyModel? ParseProperty(string? marker, string hlslType, string varName, int register, string? registerDefault)
    {
        string? defaultValue = null;
        string? clrType = null;
        string? propName = null;

        if (!string.IsNullOrEmpty(marker))
        {
            var body = marker!;

            var eqIdx = body.IndexOf('=');
            if (eqIdx >= 0)
            {
                defaultValue = body.Substring(eqIdx + 1).Trim();
                body = body.Substring(0, eqIdx).Trim();
            }

            var colonIdx = body.IndexOf(':');
            if (colonIdx >= 0)
            {
                clrType = body.Substring(colonIdx + 1).Trim();
                body = body.Substring(0, colonIdx).Trim();
            }

            propName = body.Trim();
        }

        // 属性名：显式指定 > 变量名派生（首字母大写）
        if (string.IsNullOrEmpty(propName))
            propName = Capitalize(varName);
        if (string.IsNullOrEmpty(propName)) return null;

        // 类型：显式指定 > HLSL 映射
        if (string.IsNullOrEmpty(clrType))
            clrType = MapHlslToClr(hlslType);

        // 默认值：标记显式 > 寄存器行初始化式
        if (string.IsNullOrEmpty(defaultValue))
            defaultValue = registerDefault;

        return new PropertyModel
        {
            PropName = propName!,
            ClrType = clrType!,
            Register = register,
            DefaultValue = string.IsNullOrEmpty(defaultValue) ? null : defaultValue
        };
    }

    /// <summary>提取寄存器行 <c>= ...</c> 之后、<c>;</c> 之前的初始化式作为默认值。</summary>
    private static string? ExtractRegisterDefault(string line)
    {
        var eq = line.IndexOf('=');
        if (eq < 0) return null;
        var rhs = line.Substring(eq + 1);
        var semi = rhs.IndexOf(';');
        if (semi >= 0) rhs = rhs.Substring(0, semi);
        rhs = rhs.Trim();
        return rhs.Length > 0 ? rhs : null;
    }

    private static string Capitalize(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        return char.ToUpperInvariant(s[0]) + s.Substring(1);
    }

    private static string? ExtractMarkerValue(string line, string marker)
    {
        var idx = line.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return null;
        var after = line.Substring(idx + marker.Length).Trim();
        if (after.Length == 0) return null;
        if (after[0] == ':')
        {
            var val = after.Substring(1).Trim();
            return val.Length > 0 ? val : null;
        }
        return null;
    }

    private static string MapHlslToClr(string hlslType)
    {
        return hlslType switch
        {
            "float" => "double",
            "float2" => "Point",
            "float3" => "Vector3D",
            "float4" => "Point4D",
            _ => "double"
        };
    }

    #endregion

    #region Code Generation

    private static string GenerateEffectClass(EffectModel model)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Windows;");
        sb.AppendLine("using System.Windows.Markup;");
        sb.AppendLine("using System.Windows.Media;");
        sb.AppendLine("using System.Windows.Media.Effects;");
        sb.AppendLine("using System.Windows.Media.Media3D;");
        sb.AppendLine();
        sb.AppendLine("namespace MediaKit.WPF.Effects;");
        sb.AppendLine();
        sb.AppendLine($"public {(model.IsRegister ? "sealed" : "partial")} class {model.ClassName} : ShaderEffect");
        sb.AppendLine("{");
        sb.AppendLine($"    private static readonly PixelShader _shader = new()");
        sb.AppendLine("    {");
        sb.AppendLine($"        UriSource = new Uri(\"pack://application:,,,/{AssemblyName};component/Shaders/Compiled/{model.FileName}.ps\")");
        sb.AppendLine("    };");
        sb.AppendLine();

        // Input 采样器 DP
        if (model.SamplerIndex != null)
        {
            sb.AppendLine($"    private static readonly DependencyProperty InputProperty =");
            sb.AppendLine($"        RegisterPixelShaderSamplerProperty(\"Input\", typeof({model.ClassName}), {model.SamplerIndex});");
            sb.AppendLine();
        }

        // 标记寄存器 → 公共 DP
        foreach (var prop in model.Properties)
        {
            var defaultExpr = GetDefaultExpression(prop.ClrType, prop.DefaultValue);
            sb.AppendLine($"    public static readonly DependencyProperty {prop.PropName}Property =");
            sb.AppendLine($"        DependencyProperty.Register(nameof({prop.PropName}), typeof({prop.ClrType}), typeof({model.ClassName}),");
            sb.AppendLine($"            new UIPropertyMetadata({defaultExpr}, PixelShaderConstantCallback({prop.Register})));");
            sb.AppendLine($"    public {prop.ClrType} {prop.PropName} {{ get => ({prop.ClrType})GetValue({prop.PropName}Property); set => SetValue({prop.PropName}Property, value); }}");
            sb.AppendLine();
        }

        // time 寄存器 → 由 @animate 驱动的公共 DP（外部亦可手动设定）
        if (model.TimeRegister != null)
        {
            sb.AppendLine($"    public static readonly DependencyProperty TimeProperty =");
            sb.AppendLine($"        DependencyProperty.Register(nameof(Time), typeof(double), typeof({model.ClassName}),");
            sb.AppendLine($"            new UIPropertyMetadata(0.0, PixelShaderConstantCallback({model.TimeRegister})));");
            sb.AppendLine($"    public double Time {{ get => (double)GetValue(TimeProperty); set => SetValue(TimeProperty, value); }}");
            sb.AppendLine();
        }

        // Target 宿主绑定 DP
        sb.AppendLine("    /// <summary>宿主控件。绑定后随元素 Loaded/Unloaded 自动 Attach/Detach（重新父化亦正确恢复），置空或换绑时同步清理。</summary>");
        sb.AppendLine($"    public static readonly DependencyProperty TargetProperty =");
        sb.AppendLine($"        DependencyProperty.Register(nameof(Target), typeof(FrameworkElement), typeof({model.ClassName}),");
        sb.AppendLine("            new PropertyMetadata(null, OnTargetChanged));");
        sb.AppendLine();
        sb.AppendLine("    public FrameworkElement? Target");
        sb.AppendLine("    {");
        sb.AppendLine("        get => (FrameworkElement?)GetValue(TargetProperty);");
        sb.AppendLine("        set => SetValue(TargetProperty, value);");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    private bool _isAttached;");
        sb.AppendLine();
        sb.AppendLine("    private static void OnTargetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)");
        sb.AppendLine("    {");
        sb.AppendLine($"        var fx = ({model.ClassName})d;");
        sb.AppendLine("        if (e.OldValue is FrameworkElement oldFe)");
        sb.AppendLine("        {");
        sb.AppendLine("            oldFe.Loaded -= fx.OnTargetLoaded;");
        sb.AppendLine("            oldFe.Unloaded -= fx.OnTargetUnloaded;");
        sb.AppendLine("            fx.DetachCore();");
        sb.AppendLine("        }");
        sb.AppendLine("        if (e.NewValue is FrameworkElement fe)");
        sb.AppendLine("        {");
        sb.AppendLine("            fe.Loaded += fx.OnTargetLoaded;");
        sb.AppendLine("            fe.Unloaded += fx.OnTargetUnloaded;");
        sb.AppendLine("            if (fe.IsLoaded) fx.AttachCore(fe);");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    private void OnTargetLoaded(object sender, RoutedEventArgs e) => AttachCore((FrameworkElement)sender);");
        sb.AppendLine();
        sb.AppendLine("    private void OnTargetUnloaded(object sender, RoutedEventArgs e) => DetachCore();");
        sb.AppendLine();
        sb.AppendLine("    // 幂等包装：元素因重新父化反复触发 Loaded/Unloaded 时，Attach/Detach 仅在状态翻转时各执行一次");
        sb.AppendLine("    private void AttachCore(FrameworkElement fe)");
        sb.AppendLine("    {");
        sb.AppendLine("        if (_isAttached) return;");
        sb.AppendLine("        _isAttached = true;");
        sb.AppendLine("        Attach(fe);");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    private void DetachCore()");
        sb.AppendLine("    {");
        sb.AppendLine("        if (!_isAttached) return;");
        sb.AppendLine("        _isAttached = false;");
        sb.AppendLine("        Detach();");
        sb.AppendLine("    }");
        sb.AppendLine();

        // @register：内联 Attach/Detach（自包含，无需手写 partial）
        if (model.IsRegister)
        {
            sb.AppendLine("    private FrameworkElement? _attached;");
            if (model.IsAnimated)
                sb.AppendLine("    private DateTime _startTime;");
            sb.AppendLine();
            sb.AppendLine("    /// <summary>将本效果挂载到目标元素。");
            sb.AppendLine("    /// 通常无需直接调用，绑定 Target 依赖属性即可自动挂载。</summary>");
            sb.AppendLine("    public void Attach(FrameworkElement element)");
            sb.AppendLine("    {");
            sb.AppendLine("        _attached = element;");
            sb.AppendLine("        element.Effect = this;");
            if (model.IsAnimated)
            {
                sb.AppendLine("        _startTime = DateTime.Now;");
                sb.AppendLine("        CompositionTarget.Rendering += OnRendering;");
            }
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    /// <summary>从目标元素卸载本效果。</summary>");
            sb.AppendLine("    public void Detach()");
            sb.AppendLine("    {");
            if (model.IsAnimated)
                sb.AppendLine("        CompositionTarget.Rendering -= OnRendering;");
            sb.AppendLine("        if (_attached != null)");
            sb.AppendLine("        {");
            sb.AppendLine("            _attached.Effect = null;");
            sb.AppendLine("            _attached = null;");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine();
            if (model.IsAnimated)
            {
                sb.AppendLine("    /// <summary>每帧推送已播放秒数到 time 寄存器。</summary>");
                sb.AppendLine("    private void OnRendering(object? sender, EventArgs e)");
                sb.AppendLine("    {");
                sb.AppendLine("        Time = (DateTime.Now - _startTime).TotalSeconds;");
                sb.AppendLine("    }");
                sb.AppendLine();
            }
        }

        // 构造函数
        sb.AppendLine($"    public {model.ClassName}()");
        sb.AppendLine("    {");
        sb.AppendLine("        PixelShader = _shader;");
        if (model.SamplerIndex != null)
            sb.AppendLine("        UpdateShaderValue(InputProperty);");
        foreach (var prop in model.Properties)
            sb.AppendLine($"        UpdateShaderValue({prop.PropName}Property);");
        if (model.TimeRegister != null)
            sb.AppendLine("        UpdateShaderValue(TimeProperty);");
        if (!model.IsRegister)
            sb.AppendLine("        OnConstructed();");
        sb.AppendLine("    }");
        sb.AppendLine();
        if (!model.IsRegister)
        {
            sb.AppendLine("    /// <summary>手写 partial 侧的构造钩子（推送计算/内部寄存器初值等）。</summary>");
            sb.AppendLine("    partial void OnConstructed();");
        }
        sb.AppendLine("}");
        sb.AppendLine();

        // 配套 MarkupExtension
        sb.AppendLine($"[MarkupExtensionReturnType(typeof({model.ClassName}))]");
        sb.AppendLine($"public class {model.ClassName}Extension : MarkupExtension");
        sb.AppendLine("{");
        sb.AppendLine("    public override object ProvideValue(IServiceProvider serviceProvider)");
        sb.AppendLine("    {");
        sb.AppendLine($"        var fx = new {model.ClassName}();");
        sb.AppendLine("        if (serviceProvider.GetService(typeof(IProvideValueTarget)) is IProvideValueTarget pvt");
        sb.AppendLine("            && pvt.TargetObject is FrameworkElement fe)");
        sb.AppendLine("        {");
        sb.AppendLine("            fx.Target = fe;");
        sb.AppendLine("        }");
        sb.AppendLine("        return fx;");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }

    private static string GetDefaultExpression(string clrType, string? defaultValue)
    {
        switch (clrType)
        {
            case "double":
                if (string.IsNullOrEmpty(defaultValue)) return "0.0";
                return NormalizeNum(StripFloatCtor(defaultValue!));
            case "Color":
                return string.IsNullOrEmpty(defaultValue) ? "Colors.Transparent" : "Colors." + defaultValue;
            case "Point":
                return $"new Point({string.Join(", ", ParseComponents(defaultValue, 2))})";
            case "Vector3D":
                return $"new Vector3D({string.Join(", ", ParseComponents(defaultValue, 3))})";
            case "Point4D":
                return $"new Point4D({string.Join(", ", ParseComponents(defaultValue, 4))})";
            default:
                return "default";
        }
    }

    /// <summary>去掉 <c>floatN(...)</c> / <c>float(...)</c> 包裹，返回其内部内容。</summary>
    private static string StripFloatCtor(string s)
    {
        s = s.Trim();
        var open = s.IndexOf('(');
        if (s.StartsWith("float") && open >= 0 && s.EndsWith(")"))
            return s.Substring(open + 1, s.Length - open - 2).Trim();
        return s;
    }

    /// <summary>把数字字面量规整为 double 形式（补小数点、去 f 后缀）。</summary>
    private static string NormalizeNum(string n)
    {
        n = n.Trim();
        if (n.Length == 0) return "0.0";
        if (n.EndsWith("f") || n.EndsWith("F")) n = n.Substring(0, n.Length - 1);
        return n.Contains(".") ? n : n + ".0";
    }

    /// <summary>
    /// 将默认值解析为 count 个分量。支持 <c>floatN(a,b,c,d)</c>、逗号分隔、单值 splat；
    /// 缺省分量补 0。
    /// </summary>
    private static string[] ParseComponents(string? defaultValue, int count)
    {
        var result = new string[count];
        for (int i = 0; i < count; i++) result[i] = "0.0";
        if (string.IsNullOrEmpty(defaultValue)) return result;

        var inner = StripFloatCtor(defaultValue!);
        var parts = inner.Split(',');

        if (parts.Length == 1)
        {
            var v = NormalizeNum(parts[0]);
            for (int i = 0; i < count; i++) result[i] = v;
            return result;
        }

        for (int i = 0; i < count && i < parts.Length; i++)
            result[i] = NormalizeNum(parts[i]);
        return result;
    }

    #endregion

    #region Models

    private class EffectModel
    {
        public string ClassName { get; set; } = "";
        public string FileName { get; set; } = "";
        public int? SamplerIndex { get; set; }
        public List<PropertyModel> Properties { get; set; } = new();

        /// <summary>true = @register（自包含 sealed 类）；false = @effect（partial 类，配手写 partial）。</summary>
        public bool IsRegister { get; set; }

        /// <summary>true = @animate：Attach 时启动 CompositionTarget.Rendering 循环驱动 time 寄存器。</summary>
        public bool IsAnimated { get; set; }

        /// <summary>time 保留名寄存器索引（cN）；null 表示无动画时间量。</summary>
        public int? TimeRegister { get; set; }
    }

    private class PropertyModel
    {
        public string PropName { get; set; } = "";
        public string ClrType { get; set; } = "";
        public int Register { get; set; }
        public string? DefaultValue { get; set; }
    }

    #endregion
}
