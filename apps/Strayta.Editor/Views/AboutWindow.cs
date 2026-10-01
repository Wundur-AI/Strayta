using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Strayta.Editor.Views;

/// <summary>About Strayta: version, license, and the trademark notice (Strayta is not affiliated with Adobe).</summary>
public sealed class AboutWindow : Window
{
    public const string TrademarkNotice =
        "Adobe and Photoshop are trademarks or registered trademarks of Adobe Inc. in the United States and/or other " +
        "countries. Strayta is an independent project; it is not affiliated with, endorsed by or sponsored by Adobe. " +
        "PSD is the file format Adobe publishes; Strayta reads and writes it for compatibility.";

    public AboutWindow()
    {
        Title = "About Strayta";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var version = typeof(AboutWindow).Assembly.GetName().Version;
        var close = new Button { Content = "OK", IsDefault = true, IsCancel = true, MinWidth = 80, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();
        Content = new StackPanel
        {
            Margin = new Thickness(22),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "Strayta", FontSize = 20, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = $"Version {version?.ToString(3) ?? "0.1.0"}", Opacity = 0.7 },
                new TextBlock { Text = "A free, open-source image editor for layered documents. MIT license.", TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = "github.com/Wundur-Ai/strayta", Opacity = 0.7 },
                new TextBlock { Text = TrademarkNotice, TextWrapping = TextWrapping.Wrap, FontSize = 11, Opacity = 0.6, Margin = new Thickness(0, 6, 0, 0) },
                close,
            },
        };
    }
}
