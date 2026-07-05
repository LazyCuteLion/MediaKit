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
        "iResolution", "iSourceSize", "iImage", "iTime"
    };

    private static readonly DiagnosticDescriptor DuplicateEffectName = new(
        "SKSL001",
        "重复的效果名称",
        "效果名称 '{0}' 在多个 .sksl 文件中重复定义：{1}",
        "ShaderEffect",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static void Emit(SourceProductionContext ctx, List<AdditionalText> files)
    {
        var effects = new List<EffectModel>();
        var registers = new List<RegisterModel>();

        foreach (var file in files)
        {
            var text = file.GetText(ctx.CancellationToken)?.ToString();
            if (string.IsNullOrEmpty(text)) continue;

            var fileName = Path.GetFileNameWithoutExtension(file.Path);
            var parsed = ParseSksl(text!, fileName);
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

    private static object? ParseSksl(string text, string fileName)
    {
        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        string? effectName = null;
        string? registerName = null;
        bool animate = false;
        var properties = new List<PropertyModel>();
        string? pendingPropertyDefault = null;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();

            // Parse header markers
            if (line.StartsWith("// @effect"))
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
            if (line.StartsWith("// @property"))
            {
                pendingPropertyDefault = ExtractMarkerValue(line, "@property");
                continue;
            }

            // Parse uniform line
            if (pendingPropertyDefault != null || line.StartsWith("uniform "))
            {
                if (line.StartsWith("uniform ") && pendingPropertyDefault != null)
                {
                    var uniform = ParseUniform(line);
                    if (uniform != null && !ReservedUniforms.Contains(uniform.Value.Name))
                    {
                        properties.Add(new PropertyModel
                        {
                            UniformName = uniform.Value.Name,
                            SkslType = uniform.Value.Type,
                            DefaultValue = pendingPropertyDefault
                        });
                    }
                    pendingPropertyDefault = null;
                }
                else
                {
                    pendingPropertyDefault = null;
                }
            }
        }

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
                Properties = properties
            };
        }

        if (registerName != null)
        {
            return new RegisterModel
            {
                Name = registerName,
                FileName = fileName,
                Animate = animate
            };
        }

        return null;
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

    private static readonly Regex UniformRegex = new(
        @"^uniform\s+(float[234]?|int|shader)\s+(\w+)\s*;",
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
        sb.AppendLine("using Avalonia;");
        sb.AppendLine();
        sb.AppendLine("namespace MediaKit.Avalonia.Effects;");
        sb.AppendLine();
        sb.AppendLine($"[EffectName(\"{model.EffectDisplayName}\")]");
        sb.AppendLine($"public partial class {model.ClassName} : ShaderEffect");
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
            sb.AppendLine($"        set {{ if (SetAndRaise({propName}Property, ref {fieldName}, value)) this[\"{prop.UniformName}\"] = {pushExpr}; }}");
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        // Constructor
        sb.AppendLine($"    public {model.ClassName}() : base(_shaderUri)");
        sb.AppendLine("    {");
        foreach (var prop in model.Properties)
        {
            var fieldName = "_" + prop.UniformName;
            var pushExpr = GetPushExpression(prop.SkslType, fieldName);
            sb.AppendLine($"        this[\"{prop.UniformName}\"] = {pushExpr};");
        }
        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }

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
            sb.AppendLine($"    public static EffectDescriptor {reg.Name} {{ get; }} = new(\"{reg.Name}\", static () => new ShaderEffect(new Uri(\"avares://MediaKit.Avalonia/Shaders/{reg.FileName}.sksl\")){animateStr});");
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
        public List<PropertyModel> Properties { get; set; } = new();
    }

    private class RegisterModel
    {
        public string Name { get; set; } = "";
        public string FileName { get; set; } = "";
        public bool Animate { get; set; }
    }

    private class PropertyModel
    {
        public string UniformName { get; set; } = "";
        public string SkslType { get; set; } = "";
        public string? DefaultValue { get; set; }
    }

    #endregion
}
