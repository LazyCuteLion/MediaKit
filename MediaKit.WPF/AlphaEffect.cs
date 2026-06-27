using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace MediaKit.WPF;

/// <summary>
/// Alpha 通道分离效果。在 UIElement 上设置 AlphaEffect.IsEnabled="True" 即可启用。
/// </summary>
public class AlphaEffect : ShaderEffect
{
    private static readonly Dictionary<FrameworkElement, AlphaEffect> _effects = new();

    private static readonly PixelShader _shader = new()
    {
        UriSource = new Uri("pack://application:,,,/MediaKit.WPF;component/Shaders/Compiled/Alpha.ps")
    };

    #region Attached Properties

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached("IsEnabled", typeof(bool), typeof(AlphaEffect),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static readonly DependencyProperty PositionProperty =
        DependencyProperty.RegisterAttached("Position", typeof(Dock), typeof(AlphaEffect),
            new PropertyMetadata(Dock.Right, OnPositionChanged));

    public static bool GetIsEnabled(UIElement e) => (bool)e.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(UIElement e, bool v) => e.SetValue(IsEnabledProperty, v);

    public static Dock GetPosition(UIElement e) => (Dock)e.GetValue(PositionProperty);
    public static void SetPosition(UIElement e, Dock v) => e.SetValue(PositionProperty, v);

    #endregion

    #region Shader Register DPs

    private static readonly DependencyProperty InputProperty =
        RegisterPixelShaderSamplerProperty("Input", typeof(AlphaEffect), 0);

    private static readonly DependencyProperty PositionValueProperty =
        DependencyProperty.Register("PositionValue", typeof(double), typeof(AlphaEffect),
            new UIPropertyMetadata(2.0, PixelShaderConstantCallback(0)));

    #endregion

    #region Instance State

    private FrameworkElement? _element;
    private FrameworkElement? _parent;

    #endregion

    #region Constructor

    private AlphaEffect()
    {
        PixelShader = _shader;
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(PositionValueProperty);
    }

    #endregion

    #region Attach / Detach

    private void Attach(FrameworkElement element, Dock pos)
    {
        _element = element;
        SetPositionValue(pos);
        element.Effect = this;

        _parent = element.Parent as FrameworkElement;
        if (_parent != null)
            _parent.SizeChanged += OnParentSizeChanged;

        AdjustSize(pos);
    }

    private void Detach()
    {
        if (_parent != null)
        {
            _parent.SizeChanged -= OnParentSizeChanged;
            _parent = null;
        }
        _element = null;
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
            AdjustSize(GetPosition(_element));
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

    #region Attached Property Callbacks

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;

        if ((bool)e.NewValue)
        {
            var effect = new AlphaEffect();
            _effects[element] = effect;
            effect.Attach(element, GetPosition(element));
        }
        else
        {
            if (_effects.TryGetValue(element, out var effect))
            {
                effect.Detach();
                _effects.Remove(element);
            }
            element.Effect = null;
        }
    }

    private static void OnPositionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FrameworkElement el && _effects.TryGetValue(el, out var effect))
        {
            var pos = (Dock)e.NewValue;
            effect.SetPositionValue(pos);
            effect.AdjustSize(pos);
        }
    }

    #endregion
}
