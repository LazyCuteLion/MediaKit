using System.Reflection;
using Avalonia;
using Avalonia.Media;
using HarmonyLib;

namespace MediaKit.Avalonia.Effects;

/// <summary>
/// 用 Harmony 在运行时修补 Avalonia effect 管线，使 <see cref="ShaderEffect"/> 不触发
/// <c>SaveLayer</c> 离屏层。
/// <para>
/// 修补两个方法：
/// <list type="bullet">
///   <item><c>EffectExtensions.ToImmutable</c>：ShaderEffect 返回 null，
///       使 <c>comp.Effect = null</c>，不进入 effect 管线。</item>
///   <item><c>EffectExtensions.EffectEquals</c>：immutable=null 且 right=ShaderEffect 时返回 true，
///       避免每帧重复赋值。</item>
/// </list>
/// </para>
/// </summary>
internal static class ShaderEffectPatch
{
    private static bool _applied;

    public static void Apply()
    {
        if (_applied) return;
        _applied = true;

        var harmony = new Harmony("MediaKit.Avalonia.ShaderEffect");
        var baseAssembly = Assembly.Load("Avalonia.Base");

        // ── 1. EffectExtensions.ToImmutable：ShaderEffect → 返回 null ──
        var toImmutable = baseAssembly.GetType("Avalonia.Media.EffectExtensions")?
            .GetMethod("ToImmutable", BindingFlags.Static | BindingFlags.Public,
                null, [typeof(IEffect)], null);
        if (toImmutable != null)
            harmony.Patch(toImmutable, postfix: new HarmonyMethod(typeof(ShaderEffectPatch), nameof(ToImmutablePostfix)));

        // ── 2. EffectExtensions.EffectEquals：null ↔ ShaderEffect 返回 true ──
        var effectEquals = baseAssembly.GetType("Avalonia.Media.EffectExtensions")?
            .GetMethod("EffectEquals", BindingFlags.Static | BindingFlags.NonPublic,
                null, [typeof(IImmutableEffect), typeof(IEffect)], null);
        if (effectEquals != null)
            harmony.Patch(effectEquals, prefix: new HarmonyMethod(typeof(ShaderEffectPatch), nameof(EffectEqualsPrefix)));
    }

    /// <summary>
    /// Postfix：ShaderEffect 的 ToImmutable 返回 null，使 comp.Effect = null。
    /// </summary>
    private static void ToImmutablePostfix(IEffect effect, ref IImmutableEffect? __result)
    {
        if (effect is ShaderEffect)
            __result = null;
    }

    /// <summary>
    /// Prefix：immutable=null 且 right=ShaderEffect 时返回 true，避免每帧重复赋值。
    /// </summary>
    private static bool EffectEqualsPrefix(IImmutableEffect? immutable, IEffect? right, ref bool __result)
    {
        if (immutable == null && right is ShaderEffect)
        {
            __result = true;
            return false;
        }
        return true; // 走原逻辑
    }
}
