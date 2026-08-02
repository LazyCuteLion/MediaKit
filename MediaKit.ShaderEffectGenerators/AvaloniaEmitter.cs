using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace MediaKit.ShaderEffectGenerators;

/// <summary>
/// Avalonia（SkSL）着色器效果生成器。读取 <c>.sksl</c>，为每个 uniform 与公共属性 1:1 直通生成
/// <c>DirectProperty</c>，并生成 <c>ShaderEffects</c> 描述符与 <c>ModuleInitializer</c> 自动注册。
/// </summary>
internal static class AvaloniaEmitter
{
    private static readonly HashSet<string> ReservedUniforms = new(StringComparer.Ordinal)
    {
        "iResolution", "iTime"
    };

    private static readonly DiagnosticDescriptor DuplicateEffectName = new(
        "SKSL001",
        "重复的效果名称",
        "效果名称 '{0}' 在多个 .sksl 文件中重复定义：{1}",
        "ShaderEffect",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor MissingSourceMarker = new(
        "SKSL002",
        "uniform shader 缺少来源标记",
        "'uniform shader {0}' 上方需要 '// @surface'（取目标控件自身的表面）或 '// @texture'（由调用方喂图）",
        "ShaderEffect",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor MisplacedSourceMarker = new(
        "SKSL003",
        "来源标记位置错误",
        "'{0}' 只能标在 'uniform shader' 上，且同一个 uniform 不能同时标两种来源",
        "ShaderEffect",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor MultipleSurfaces = new(
        "SKSL004",
        "多个 @surface 标记",
        "已有 'uniform shader {0}' 标为 @surface；目标控件的表面只能填一个 slot",
        "ShaderEffect",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor PropertyOnShaderUniform = new(
        "SKSL005",
        "@property 标在了 shader 类型上",
        "'uniform shader {0}' 不能用 @property；改用 '// @texture' 就会生成 Uri? 属性",
        "ShaderEffect",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidTextureName = new(
        "SKSL006",
        "@texture 的属性名不可用",
        "@texture 推导或指定的属性名 '{0}' 不可用：{1}",
        "ShaderEffect",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor SurfaceNameIgnored = new(
        "SKSL007",
        "@surface 后面的名字被忽略",
        "@surface 不生成属性，'{0}' 会被忽略；需要调用方喂图的话请改用 '// @texture'",
        "ShaderEffect",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidCompanionSize = new(
        "SKSL008",
        "伴生尺寸 uniform 用法错误",
        "'{0}' 命中纹理 '{1}' 的伴生尺寸约定，由渲染侧自动喂值：{2}",
        "ShaderEffect",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor TextureNeedsGeneratedClass = new(
        "SKSL009",
        "@effect: default 下无法喂纹理",
        "'uniform shader {0}' 标了 @texture，但 '@effect: default' 不生成强类型类，没有属性可以喂图；请改用 '@effect: <类名>'",
        "ShaderEffect",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static void Emit(SourceProductionContext ctx, List<AdditionalText> files)
    {
        var effects = new List<EffectModel>();
        var registers = new List<RegisterModel>();

        foreach (var file in files)
        {
            var text = file.GetText(ctx.CancellationToken);
            if (text == null || text.Length == 0) continue;

            var fileName = Path.GetFileNameWithoutExtension(file.Path);
            var parsed = ParseSksl(text, fileName, file.Path, ctx);
            if (parsed == null) continue;

            if (parsed is EffectModel effect)
                effects.Add(effect);
            else if (parsed is RegisterModel register)
                registers.Add(register);
        }

        // 检测重复名称
        var nameToFiles = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in effects)
        {
            if (!nameToFiles.TryGetValue(e.EffectDisplayName, out var list))
                nameToFiles[e.EffectDisplayName] = list = new List<string>();
            list.Add(e.FileName + ".sksl");
        }
        foreach (var r in registers)
        {
            if (!nameToFiles.TryGetValue(r.Name, out var list))
                nameToFiles[r.Name] = list = new List<string>();
            list.Add(r.FileName + ".sksl");
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

        if (effects.Count > 0 || registers.Count > 0)
        {
            ctx.AddSource("ShaderEffects.g.cs", SourceText.From(GenerateShaderEffectsClass(effects, registers), Encoding.UTF8));
            ctx.AddSource("ShaderEffectAutoRegister.g.cs", SourceText.From(GenerateAutoRegister(effects, registers), Encoding.UTF8));
        }
    }

    #region Parsing

    private static object? ParseSksl(SourceText source, string fileName, string filePath, SourceProductionContext ctx)
    {
        string? effectName = null;
        string? registerName = null;
        bool animate = false;
        var properties = new List<PropertyModel>();
        var textures = new List<TextureModel>();
        string? surfaceUniform = null;

        // 全部 uniform 声明（名 → 类型 + 位置），伴生尺寸得扫完全文才能判
        var declared = new Dictionary<string, (string Type, Location Location)>(StringComparer.Ordinal);

        string? pendingPropertyDefault = null;
        var pendingSource = PendingSource.None;
        string? pendingTextureName = null;
        Location? pendingSourceLocation = null;

        bool hasError = false;

        foreach (var textLine in source.Lines)
        {
            var line = source.ToString(textLine.Span).Trim();
            if (line.Length == 0) continue;
            var location = LineLocation(filePath, textLine);

            // Parse header markers
            if (line.StartsWith("// @effect", StringComparison.Ordinal))
            {
                var val = ExtractMarkerValue(line, "@effect");
                if (string.Equals(val, "default", StringComparison.OrdinalIgnoreCase))
                    // @effect: default → 仅注册为描述符，不生成强类型类
                    registerName = fileName;
                else
                    effectName = val ?? (fileName + "Effect");
                continue;
            }
            if (line == "// @animate")
            {
                animate = true;
                continue;
            }
            if (line.StartsWith("// @surface", StringComparison.Ordinal))
            {
                if (pendingSource != PendingSource.None)
                {
                    ctx.ReportDiagnostic(Diagnostic.Create(MisplacedSourceMarker, location, "@surface"));
                    hasError = true;
                }
                // 表面没有用户可设的值，也就没有属性名可言。静默失效比误用更难查，所以提醒
                var ignored = line.Substring("// @surface".Length).Trim().TrimStart(':').Trim();
                if (ignored.Length > 0)
                    ctx.ReportDiagnostic(Diagnostic.Create(SurfaceNameIgnored, location, ignored));

                pendingSource = PendingSource.Surface;
                pendingSourceLocation = location;
                continue;
            }
            if (line.StartsWith("// @texture", StringComparison.Ordinal))
            {
                if (pendingSource != PendingSource.None)
                {
                    ctx.ReportDiagnostic(Diagnostic.Create(MisplacedSourceMarker, location, "@texture"));
                    hasError = true;
                }
                pendingSource = PendingSource.Texture;
                pendingTextureName = ExtractMarkerValue(line, "@texture");
                pendingSourceLocation = location;
                continue;
            }
            if (line.StartsWith("// @property", StringComparison.Ordinal))
            {
                pendingPropertyDefault = ExtractMarkerValue(line, "@property");
                continue;
            }

            if (!line.StartsWith("uniform ", StringComparison.Ordinal))
            {
                // 非 uniform 行打断待配对的标记（空行除外，上面已 continue）
                pendingPropertyDefault = null;
                pendingSource = PendingSource.None;
                pendingTextureName = null;
                continue;
            }

            var uniform = ParseUniform(line);
            if (uniform == null)
            {
                pendingPropertyDefault = null;
                pendingSource = PendingSource.None;
                pendingTextureName = null;
                continue;
            }

            var (type, name) = uniform.Value;
            declared[name] = (type, location);

            if (type == "shader")
            {
                if (pendingPropertyDefault != null)
                {
                    ctx.ReportDiagnostic(Diagnostic.Create(PropertyOnShaderUniform, location, name));
                    hasError = true;
                    pendingPropertyDefault = null;
                }

                switch (pendingSource)
                {
                    case PendingSource.None:
                        ctx.ReportDiagnostic(Diagnostic.Create(MissingSourceMarker, location, name));
                        hasError = true;
                        break;

                    case PendingSource.Surface when surfaceUniform != null:
                        ctx.ReportDiagnostic(Diagnostic.Create(MultipleSurfaces, location, surfaceUniform));
                        hasError = true;
                        break;

                    case PendingSource.Surface:
                        surfaceUniform = name;
                        break;

                    case PendingSource.Texture:
                        textures.Add(new TextureModel
                        {
                            UniformName = name,
                            PropertyName = pendingTextureName ?? DeriveTexturePropertyName(name),
                            Location = location,
                            ExplicitName = pendingTextureName != null
                        });
                        break;
                }

                pendingSource = PendingSource.None;
                pendingTextureName = null;
                continue;
            }

            // 数值 uniform：来源标记落在这里就是标错位置了
            if (pendingSource != PendingSource.None)
            {
                var marker = pendingSource == PendingSource.Surface ? "@surface" : "@texture";
                ctx.ReportDiagnostic(Diagnostic.Create(
                    MisplacedSourceMarker, pendingSourceLocation ?? location, marker));
                hasError = true;
                pendingSource = PendingSource.None;
                pendingTextureName = null;
            }

            if (pendingPropertyDefault != null)
            {
                if (!ReservedUniforms.Contains(name) && IsPropertyType(type))
                {
                    properties.Add(new PropertyModel
                    {
                        UniformName = name,
                        SkslType = type,
                        DefaultValue = pendingPropertyDefault,
                        Location = location
                    });
                }
                pendingPropertyDefault = null;
            }
        }

        hasError |= ValidateTextures(ctx, textures, surfaceUniform, declared, properties, registerName != null);
        if (hasError) return null;

        if (effectName != null)
        {
            return new EffectModel
            {
                ClassName = effectName,
                EffectDisplayName = effectName.EndsWith("Effect")
                    ? effectName.Substring(0, effectName.Length - 6)
                    : effectName,
                FileName = fileName,
                Animate = animate,
                RequiresSurface = surfaceUniform != null,
                Properties = properties,
                Textures = textures
            };
        }

        if (registerName != null)
        {
            return new RegisterModel
            {
                Name = registerName,
                FileName = fileName,
                Animate = animate,
                RequiresSurface = surfaceUniform != null
            };
        }

        return null;
    }

    /// <summary>
    /// 扫完全文才能做的校验：伴生尺寸（<c>maskSize</c> 完全可以写在 <c>mask</c> 之前）
    /// 与属性名冲突。返回是否有错。
    /// </summary>
    private static bool ValidateTextures(SourceProductionContext ctx, List<TextureModel> textures,
        string? surfaceUniform, Dictionary<string, (string Type, Location Location)> declared,
        List<PropertyModel> properties, bool isRegisterOnly)
    {
        bool hasError = false;

        // 伴生尺寸对表面槽也成立，所以把 surface 名一起算进来
        var textureNames = textures.Select(t => t.UniformName).ToList();
        if (surfaceUniform != null) textureNames.Add(surfaceUniform);

        foreach (var textureName in textureNames)
        {
            var sizeName = textureName + "Size";
            if (!declared.TryGetValue(sizeName, out var size)) continue;

            if (size.Type != "float2")
            {
                ctx.ReportDiagnostic(Diagnostic.Create(InvalidCompanionSize, size.Location,
                    sizeName, textureName, $"类型必须是 float2，当前是 {size.Type}"));
                hasError = true;
                continue;
            }

            var asProperty = properties.FirstOrDefault(p => p.UniformName == sizeName);
            if (asProperty != null)
            {
                ctx.ReportDiagnostic(Diagnostic.Create(InvalidCompanionSize, asProperty.Location ?? size.Location,
                    sizeName, textureName, "不要标 @property"));
                hasError = true;
                properties.Remove(asProperty);
            }
        }

        foreach (var texture in textures)
        {
            if (isRegisterOnly)
            {
                ctx.ReportDiagnostic(Diagnostic.Create(
                    TextureNeedsGeneratedClass, texture.Location, texture.UniformName));
                hasError = true;
                continue;
            }

            if (!IsValidIdentifier(texture.PropertyName))
            {
                ctx.ReportDiagnostic(Diagnostic.Create(InvalidTextureName, texture.Location,
                    texture.PropertyName, "不是合法的 C# 标识符"));
                hasError = true;
                continue;
            }

            var clash = properties.Any(p => ToPascalCase(p.UniformName) == texture.PropertyName)
                || textures.Any(t => t != texture && t.PropertyName == texture.PropertyName);
            if (clash)
            {
                ctx.ReportDiagnostic(Diagnostic.Create(InvalidTextureName, texture.Location,
                    texture.PropertyName, "与同文件内其它属性名重复"));
                hasError = true;
            }
        }

        return hasError;
    }

    /// <summary>
    /// 纹理属性名的自动推导：先剥掉 <c>^i[A-Z]</c> 形态的前缀，再首字母大写。
    /// <para>
    /// <c>iImage → Image</c>、<c>iMask → Mask</c>，而 <c>intensity</c> 不受影响（i 后是小写）。
    /// 只作用于纹理推导，不是 <see cref="ToPascalCase"/> 的全局规则。
    /// </para>
    /// </summary>
    private static string DeriveTexturePropertyName(string uniformName)
    {
        if (uniformName.Length >= 2 && uniformName[0] == 'i' && char.IsUpper(uniformName[1]))
            return uniformName.Substring(1);
        return ToPascalCase(uniformName);
    }

    private static bool IsValidIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (!char.IsLetter(name[0]) && name[0] != '_') return false;
        return name.All(c => char.IsLetterOrDigit(c) || c == '_');
    }

    private static bool IsPropertyType(string type)
        => type == "float" || type == "float2" || type == "float3" || type == "float4" || type == "int";

    private static Location LineLocation(string filePath, TextLine line)
        => Location.Create(filePath, line.Span, new LinePositionSpan(
            new LinePosition(line.LineNumber, 0),
            new LinePosition(line.LineNumber, line.Span.Length)));

    private enum PendingSource
    {
        None,
        Surface,
        Texture
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

    /// <summary>
    /// 类型位原样捕获（包括 <c>float3x3</c> 这类不生成属性的），筛选交给调用方，
    /// 这样伴生尺寸的类型误用也能被看见而不是静默漏掉。
    /// </summary>
    private static readonly Regex UniformRegex = new(
        @"^uniform\s+(\S+)\s+(\w+)\s*;",
        RegexOptions.Compiled);

    private static (string Type, string Name)? ParseUniform(string line)
    {
        var match = UniformRegex.Match(line);
        if (!match.Success) return null;
        return (match.Groups[1].Value, match.Groups[2].Value);
    }

    #endregion

    #region Code Generation - @effect

    private static string GenerateEffectClass(EffectModel model)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using Avalonia;");
        sb.AppendLine();
        sb.AppendLine("namespace MediaKit.Avalonia.Effects;");
        sb.AppendLine();
        sb.AppendLine($"[EffectName(\"{model.EffectDisplayName}\")]");
        sb.AppendLine($"public partial class {model.ClassName} : {GetBaseClass(model.RequiresSurface)}");
        sb.AppendLine("{");
        sb.AppendLine($"    private static readonly Uri _shaderUri = new(\"avares://MediaKit.Avalonia/Shaders/{model.FileName}.sksl\");");
        sb.AppendLine();

        // Property declarations
        foreach (var prop in model.Properties)
        {
            var csharpType = GetCSharpType(prop.SkslType);
            var propName = ToPascalCase(prop.UniformName);
            var defaultExpr = GetDefaultExpression(prop.SkslType, prop.DefaultValue);
            var fieldName = "_" + prop.UniformName;

            sb.AppendLine($"    public static readonly DirectProperty<{model.ClassName}, {csharpType}> {propName}Property =");
            sb.AppendLine($"        AvaloniaProperty.RegisterDirect<{model.ClassName}, {csharpType}>(");
            sb.AppendLine($"            nameof({propName}), o => o.{propName}, (o, v) => o.{propName} = v, {defaultExpr});");
            sb.AppendLine();
            sb.AppendLine($"    private {csharpType} {fieldName} = {defaultExpr};");
            sb.AppendLine($"    public {csharpType} {propName}");
            sb.AppendLine("    {");
            sb.AppendLine($"        get => {fieldName};");

            var pushExpr = GetPushExpression(prop.SkslType, "value");
            sb.AppendLine($"        set {{ if (SetAndRaise({propName}Property, ref {fieldName}, value)) SetUniform(\"{prop.UniformName}\", {pushExpr}); }}");
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        // 纹理属性：值是图片 URI，由渲染侧加载并管 GPU 生命周期，所以不走 SetUniform
        foreach (var tex in model.Textures)
        {
            var fieldName = "_" + tex.UniformName;

            sb.AppendLine($"    public static readonly DirectProperty<{model.ClassName}, Uri?> {tex.PropertyName}Property =");
            sb.AppendLine($"        AvaloniaProperty.RegisterDirect<{model.ClassName}, Uri?>(");
            sb.AppendLine($"            nameof({tex.PropertyName}), o => o.{tex.PropertyName}, (o, v) => o.{tex.PropertyName} = v);");
            sb.AppendLine();
            sb.AppendLine($"    private Uri? {fieldName};");
            sb.AppendLine($"    public Uri? {tex.PropertyName}");
            sb.AppendLine("    {");
            sb.AppendLine($"        get => {fieldName};");
            sb.AppendLine($"        set {{ if (SetAndRaise({tex.PropertyName}Property, ref {fieldName}, value)) SetTexture(\"{tex.UniformName}\", value); }}");
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        // Constructor
        sb.AppendLine($"    public {model.ClassName}() : base(_shaderUri)");
        sb.AppendLine("    {");
        sb.AppendLine("    }");

        // 初值交接：attach 时把当前字段值一次性交给新建的 Renderer
        if (model.Properties.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("    protected override void CollectUniforms(Dictionary<string, object> sink)");
            sb.AppendLine("    {");
            sb.AppendLine("        base.CollectUniforms(sink);");
            foreach (var prop in model.Properties)
            {
                var fieldName = "_" + prop.UniformName;
                var pushExpr = GetPushExpression(prop.SkslType, fieldName);
                sb.AppendLine($"        sink[\"{prop.UniformName}\"] = {pushExpr};");
            }
            sb.AppendLine("    }");
        }

        // 纹理的初值也得在 attach 时交接：渲染器构造时就要求每个 @texture 槽有源
        if (model.Textures.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("    protected override void CollectTextures(Dictionary<string, Uri?> sink)");
            sb.AppendLine("    {");
            sb.AppendLine("        base.CollectTextures(sink);");
            foreach (var tex in model.Textures)
                sb.AppendLine($"        sink[\"{tex.UniformName}\"] = _{tex.UniformName};");
            sb.AppendLine("    }");
        }

        sb.AppendLine("}");

        return sb.ToString();
    }

    private static string GetBaseClass(bool requiresSurface)
        => requiresSurface ? "ShaderEffect" : "ShaderPainter";

    private static string GetCSharpType(string skslType)
    {
        return skslType switch
        {
            "float" => "double",
            "float2" => "float[]",
            "float3" => "float[]",
            "float4" => "float[]",
            "int" => "int",
            _ => "double"
        };
    }

    private static string GetDefaultExpression(string skslType, string? defaultValue)
    {
        if (string.IsNullOrEmpty(defaultValue))
        {
            return skslType switch
            {
                "float" => "0.0",
                "float2" => "new float[] { 0f, 0f }",
                "float3" => "new float[] { 0f, 0f, 0f }",
                "float4" => "new float[] { 0f, 0f, 0f, 0f }",
                "int" => "0",
                _ => "0.0"
            };
        }

        var parts = defaultValue!.Split(',');
        if (parts.Length == 1)
        {
            // Single value
            return skslType switch
            {
                "float" => defaultValue.Contains(".") ? defaultValue : defaultValue + ".0",
                "int" => defaultValue,
                "float2" => $"new float[] {{ {ToFloat(parts[0])}, {ToFloat(parts[0])} }}",
                "float3" => $"new float[] {{ {ToFloat(parts[0])}, {ToFloat(parts[0])}, {ToFloat(parts[0])} }}",
                "float4" => $"new float[] {{ {ToFloat(parts[0])}, {ToFloat(parts[0])}, {ToFloat(parts[0])}, {ToFloat(parts[0])} }}",
                _ => defaultValue
            };
        }

        // Multiple values
        var floatParts = string.Join(", ", parts.Select(p => ToFloat(p.Trim())));
        return $"new float[] {{ {floatParts} }}";
    }

    private static string ToFloat(string val)
    {
        val = val.Trim();
        if (val.Contains("."))
            return val.EndsWith("f") ? val : val + "f";
        return val + "f";
    }

    private static string GetPushExpression(string skslType, string valueExpr)
    {
        return skslType switch
        {
            "float" => $"(float){valueExpr}",
            "int" => valueExpr,
            _ => valueExpr // float2/3/4 → float[] 直接赋值
        };
    }

    private static string ToPascalCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return char.ToUpperInvariant(name[0]) + name.Substring(1);
    }

    #endregion

    #region Code Generation - ShaderEffects + AutoRegister

    private static string GenerateShaderEffectsClass(List<EffectModel> effects, List<RegisterModel> registers)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using System;");
        sb.AppendLine();
        sb.AppendLine("namespace MediaKit.Avalonia.Effects;");
        sb.AppendLine();
        sb.AppendLine("public static class ShaderEffects");
        sb.AppendLine("{");

        foreach (var e in effects)
        {
            var animateStr = e.Animate ? ", animate: true" : "";
            sb.AppendLine($"    public static EffectDescriptor {e.EffectDisplayName} {{ get; }} = new(\"{e.EffectDisplayName}\", static () => new {e.ClassName}(){animateStr});");
        }

        foreach (var reg in registers)
        {
            var animateStr = reg.Animate ? ", animate: true" : "";
            sb.AppendLine($"    public static EffectDescriptor {reg.Name} {{ get; }} = new(\"{reg.Name}\", static () => new {GetBaseClass(reg.RequiresSurface)}(new Uri(\"avares://MediaKit.Avalonia/Shaders/{reg.FileName}.sksl\")){animateStr});");
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string GenerateAutoRegister(List<EffectModel> effects, List<RegisterModel> registers)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using System.Runtime.CompilerServices;");
        sb.AppendLine();
        sb.AppendLine("namespace MediaKit.Avalonia.Effects;");
        sb.AppendLine();
        sb.AppendLine("internal static class ShaderEffectAutoRegister");
        sb.AppendLine("{");
        sb.AppendLine("    [ModuleInitializer]");
        sb.AppendLine("    internal static void Initialize()");
        sb.AppendLine("    {");

        foreach (var e in effects)
            sb.AppendLine($"        ShaderEffectConverter.Register(ShaderEffects.{e.EffectDisplayName});");

        foreach (var reg in registers)
            sb.AppendLine($"        ShaderEffectConverter.Register(ShaderEffects.{reg.Name});");

        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    #endregion

    #region Models

    private class EffectModel
    {
        public string ClassName { get; set; } = "";
        public string EffectDisplayName { get; set; } = "";
        public string FileName { get; set; } = "";
        public bool Animate { get; set; }
        public bool RequiresSurface { get; set; }
        public List<PropertyModel> Properties { get; set; } = new();
        public List<TextureModel> Textures { get; set; } = new();
    }

    private class RegisterModel
    {
        public string Name { get; set; } = "";
        public string FileName { get; set; } = "";
        public bool Animate { get; set; }
        public bool RequiresSurface { get; set; }
    }

    private class PropertyModel
    {
        public string UniformName { get; set; } = "";
        public string SkslType { get; set; } = "";
        public string? DefaultValue { get; set; }
        public Location? Location { get; set; }
    }

    private class TextureModel
    {
        public string UniformName { get; set; } = "";

        /// <summary>显式给的 name，或从 uniform 名推导出来的。</summary>
        public string PropertyName { get; set; } = "";

        public bool ExplicitName { get; set; }
        public Location Location { get; set; } = Location.None;
    }

    #endregion
}
