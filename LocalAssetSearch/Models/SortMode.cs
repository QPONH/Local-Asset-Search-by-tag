namespace LocalAssetSearch.Models;

/// <summary>结果列表的排序方式。</summary>
public enum SortMode
{
    /// <summary>按目录名末尾的 <c>{收藏夹页码,页内序号}</c> 升序，解析不出的排最后。</summary>
    FavoriteOrder,

    /// <summary>同上但倒序：页码大的在前，同一页里序号大的在前。</summary>
    FavoriteOrderDesc,

    /// <summary>按标题首字母正序（A→Z）。</summary>
    TitleAsc,

    /// <summary>按标题首字母倒序（Z→A）。</summary>
    TitleDesc,

    /// <summary>按 work-id.json 里的 GlobalIndex 升序，没有识别文件的排最后。</summary>
    GlobalIndex,
}
