using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlcCore.Models;
using PlcCore.Services;

namespace PlcHmi.ViewModels;

/// <summary>
/// 「存储映射」页：一段寄存器区段 → 一张表。组表在界面上是**草稿**，
/// 点【建表 / 校验结构】或【保存配置】才提交成生效配置 —— 免得边打字边把数据写到还不存在的表上。
/// 点位的归组展示也由这里刷新：地址落在哪个区段就归哪个组，区段是唯一判定依据。
/// </summary>
public partial class StorageViewModel : ObservableObject
{
    private readonly MainViewModel _shell;
    private readonly AppServices _services;

    /// <summary>正在刷归组展示：改 PlcTag.Group 会反过来触发属性变更，挡一下重入</summary>
    private bool _refreshingTagGroups;

    public StorageViewModel(MainViewModel shell, AppServices services)
    {
        _shell = shell;
        _services = services;

        ModeOptions =
        [
            new StorageModeOption(StorageMode.Wide, "宽表：一行一个时刻（时间 + 每个点位一列）"),
            new StorageModeOption(StorageMode.Long, "长表：一行一个点位（时间/地址/原始值/工程值）")
        ];

        Groups.CollectionChanged += OnGroupsChanged;
        RebuildFromConfig();
    }

    /// <summary>界面上的组表草稿，点【建表 / 校验结构】或【保存配置】才提交成生效配置</summary>
    public ObservableCollection<StorageGroup> Groups { get; } = [];

    [ObservableProperty] private StorageGroup? _selectedGroup;
    [ObservableProperty] private string _storageStatus = "";
    [ObservableProperty] private string _createScriptPreview = "";
    [ObservableProperty] private string _effectiveStorageText = "";

    /// <summary>库里已有的表，供目标表下拉框选择</summary>
    public ObservableCollection<string> Tables { get; } = [];

    public IReadOnlyList<StorageModeOption> ModeOptions { get; }

    /// <summary>页面底部那行「最近 N 条」提示取自看板：两页看的是同一条落库状态（具体记录去卡片曲线的弹窗里看）</summary>
    public DashboardViewModel Dashboard => _shell.Dashboard;

    /// <summary>按生效配置重建草稿：启动、切换机型后调用</summary>
    public void RebuildFromConfig()
    {
        Groups.Clear();
        foreach (var group in _shell.Config.Groups)
        {
            Groups.Add(group.Clone());
        }

        SelectedGroup = Groups.FirstOrDefault();
        RefreshTagGroups();
        UpdateScriptPreview();
        UpdateEffectiveStorageText();
    }

    /// <summary>点位表变了（增删/改名/改地址）：归组展示与建表预览都要跟着刷新</summary>
    public void RefreshAfterTagsChanged()
    {
        RefreshTagGroups();
        UpdateScriptPreview();
    }

    // ---------------- 归组展示 ----------------

    /// <summary>
    /// 重新推导每个点位归到哪个组：地址落在哪个区段就归哪个组。
    /// 区段是唯一判定依据，所以不存在「点位指向一个不存在的组」这种事。
    /// </summary>
    public void RefreshTagGroups()
    {
        if (_refreshingTagGroups)
        {
            return;
        }

        _refreshingTagGroups = true;
        try
        {
            foreach (var tag in _shell.Config.Tags)
            {
                tag.Group = ResolveGroupName(tag.Address);
            }
        }
        finally
        {
            _refreshingTagGroups = false;
        }

        // 归组变了，看板的分段也要跟着挪：只重排卡片不重建，曲线不会丢。
        // 外壳构造本页时看板还没建出来（见 MainViewModel 构造函数顺序），这一步要能跳过。
        _shell.Dashboard?.RefreshGrouping();
    }

    /// <summary>地址落在哪个区段；不在任何区段内返回空 —— 这种点位既不会采集也不会落库</summary>
    private string ResolveGroupName(ushort address)
    {
        foreach (var group in Groups)
        {
            if (group.Covers(address))
            {
                return group.Name;
            }
        }

        return string.Empty;
    }

    /// <summary>落在某个区段内的点位（区段是唯一的判定依据，不看点位上的归组缓存）</summary>
    private List<PlcTag> TagsOfGroup(StorageGroup group) =>
        [.. _shell.Config.Tags.Where(t => group.Covers(t.Address))];

    // ---------------- 组表编辑与校验 ----------------

    /// <summary>组表草稿变了：补/退订阅，并刷新归组与建表预览</summary>
    private void OnGroupsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (StorageGroup group in e.OldItems)
            {
                group.PropertyChanged -= OnGroupPropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (StorageGroup group in e.NewItems)
            {
                group.PropertyChanged += OnGroupPropertyChanged;
            }
        }

        RefreshTagGroups();
        UpdateScriptPreview();
    }

    /// <summary>组的区段/组名/表名任一改动，点位的归组和建表预览都要跟着变</summary>
    private void OnGroupPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        RefreshTagGroups();
        UpdateScriptPreview();
    }

    [RelayCommand]
    private void AddGroup()
    {
        int index = Groups.Count + 1;
        while (Groups.Any(g => string.Equals(g.Name, $"组{index}", StringComparison.OrdinalIgnoreCase)))
        {
            index++;
        }

        // 新区段接在所有现有区段之后，默认 20 个寄存器（现场常见的一档）
        int start = Groups.Count == 0 ? 0 : Groups.Max(g => g.EndAddress) + 1;
        Groups.Add(new StorageGroup($"组{index}", $"PlcData{index}", StorageMode.Wide, (ushort)start, 20));
        SelectedGroup = Groups[^1];
        StorageStatus = $"已新增组「组{index}」，区段 {Groups[^1].DisplayRange}：接着设目标表，并把区段改成本台设备实际的地址段";
    }

    [RelayCommand]
    private void RemoveGroup()
    {
        if (SelectedGroup is null)
        {
            StorageStatus = "先选中要删除的组";
            return;
        }

        if (Groups.Count <= 1)
        {
            StorageStatus = "至少要保留一个存储组";
            return;
        }

        var removedName = SelectedGroup.Name;
        int affected = TagsOfGroup(SelectedGroup).Count;
        Groups.Remove(SelectedGroup);
        SelectedGroup = Groups[0];

        StorageStatus = affected > 0
            ? $"已删除组「{removedName}」：原区段内的 {affected} 个点位现在显示为「未归组」，改成别的组的区段覆盖它们即可"
            : $"已删除组「{removedName}」";
    }

    /// <summary>
    /// 校验组表草稿：规则见 <see cref="StorageGroupValidator"/>。
    /// 组名/表名会在校验里被 Trim，所以校验通过后就不用再处理空格了。
    /// </summary>
    private void ValidateGroups() => StorageGroupValidator.Validate(Groups, _shell.Config.Tags);

    /// <summary>把界面上的组表草稿提交成生效配置（不落盘）</summary>
    public void CommitMapping()
    {
        _shell.Config.Groups.Clear();
        foreach (var group in Groups)
        {
            _shell.Config.Groups.Add(group.Clone());
        }

        UpdateEffectiveStorageText();
    }

    /// <summary>草稿提交 + 落盘</summary>
    public void CommitMappingAndSave()
    {
        CommitMapping();
        _shell.SaveConfigNow();
    }

    private void UpdateEffectiveStorageText()
    {
        EffectiveStorageText = _shell.Config.Groups.Count == 0
            ? "当前没有存储组"
            : "当前写入：" + string.Join("　|　", _shell.Config.Groups.Select(g =>
                $"{g.Name} {g.DisplayRange} → {g.TableName}（{(g.Mode == StorageMode.Wide ? "宽表" : "长表")}）"));
    }

    /// <summary>附加列摘要：Id / 时间戳哪些开着（这两列由用户自己决定加不加）</summary>
    private static string DescribeShape(StorageGroup group) => (group.UseIdentityColumn, group.UseTimestampColumn) switch
    {
        (true, true) => "含 Id + 时间戳",
        (true, false) => "只含 Id",
        (false, true) => "只含时间戳",
        _ => "不含附加列"
    };

    /// <summary>建表语句预览：每个组一段，段首标出组名与点位数量</summary>
    public void UpdateScriptPreview()
    {
        try
        {
            var blocks = new List<string>();
            foreach (var group in Groups)
            {
                var tags = TagsOfGroup(group);
                blocks.Add($"-- 组「{group.Name}」→ 表 {group.TableName}" +
                           $"（{tags.Count} 个点位 · {DescribeShape(group)}）\r\n" +
                           DynamicTableStore.BuildCreateScript(group.TableName, group.Mode, tags, group.Shape));
            }

            CreateScriptPreview = blocks.Count > 0 ? string.Join("\r\n\r\n", blocks) : "（还没有存储组）";
        }
        catch (Exception ex)
        {
            CreateScriptPreview = $"（暂时无法生成建表语句：{ex.Message}）";
        }
    }

    // ---------------- 建表与表清单 ----------------

    [RelayCommand]
    public async Task RefreshTablesAsync()
    {
        try
        {
            var tables = await _services.TableStore.ListTablesAsync();
            Tables.Clear();
            foreach (var table in tables)
            {
                Tables.Add(table);
            }
        }
        catch (Exception ex)
        {
            StorageStatus = $"读取表清单失败：{ex.Message}";
        }
    }

    /// <summary>按组建表（已存在则补列）：先校验，全部建完才把组表提交生效</summary>
    [RelayCommand]
    private async Task CreateTableAsync()
    {
        try
        {
            StorageStatus = "处理中…";
            ValidateGroups();

            var notes = new List<string>();
            foreach (var group in Groups)
            {
                var tags = TagsOfGroup(group);
                bool created = await _services.TableStore.CreateTableIfMissingAsync(
                    group.TableName, group.Mode, tags, group.Shape);
                var sync = group.Mode == StorageMode.Wide
                    ? await _services.TableStore.EnsureWideColumnsAsync(group.TableName, tags, group.Shape)
                    : new DynamicTableStore.WideColumnSyncResult([], [], []);

                notes.Add(DescribeGroupSync(group, tags.Count, created, sync));
            }

            CommitMappingAndSave();
            _ = RefreshTablesAsync();

            StorageStatus = string.Join("；", notes);
            await _shell.Dashboard.RefreshRecordsAsync();
        }
        catch (Exception ex)
        {
            StorageStatus = $"建表失败：{ex.Message}";
        }
    }

    /// <summary>把一个组的建表结果压成一句话</summary>
    private static string DescribeGroupSync(
        StorageGroup group, int tagCount, bool created, DynamicTableStore.WideColumnSyncResult sync)
    {
        var note = new StringBuilder($"组「{group.Name}」");
        note.Append(created ? $"已创建表 {group.TableName}" : $"表 {group.TableName} 已存在，结构已校验");
        note.Append($"（{tagCount} 个点位）");

        if (sync.Added.Count > 0)
        {
            note.Append($"，补齐列：{string.Join("、", sync.Added)}");
        }

        if (sync.Relaxed.Count > 0)
        {
            note.Append($"，改为可空（允许个别点位读失败）：{string.Join("、", sync.Relaxed)}");
        }

        if (sync.BlockingNotNull.Count > 0)
        {
            note.Append($"；⚠ 表里这些非空列没人写，插入会失败，建议换新表名或把它们改成可空 / 删掉：" +
                        string.Join("、", sync.BlockingNotNull));
        }

        return note.ToString();
    }
}
