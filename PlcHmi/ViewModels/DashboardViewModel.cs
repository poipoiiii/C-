using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using PlcCore.Models;
using PlcCore.Services;
using SkiaSharp;

namespace PlcHmi.ViewModels;

/// <summary>
/// 「实时看板」页：每个点位一张卡片（大数字 + 滚动曲线），按存储组分段、每段可折叠。
/// 卡片跟随点位表重建；点某张卡片的曲线弹「表记录历史」窗（见 <see cref="ResolveStorageGroup"/>）。
/// 这里只维护 RecordsInfo 那行提示（「存储映射」页底部用它显示落库是否还在动）。
/// </summary>
public partial class DashboardViewModel : ObservableObject
{
    /// <summary>RecordsInfo 那行提示每次从数据库取多少条来数</summary>
    private const int RecentRecordCount = 50;

    /// <summary>地址不落在任何区段内的点位会单独归到这一组：它们既不会被采集，也不会落库</summary>
    private const string UngroupedName = "未归组";

    /// <summary>卡片曲线的配色，按点位顺序轮着用</summary>
    private static readonly SKColor[] Palette =
    [
        SKColor.Parse("#E6A23C"),
        SKColor.Parse("#409EFF"),
        SKColor.Parse("#67C23A"),
        SKColor.Parse("#F56C6C"),
        SKColor.Parse("#9B59B6"),
        SKColor.Parse("#909399")
    ];

    private readonly MainViewModel _shell;
    private readonly AppServices _services;
    private readonly DispatcherTimer _recordTimer;

    public DashboardViewModel(MainViewModel shell, AppServices services)
    {
        _shell = shell;
        _services = services;

        // 采集期间每 5 秒拉一次最近记录（不采集时不查库，省得白连）
        _recordTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _recordTimer.Tick += async (_, _) =>
        {
            if (_shell.IsAcquiring)
            {
                await RefreshRecordsAsync();
            }
        };
        _recordTimer.Start();
    }

    /// <summary>实时卡片与曲线，跟随点位表重建（扁平一份，采集线程按地址查卡用）</summary>
    public ObservableCollection<TagViewModel> Tags { get; } = [];

    /// <summary>按存储组归拢的卡片，界面按这个分段展示、每段可折叠</summary>
    public ObservableCollection<DashboardGroupViewModel> Groups { get; } = [];

    [ObservableProperty] private string _recordsInfo = "未加载";

    /// <summary>
    /// 某个点位落到哪张表：按地址找生效配置里覆盖它的存储组，与落库用的是同一套判定。
    /// 返回 null 表示这个地址不在任何区段内 —— 既没采集也没落库，自然没有记录历史。
    /// </summary>
    public StorageGroup? ResolveStorageGroup(PlcTag tag) =>
        _shell.Config.Groups.FirstOrDefault(g => g.Covers(tag.Address));

    /// <summary>按当前点位表重建卡片（增删点位、切换机型后调用）</summary>
    public void Rebuild()
    {
        Tags.Clear();
        var tags = _shell.Config.Tags;
        for (int i = 0; i < tags.Count; i++)
        {
            Tags.Add(new TagViewModel(tags[i], Palette[i % Palette.Length]));
        }

        RefreshGrouping();
    }

    /// <summary>
    /// 按点位当前的「存储组」重排分组：只挪卡片、不重建，所以改地址或调区段不会把曲线清空。
    /// 组名就是 <see cref="PlcTag.Group"/>，与「存储映射」页的归组展示同一份数据。
    /// </summary>
    public void RefreshGrouping()
    {
        // 折叠状态按组名记着：重排后还原，改个点位不会把已经展开的组弹回去
        var collapsed = Groups.Where(g => !g.IsExpanded).Select(g => g.Name).ToHashSet();

        // 组头里显示的目标表取生效配置；只在草稿里存在的组还没提交，表名先留空
        var tables = _shell.Config.Groups.ToDictionary(g => g.Name, g => g.TableName);

        // 组序：先按生效配置的组序，再是配置里还没有的组名（按点位表顺序），最后是未归组
        var names = new List<string>();
        foreach (var group in _shell.Config.Groups)
        {
            names.Add(group.Name);
        }

        foreach (var name in Tags.Select(t => t.Tag.Group).Where(n => n.Length > 0))
        {
            if (!names.Contains(name))
            {
                names.Add(name);
            }
        }

        names.Add(string.Empty);

        Groups.Clear();
        foreach (var name in names)
        {
            var members = Tags.Where(t => t.Tag.Group == name).ToList();
            if (members.Count == 0)
            {
                continue;   // 空组不占地方；名下的点位加回来时这一段会自己回来
            }

            string display = name.Length == 0 ? UngroupedName : name;
            Groups.Add(new DashboardGroupViewModel(
                display, tables.GetValueOrDefault(name, string.Empty), members, !collapsed.Contains(display)));
        }
    }

    /// <summary>把卡片全部标灰（断开连接时调用）</summary>
    public void MarkAllOffline()
    {
        foreach (var tag in Tags)
        {
            tag.MarkOffline();
        }
    }

    /// <summary>
    /// 刷新落库提示（「存储映射」页底部那行）：查「存储映射」页里选中的那个组的目标表，数一下最近有多少条。
    /// 具体记录不在这儿展示了，看历史走卡片曲线上的弹窗。
    /// </summary>
    public async Task RefreshRecordsAsync()
    {
        var target = _shell.Storage.SelectedGroup ?? _shell.Storage.Groups.FirstOrDefault();
        if (target is null)
        {
            RecordsInfo = "还没有存储组";
            return;
        }

        try
        {
            var data = await _services.TableStore.QueryRecentAsync(target.TableName.Trim(), RecentRecordCount);
            RecordsInfo = $"组「{target.Name}」· 表 {target.TableName} · 最近 {data.Rows.Count} 条 · 更新于 {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            RecordsInfo = $"读取数据库失败：{ex.Message}";
        }
    }

    public void Stop() => _recordTimer.Stop();
}
