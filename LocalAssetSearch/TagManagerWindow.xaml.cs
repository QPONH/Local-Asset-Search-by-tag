using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LocalAssetSearch.Models;
using LocalAssetSearch.Services;

namespace LocalAssetSearch;

/// <summary>
/// 标签管理：改分类、改名、删除，以及新建。
/// 分类本身就是一个文本字段，不需要先建分类再往里塞标签。
/// </summary>
public partial class TagManagerWindow : Window
{
    private readonly Database _db;

    /// <summary>关窗后主窗口据此决定要不要刷新标签和结果。</summary>
    public bool Changed { get; private set; }

    public TagManagerWindow(Database db)
    {
        _db = db;
        InitializeComponent();
        BuildRows();
    }

    private void BuildRows()
    {
        RowPanel.Children.Clear();
        var tags = _db.GetTags();
        if (tags.Count == 0)
        {
            RowPanel.Children.Add(new TextBlock { Text = "还没有任何标签。在上面输入名称即可新建。", Foreground = ThemeManager.MutedBrush, TextWrapping = TextWrapping.Wrap });
            return;
        }

        foreach (var tag in tags)
        {
            var row = new Grid { Margin = new Thickness(2, 3, 2, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });

            var nameBox = new TextBox { Text = tag.Name, Margin = new Thickness(0, 0, 6, 0), VerticalContentAlignment = VerticalAlignment.Center };
            var catBox = new TextBox { Text = tag.Category, Margin = new Thickness(0, 0, 6, 0), ToolTip = "留空表示未分类", VerticalContentAlignment = VerticalAlignment.Center };
            var usage = new TextBlock { Text = tag.UsageCount.ToString("N0"), VerticalAlignment = VerticalAlignment.Center, Foreground = ThemeManager.MutedBrush, Margin = new Thickness(2, 0, 0, 0) };

            var save = new Button { Content = "保存", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 4, 0) };
            var del = new Button { Content = "删除", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0) };
            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            actions.Children.Add(save);
            actions.Children.Add(del);

            Grid.SetColumn(nameBox, 0); Grid.SetColumn(catBox, 1); Grid.SetColumn(usage, 2); Grid.SetColumn(actions, 3);
            row.Children.Add(nameBox); row.Children.Add(catBox); row.Children.Add(usage); row.Children.Add(actions);

            var captured = tag;
            save.Click += (_, _) => Save(captured, nameBox.Text, catBox.Text);
            del.Click += (_, _) => Delete(captured);

            RowPanel.Children.Add(row);
        }
    }

    private void Save(TagInfo tag, string newName, string category)
    {
        try
        {
            bool touched = false;
            if (!string.Equals(newName.Trim(), tag.Name, StringComparison.Ordinal)) { _db.RenameTag(tag.Id, newName); touched = true; }
            if (!string.Equals(category.Trim(), tag.Category, StringComparison.Ordinal)) { _db.SetTagCategory(tag.Id, category); touched = true; }

            StatusText.Text = touched ? $"已保存「{newName.Trim()}」。" : "没有变化。";
            if (touched) { Changed = true; BuildRows(); }
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "保存失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void Delete(TagInfo tag)
    {
        var msg = tag.UsageCount > 0
            ? $"删除标签「{tag.Name}」？\n\n它正被 {tag.UsageCount:N0} 个素材使用，删除后会解除这些关联。\n磁盘上的原始文件不受影响。"
            : $"删除标签「{tag.Name}」？";
        if (MessageBox.Show(msg, "删除标签", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        try
        {
            _db.DeleteTag(tag.Id);
            Changed = true;
            BuildRows();
            StatusText.Text = $"已删除「{tag.Name}」。";
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "删除失败", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void NewTag_Click(object sender, RoutedEventArgs e)
    {
        var name = NewTagName.Text.Trim();
        if (name.Length == 0) { StatusText.Text = "请先输入标签名称。"; return; }
        var category = NewTagCategory.Text.Trim();
        try { _db.EnsureTag(name, category.Length == 0 ? null : category); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "新建标签失败", MessageBoxButton.OK, MessageBoxImage.Error); return; }

        NewTagName.Clear();
        Changed = true;
        BuildRows();
        StatusText.Text = $"已新建「{name}」。";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
