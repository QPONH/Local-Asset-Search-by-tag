using System.Windows;

namespace LocalAssetSearch;

/// <summary>
/// 只改标签显示文字的小窗口。真正的更新由 MainWindow 调 Database.RenameTag 完成，
/// AssetTags 仍然按 TagId 关联，所以不会影响已有标签对应。
/// </summary>
public partial class TagRenameWindow : Window
{
    public string TagName { get; private set; } = "";

    public TagRenameWindow(string currentName)
    {
        InitializeComponent();
        NameBox.Text = currentName;
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            System.Windows.MessageBox.Show("标签文字不能为空。", "编辑标签文字", MessageBoxButton.OK, MessageBoxImage.Information);
            NameBox.Focus();
            return;
        }
        TagName = name;
        DialogResult = true;
    }
}
