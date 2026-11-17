using System.Windows;
using System.Windows.Controls;

namespace WinTune.Views.Pages;

/// <summary>Placeholder page for the sections of later phases: title and list of what it will contain.</summary>
public abstract class PlaceholderPage : Page
{
    protected PlaceholderPage(string title, string phase, string[] features)
    {
        Title = title;
        SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");

        var panel = new StackPanel { Margin = new Thickness(32, 24, 32, 32) };

        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 28,
            FontWeight = FontWeights.SemiBold,
        });

        var subtitle = new TextBlock
        {
            Text = "Coming in " + phase + ". It will contain:",
            Margin = new Thickness(0, 4, 0, 16),
            TextWrapping = TextWrapping.Wrap,
        };
        subtitle.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        panel.Children.Add(subtitle);

        foreach (string feature in features)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "•  " + feature,
                Margin = new Thickness(0, 0, 0, 8),
                TextWrapping = TextWrapping.Wrap,
            });
        }

        Content = panel;
    }
}
