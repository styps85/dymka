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

    /// <summary>Запрос пароля. confirm - два поля (новый пароль и повтор). validate возвращает текст ошибки или null.</summary>
    public static string? AskPassword(Window? owner, string title, string message, bool confirm, Func<string, string?>? validate = null)
    {
        var res = Application.Current.Resources;
        string? result = null;
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

        PasswordBox Box() => new()
        {
            FontSize = 14, Height = 38, Padding = new Thickness(10, 0, 10, 0), VerticalContentAlignment = VerticalAlignment.Center,
            BorderBrush = (Brush)res["LineStrong"], BorderThickness = new Thickness(1.5), Background = (Brush)res["Field"],
        };
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        stack.Children.Add(new TextBlock { Text = message, FontSize = 13.5, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)res["Ink2"], LineHeight = 20 });
        var p1 = Box();
        p1.Margin = new Thickness(0, 14, 0, 0);
        stack.Children.Add(p1);
        PasswordBox? p2 = null;
        if (confirm)
        {
            stack.Children.Add(new TextBlock { Text = "Повторите пароль", FontSize = 12.5, Foreground = (Brush)res["Muted"], Margin = new Thickness(0, 10, 0, 4) });
            p2 = Box();
            stack.Children.Add(p2);
        }
        var error = new TextBlock { FontSize = 12.5, Foreground = new SolidColorBrush(Color.FromRgb(0xC2, 0x35, 0x2E)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        stack.Children.Add(error);

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var ok = new Button { Content = "Готово", Style = (Style)res["PrimaryButton"], MinWidth = 96, IsDefault = true };
        var cancel = new Button { Content = "Отмена", Style = (Style)res["SecondaryButton"], MinWidth = 96, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        row.Children.Add(ok);
        row.Children.Add(cancel);
        stack.Children.Add(row);

        void Fail(string msg) { error.Text = msg; error.Visibility = Visibility.Visible; p1.Focus(); p1.SelectAll(); }
        ok.Click += (_, _) =>
        {
            var pw = p1.Password;
            if (pw.Length == 0) { Fail("Введите пароль"); return; }
            if (p2 != null && p2.Password != pw) { Fail("Пароли не совпадают"); return; }
            win.Cursor = System.Windows.Input.Cursors.Wait;
            var err = validate?.Invoke(pw);
            win.Cursor = null;
            if (err != null) { Fail(err); return; }
            result = pw;
            win.DialogResult = true;
        };
        win.Content = new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(16),
            BorderBrush = (Brush)res["Line"],
            BorderThickness = new Thickness(1),
            Padding = new Thickness(24, 22, 24, 20),
            Margin = new Thickness(20),
            Width = 420,
            Child = stack,
            Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 8, Direction = 270, Opacity = 0.18, Color = Color.FromRgb(0x1B, 0x22, 0x30) },
        };
        win.Loaded += (_, _) => p1.Focus();
        win.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { win.DragMove(); } catch { } };
        try { win.ShowDialog(); } catch { }
        return result;
    }
}
