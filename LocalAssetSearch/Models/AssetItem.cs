using System.ComponentModel;

namespace LocalAssetSearch.Models;

public sealed class AssetItem : INotifyPropertyChanged
{
    public long Id { get; set; }
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public long Size { get; set; }
    public DateTime Modified { get; set; }
    public string ThumbnailPath { get; set; } = "";

    /// <summary>该条目自身的标签名，绑定到卡片上的标签芯片。</summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>文件夹里的图片数量。生成缩略图那一趟顺手数好，不在扫描时入库。</summary>
    public int ImageCount { get; set; }

    /// <summary>卡片上显示的图片数量文案。</summary>
    public string ImageCountLabel => ImageCount == 0 ? "无图片" : $"{ImageCount} 张图片";

    public string KindLabel => Kind switch { "folder" => "文件夹", "image" => "图片", _ => Kind };
    public string DisplaySize => Kind == "folder" ? "文件夹" : Size < 1024 * 1024 ? $"{Size / 1024.0:0} KB" : $"{Size / 1024.0 / 1024:0.0} MB";

    /// <summary>所在目录。同名文件夹靠它区分。</summary>
    public string ParentFolder => System.IO.Path.GetDirectoryName(Path) ?? "";

    private bool _isSelected;

    /// <summary>
    /// 卡片左上角的勾选状态。双向绑定到 CheckBox，供「从工具中删除」批量操作。
    /// 需要通知机制：点「全选本页」时要能直接驱动界面上的勾选框。
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
