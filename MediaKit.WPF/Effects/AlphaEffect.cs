using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Effects;

namespace MediaKit.WPF.Effects;

/// <summary>
/// Alpha 通道分离效果。将 AlphaEffect 实例赋给元素的 Effect 属性并调用 Attach 即可启用。
/// 基础设施（_shader / Input / Target / 构造函数 / MarkupExtension）由源生成器产出。
/// </summary>
public partial class AlphaEffect : ShaderEffect
{
    #region Dependency Properties

    public static readonly DependencyProperty PositionProperty =
        DependencyProperty.Register(nameof(Position), typeof(Dock), typeof(AlphaEffect),
            new PropertyMetadata(Dock.Right, OnPositionChanged));

    public Dock Position { get => (Dock)GetValue(PositionProperty); set => SetValue(PositionProperty, value); }

    #endregion

    #region Shader Register DPs（枚举映射，手写）

    // C0: Position(Dock) → double
    private static readonly DependencyProperty PositionValueProperty =
        DependencyProperty.Register("PositionValue", typeof(double), typeof(AlphaEffect),
            new UIPropertyMetadata(2.0, PixelShaderConstantCallback(0)));

    #endregion

    #region Instance State

    private FrameworkElement? _element;
    private FrameworkElement? _parent;

    // 挂载前的原始尺寸，用于 Detach 时还原
    private double _originalWidth;
    private double _originalHeight;

    #endregion

    partial void OnConstructed()
    {
        UpdateShaderValue(PositionValueProperty);
    }

    #region Attach / Detach

    public void Attach(FrameworkElement element)
    {
        _element = element;
        _originalWidth = element.Width;
        _originalHeight = element.Height;
        SetPositionValue(Position);
        element.Effect = this;

        _parent = element.Parent as FrameworkElement;
        if (_parent != null)
            _parent.SizeChanged += OnParentSizeChanged;

        AdjustSize(Position);
    }

    public void Detach()
    {
        if (_parent != null)
        {
            _parent.SizeChanged -= OnParentSizeChanged;
            _parent = null;
        }
        if (_element != null)
        {
            // 还原挂载前尺寸，避免移除效果后元素尺寸被污染
            _element.Width = _originalWidth;
            _element.Height = _originalHeight;
            _element.Effect = null;
            _element = null;
        }
    }

    #endregion

    #region Internal Logic

    private void SetPositionValue(Dock dock)
    {
        double v = dock switch
        {
            Dock.Left => 0.0,
            Dock.Top => 1.0,
            Dock.Right => 2.0,
            Dock.Bottom => 3.0,
            _ => 2.0
        };
        SetValue(PositionValueProperty, v);
    }

    private void OnParentSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_element != null)
            AdjustSize(Position);
    }

    private void AdjustSize(Dock pos)
    {
        if (_element == null) return;
        var slot = LayoutInformation.GetLayoutSlot(_element);
        if (slot.Width <= 0 || slot.Height <= 0) return;

        switch (pos)
        {
            case Dock.Left:
            case Dock.Right:
                _element.Width = slot.Width * 2;
                _element.Height = slot.Height;
                break;
            case Dock.Top:
            case Dock.Bottom:
                _element.Width = slot.Width;
                _element.Height = slot.Height * 2;
                break;
        }
    }

    #endregion

    #region Property Callbacks

    private static void OnPositionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var effect = (AlphaEffect)d;
        var pos = (Dock)e.NewValue;
        effect.SetPositionValue(pos);
        if (effect._element != null)
            effect.AdjustSize(pos);
    }

    #endregion
}
