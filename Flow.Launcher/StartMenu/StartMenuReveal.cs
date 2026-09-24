using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Flow.Launcher.StartMenu;

/// <summary>
/// The Windows 10 Start menu's hover light ("reveal"): a soft light that follows the mouse over a row or button.
/// Measured on a list row: about 20% of the reveal color at the mouse, fading to about 2% some 270 DIP away.
/// </summary>
public static class StartMenuReveal
{
    private const double Radius = 270;

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(StartMenuReveal), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not System.Windows.Controls.Border border) return;

        if ((bool)e.NewValue)
        {
            border.MouseEnter += OnMouse;
            border.MouseMove += OnMouse;
            border.MouseLeave += OnMouseLeave;
        }
        else
        {
            border.MouseEnter -= OnMouse;
            border.MouseMove -= OnMouse;
            border.MouseLeave -= OnMouseLeave;
        }
    }

    private static void OnMouse(object sender, MouseEventArgs e)
    {
        var border = (System.Windows.Controls.Border)sender;
        var position = e.GetPosition(border);

        if (border.Background is not RadialGradientBrush brush || brush.IsFrozen)
        {
            var color = border.TryFindResource("Win10StartRevealColor") is Color c ? c : Colors.White;
            brush = new RadialGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute,
                RadiusX = Radius,
                RadiusY = Radius,
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0x33, color.R, color.G, color.B), 0),
                    new GradientStop(Color.FromArgb(0x05, color.R, color.G, color.B), 1)
                }
            };
            border.Background = brush;
        }

        brush.Center = position;
        brush.GradientOrigin = position;
    }

    private static void OnMouseLeave(object sender, MouseEventArgs e)
    {
        ((System.Windows.Controls.Border)sender).Background = Brushes.Transparent;
    }
}
