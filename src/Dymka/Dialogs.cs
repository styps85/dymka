using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Dymka;

/// <summary>Аккуратные диалоги в стиле браузера вместо системных MessageBox.</summary>
static class Dialogs
{
    public static (int index, bool checkbox) Show(Window? owner, string title, string message, string[] buttons,
        int defaultIndex = 0, string? checkbox = null, bool checkboxValue = false)
    {
        int result = buttons.Length - 1;
        var res = Application.Current.Resources;

        var win = new Window
        {
            Title = title,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            ShowInTaskbar = owner == null,
            WindowStartupLocation = owner != null && owner.IsVisible ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            FontFamily = (FontFamily)res["UiFont"],
            Foreground = (Brush)res["Ink"],
            Topmost = owner?.Topmost ?? false,
        };
        if (owner != null && owner.IsVisible) win.Owner = owner;

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 17,
            FontWeight = FontWeights.Bold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        });
        stack.Children.Add(new TextBlock
        {
            Text = message,
            FontSize = 13.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)res["Ink2"],
            LineHeight = 20,
        });

        CheckBox? cb = null;
        if (checkbox != null)
        {
            cb = new CheckBox
            {
                Content = checkbox,
                IsChecked = checkboxValue,
                Margin = new Thickness(0, 14, 0, 0),
                FontSize = 13,
                Foreground = (Brush)res["Ink2"],
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            stack.Children.Add(cb);
        }

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        for (int i = 0; i < buttons.Length; i++)
        {
            int idx = i;
            var b = new Button
            {
                Content = buttons[i],
                Style = (Style)res[i == defaultIndex ? "PrimaryButton" : "SecondaryButton"],
                Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0),
                MinWidth = 96,
                IsDefault = i == defaultIndex,
                IsCancel = i == buttons.Length - 1,
            };
            b.Click += (_, _) => { result = idx; win.DialogResult = true; };
            row.Children.Add(b);
        }
        stack.Children.Add(row);

        win.Content = new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(16),
            BorderBrush = (Brush)res["Line"],
            BorderThickness = new Thickness(1),
            Padding = new Thickness(24, 22, 24, 20),
            Margin = new Thickness(20),
            Width = 440,
            Child = stack,
            Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 8, Direction = 270, Opacity = 0.18, Color = Color.FromRgb(0x1B, 0x22, 0x30) },
        };
        win.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { win.DragMove(); } catch { } };

        try { win.ShowDialog(); } catch { }
        return (result, cb?.IsChecked == true);
    }
}
