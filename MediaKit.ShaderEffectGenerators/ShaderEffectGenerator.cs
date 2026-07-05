using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace MediaKit.ShaderEffectGenerators;

/// <summary>
/// 多平台着色器效果源生成器。按 AdditionalFiles 扩展名分派：
/// <list type="bullet">
///   <item><c>.sksl</c> → Avalonia（SkSL / DirectProperty）</item>
///   <item><c>.fx</c> → WPF（HLSL / DependencyProperty + PixelShaderConstantCallback）</item>
///   <item><c>.hlsl</c> → WinUI（预留）</item>
/// </list>
/// 生成器本身不引用任何 UI 框架，仅输出源代码字符串；生成的代码编译进消费方项目。
/// </summary>
[Generator(LanguageNames.CSharp)]
public class ShaderEffectGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var shaderFiles = context.AdditionalTextsProvider
            .Where(static f =>
                f.Path.EndsWith(".sksl", StringComparison.OrdinalIgnoreCase) ||
                f.Path.EndsWith(".fx", StringComparison.OrdinalIgnoreCase));

        context.RegisterSourceOutput(shaderFiles.Collect(), GenerateCode);
    }

    private void GenerateCode(SourceProductionContext ctx, ImmutableArray<AdditionalText> files)
    {
        var sksl = new List<AdditionalText>();
        var fx = new List<AdditionalText>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            // 部分 SDK 会把资源文件重复加入 AdditionalFiles，去重
            if (!seen.Add(file.Path)) continue;

            if (file.Path.EndsWith(".sksl", StringComparison.OrdinalIgnoreCase))
                sksl.Add(file);
            else if (file.Path.EndsWith(".fx", StringComparison.OrdinalIgnoreCase))
                fx.Add(file);
            // .hlsl → WinUI 分支预留
        }

        if (sksl.Count > 0)
            AvaloniaEmitter.Emit(ctx, sksl);

        if (fx.Count > 0)
            WpfEmitter.Emit(ctx, fx);
    }
}
