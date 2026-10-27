using System.Windows;
using System.Windows.Controls;

namespace LocalAssetSearch.Services;

/// <summary>标签分类小标题：标题文字 + 向右延伸的横线，统一三个标签界面。</summary>
public static class TagHeader
{
    public static (Grid Root, TextBlock Text) Create(string text)
    {
        var root = new Grid { Margin = new Thickness(2, 10, 2, 4) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var title = new TextBlock
        {
            Text = text,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeManager.MutedBrush,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 190,
        };
        var rule = new Border
        {
            Height = 1,
            Margin = new Thickness(10, 0, 2, 0),
            Background = ThemeManager.Brush("Brush.Border"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(rule, 1);
        root.Children.Add(title);
        root.Children.Add(rule);
        return (root, title);
    }
}
