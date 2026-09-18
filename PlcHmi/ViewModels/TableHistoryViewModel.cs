using System.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlcCore.Services;

namespace PlcHmi.ViewModels;

/// <summary>
/// 「表记录历史」弹窗：看某个存储组的目标表里最近写进去的记录。
/// 打开时查一次，之后靠窗口上的【刷新】重查 —— 不常驻轮询，省得白连数据库。
/// </summary>
public partial class TableHistoryViewModel : ObservableObject
{
    /// <summary>一次取多少条：够顺着时间往回翻，又不至于把宽表拉爆</summary>
    private const int HistoryCount = 500;

    private readonly AppServices _services;
    private readonly string _tableName;

    public TableHistoryViewModel(AppServices services, string groupName, string tableName)
    {
        _services = services;
        _tableName = tableName;
        HeaderText = $"组「{groupName}」 → 表 {tableName}";
    }

    /// <summary>窗口标题栏与表头都用的这行「组 → 表」</summary>
    public string HeaderText { get; }

    /// <summary>长表/宽表列不同，所以直接绑 DataView 让表格自动生成列</summary>
    [ObservableProperty] private DataView? _records;

    [ObservableProperty] private string _status = "加载中…";

    [RelayCommand]
    public async Task RefreshAsync()
    {
        try
        {
            var data = await _services.TableStore.QueryRecentAsync(_tableName.Trim(), HistoryCount);
            Records = data.DefaultView;
            Status = $"最近 {data.Rows.Count} 条（按 Id 倒序）· 更新于 {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            Status = $"读取数据库失败：{ex.Message}";
        }
    }
}
