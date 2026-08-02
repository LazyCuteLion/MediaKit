using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using MediaKit.Avalonia.Effects;

namespace MediaKit.Avalonia.Demo;

public partial class InvertProbeWindow : Window
{
    private InvertEffect? _effect;

    public InvertProbeWindow()
    {
        InitializeComponent();
        AttachEffect();
    }

    private void AttachEffect()
    {
        _effect = new InvertEffect { Amount = AmountSlider.Value };
        ShaderEffect.SetEffect(TargetPanel, _effect);
        UpdateStatus();
    }

    private void DetachEffect()
    {
        ShaderEffect.SetEffect(TargetPanel, null);
        _effect = null;
        UpdateStatus();
    }

    private void AmountSlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (AmountText != null)
            AmountText.Text = AmountSlider.Value.ToString("F2");
        if (_effect != null)
            _effect.Amount = AmountSlider.Value;
    }

    private void EffectToggle_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (TargetPanel == null) return;

        if (EffectToggle.IsChecked == true)
        {
            if (_effect == null) AttachEffect();
        }
        else
        {
            if (_effect != null) DetachEffect();
        }
    }

    private void Reattach_Click(object? sender, RoutedEventArgs e)
    {
        DetachEffect();
        AttachEffect();
        EffectToggle.IsChecked = true;
    }

    private void UpdateStatus()
    {
        if (StatusText == null) return;
        StatusText.Text =
            $"Effect={(_effect == null ? "无" : "InvertEffect")}  " +
            $"TargetPanel.CacheMode={(TargetPanel.CacheMode?.GetType().Name ?? "null")}   " +
            "（拖 amount 到 0 时 ② 应与 ① 完全一致；Debug 构建下效果区左上角的随机色小方块是 ShaderEffect 内置调试绘制。）";
    }
}
