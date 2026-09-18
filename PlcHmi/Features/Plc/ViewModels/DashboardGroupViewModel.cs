using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PlcHmi.Features.Plc.ViewModels;

/// <summary>
/// 看板上的一段：一个存储组下的所有点位卡片。
/// 组名与目标表在分组的当下定下来（见 <see cref="DashboardViewModel.RefreshGrouping"/>），
/// 卡片被挪进挪出只影响 <see cref="Tags"/>，不影响这一段本身。
/// </summary>
public partial class DashboardGroupViewModel : ObservableObject
{
    public DashboardGroupViewModel(string name, string tableName, IReadOnlyList<TagViewModel> tags, bool isExpanded)
    {
        Name = name;
        TableName = tableName;
        IsExpanded = isExpanded;

        foreach (var tag in tags)
        {
            Tags.Add(tag);
        }

        HeaderText = tableName.Length == 0 ? $"{Tags.Count} 个点位" : $"{Tags.Count} 个点位 → {tableName}";
    }

    /// <summary>组名，与「存储映射」页的组名一致；地址没落在任何区段的点位归在「未归组」</summary>
    public string Name { get; }

    /// <summary>目标表；该组还没提交到生效配置时为空</summary>
    public string TableName { get; }

    /// <summary>组头右边的摘要：多少个点位、落到哪张表</summary>
    public string HeaderText { get; }

    /// <summary>组内的点位卡片，顺序与点位表一致</summary>
    public ObservableCollection<TagViewModel> Tags { get; } = [];

    /// <summary>展开 / 收起；重建分组时按组名还原，不会因为改了点位就弹回默认</summary>
    [ObservableProperty] private bool _isExpanded;
}
