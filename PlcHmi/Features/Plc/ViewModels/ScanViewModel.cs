using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HmiCore;
using HmiCore.Models;
using PlcHmi.ViewModels;

namespace PlcHmi.Features.Plc.ViewModels;

/// <summary>
/// 「采集点位」页：扫寄存器试读哪些地址存在，把要采的地址勾进点位表，再设名称 / 单位 / 换算。
/// 点位表本身就是配置对象里的集合（改点位即改配置），保存与生效由外壳协调（见 MainViewModel.OnTagsChanged）。
/// </summary>
public partial class ScanViewModel : ObservableObject
{
    private readonly MainViewModel _shell;
    private readonly AppServices _services;
    private CancellationTokenSource? _scanCts;

    public ScanViewModel(MainViewModel shell, AppServices services)
    {
        _shell = shell;
        _services = services;
        AttachTagSubscriptions();

        // 扫描要看连没连上，而 RelayCommand 的 CanExecute 不会自己重算：
        // 不跟着外壳的连接状态手动通知一次，【开始扫描】会在启动时判过一次 false 之后一直灰着。
        _shell.PropertyChanged += OnShellPropertyChanged;
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsConnected))
        {
            ScanCommand.NotifyCanExecuteChanged();
        }
    }

    // ---------------- 扫描 ----------------

    [ObservableProperty] private string _scanStart = "40001";
    [ObservableProperty] private string _scanEnd = "40040";
    [ObservableProperty] private int _scanBatchSize = 10;
    [ObservableProperty] private string _scanStatus = "未扫描";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelScanCommand))]
    private bool _isScanning;

    /// <summary>扫描结果</summary>
    public ObservableCollection<ScanResult> ScanResults { get; } = [];

    [ObservableProperty] private string _tagStatus = "";

    /// <summary>点位表「类型」下拉的选项</summary>
    public IReadOnlyList<PlcDataTypeOption> DataTypeOptions { get; } = PlcValueCodec.DataTypeOptions;

    /// <summary>点位表「字序」下拉的选项</summary>
    public IReadOnlyList<WordOrderOption> WordOrderOptions { get; } = PlcValueCodec.WordOrderOptions;

    /// <summary>合并下拉的选项：只列占多个寄存器的类型（合并成 16 位没有意义）</summary>
    public IReadOnlyList<PlcDataTypeOption> MergeDataTypeOptions { get; } = PlcValueCodec.MultiRegisterTypeOptions;

    /// <summary>合并成哪种类型 —— 现场多数量是 32 位浮点占两个寄存器，所以默认它</summary>
    [ObservableProperty] private PlcDataType _mergeDataType = PlcDataType.Float32;

    /// <summary>合并出来的点位用哪种字序，合并后还能单独改</summary>
    [ObservableProperty] private WordOrder _mergeWordOrder = WordOrder.HighWordFirst;

    /// <summary>可编辑的采集点位表（直接就是配置对象里的集合）</summary>
    public ObservableCollection<PlcTag> TagConfigs => _shell.Config.Tags;

    /// <summary>换了机型：点位表实例换了，重新挂订阅并让界面重新绑定</summary>
    public void OnConfigChanged()
    {
        AttachTagSubscriptions();
        OnPropertyChanged(nameof(TagConfigs));
    }

    private bool CanScan() => _shell.IsConnected && !IsScanning;

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        var modbus = _shell.Modbus;
        if (modbus is null)
        {
            return;
        }

        if (!TryParseAddress(ScanStart, out var start) || !TryParseAddress(ScanEnd, out var end))
        {
            ScanStatus = "地址格式无效，示例：40001（人读地址）或 0（协议地址）";
            return;
        }

        if (end < start)
        {
            ScanStatus = "结束地址不能小于起始地址";
            return;
        }

        var batchSize = (ushort)Math.Clamp(ScanBatchSize, 1, 120);
        _scanCts = new CancellationTokenSource();
        IsScanning = true;
        ScanResults.Clear();
        ScanStatus = $"扫描中：{start} ~ {end}（协议地址），每段 {batchSize} 个";

        try
        {
            var progress = new Progress<string>(message => ScanStatus = message);
            var results = await _services.ScanService.ScanAsync(modbus, start, end, batchSize, progress, _scanCts.Token);

            foreach (var result in results)
            {
                ScanResults.Add(result);
            }

            int readable = results.Count(r => r.IsReadable);
            ScanStatus = $"扫描完成：共 {results.Count} 个地址，可读 {readable} 个，读异常 {results.Count - readable} 个";
        }
        catch (OperationCanceledException)
        {
            ScanStatus = "扫描已取消";
        }
        catch (Exception ex)
        {
            ScanStatus = $"扫描失败：{ex.Message}";
        }
        finally
        {
            IsScanning = false;
            _scanCts?.Dispose();
            _scanCts = null;
        }
    }

    private bool CanCancelScan() => IsScanning;

    [RelayCommand(CanExecute = nameof(CanCancelScan))]
    private void CancelScan() => _scanCts?.Cancel();

    /// <summary>连接断了：扫描没法继续，取消掉</summary>
    public void CancelIfScanning() => _scanCts?.Cancel();

    // ---------------- 点位表编辑 ----------------

    /// <summary>把勾选的可读地址加入点位表</summary>
    [RelayCommand]
    private void AddSelectedScanResults()
    {
        var selected = ScanResults.Where(r => r.IsSelected && r.IsReadable).ToList();
        if (selected.Count == 0)
        {
            TagStatus = "未勾选任何可读地址";
            return;
        }

        var tagConfigs = TagConfigs;
        int added = 0;
        foreach (var item in selected.Where(item => !IsRegisterUsed(tagConfigs, item.Address)))
        {
            tagConfigs.Add(new PlcTag(item.Address, $"点位{item.Address}", string.Empty, 1, 0, $"点位{item.Address}"));
            added++;
        }

        _shell.OnTagsChanged();
        TagStatus = $"已加入 {added} 个点位，跳过重复或已占用的 {selected.Count - added} 个。请按需修改名称与 Scale。";
    }

    /// <summary>
    /// 把点位表里选中的相邻几行合并成一个多寄存器点位（现场多数量是 32 位浮点占两个寄存器）。
    /// 类型与字序只能在这一侧定：扫描结果只看得见一个个 16 位寄存器，
    /// 哪两个合起来是一个浮点，只有人知道 —— 所以合并放在点位表上，不在扫描结果上。
    /// 合并 = 把第一行改成选定的宽度、删掉后面的行；名称/单位/换算沿用第一行。
    /// </summary>
    [RelayCommand]
    private void MergeSelectedTags(IList? selectedRows)
    {
        int count = PlcValueCodec.RegisterCount(MergeDataType);

        var targets = selectedRows?.OfType<PlcTag>().OrderBy(t => t.Address).ToList() ?? [];
        if (targets.Count != count)
        {
            TagStatus = $"{PlcValueCodec.Display(MergeDataType)}占用 {count} 个寄存器：" +
                $"请在点位表中选择 {count} 行相邻点位（当前已选 {targets.Count} 行）";
            return;
        }

        for (int i = 1; i < targets.Count; i++)
        {
            if (targets[i].Address != targets[i - 1].Address + 1)
            {
                TagStatus = "选中的点位地址不连续：合并后的点位从起始地址起连续占用";
                return;
            }
        }

        var first = targets[0];
        int endAddress = first.Address + count - 1;
        var tagConfigs = TagConfigs;

        // 合并后的区间不能压住别人（选中的这几行自己当然可以）
        var occupant = tagConfigs.FirstOrDefault(t =>
            !targets.Contains(t) && t.Address <= endAddress && first.Address <= t.EndAddress);
        if (occupant is not null)
        {
            TagStatus = $"合并后会与点位「{occupant.Name}」（{occupant.DisplayAddress}）地址重叠，请先删除该点位或将其改窄";
            return;
        }

        var merged = new PlcTag(first.Address, first.Name, first.Unit, first.Scale, first.Offset,
            first.ColumnName, MergeDataType, MergeWordOrder);

        int index = tagConfigs.IndexOf(first);
        foreach (var tag in targets)
        {
            tagConfigs.Remove(tag);
        }

        tagConfigs.Insert(Math.Min(index, tagConfigs.Count), merged);

        _shell.OnTagsChanged();
        TagStatus = $"已将 {targets.Count} 个点位合并为「{merged.Name}」" +
            $"（{PlcValueCodec.Display(MergeDataType)}，{PlcValueCodec.Display(MergeWordOrder)}，占用 {merged.DisplayAddress}）";
    }

    /// <summary>把多寄存器点位再拆回一个个 16 位点位（合并选错了能改回来）</summary>
    [RelayCommand]
    private void SplitSelectedTags(IList? selectedRows)
    {
        var targets = selectedRows?.OfType<PlcTag>().Where(t => t.RegisterCount > 1).ToList() ?? [];
        if (targets.Count == 0)
        {
            TagStatus = "请先选中至少一个 32/64 位点位（仅占多个寄存器的点位需要拆开）";
            return;
        }

        var tagConfigs = TagConfigs;
        foreach (var tag in targets)
        {
            int index = tagConfigs.IndexOf(tag);
            if (index < 0)
            {
                continue;
            }

            tagConfigs.RemoveAt(index);

            // 拆出来的寄存器按 16 位有符号看（类型待定），Scale/Offset 是原来那个合成量的，不带过去
            for (int i = 0; i < tag.RegisterCount; i++)
            {
                string name = $"{tag.Name}-{i + 1}";
                tagConfigs.Insert(index + i,
                    new PlcTag((ushort)(tag.Address + i), name, tag.Unit, 1, 0, name));
            }
        }

        _shell.OnTagsChanged();
        TagStatus = $"已拆开 {targets.Count} 个点位；拆出的寄存器按 16 位有符号读取，请核对类型与换算";
    }

    /// <summary>这个寄存器地址是不是已经被某个点位占着（多寄存器点位要把整段都算上）</summary>
    private static bool IsRegisterUsed(IEnumerable<PlcTag> tags, ushort address) =>
        tags.Any(t => t.Address <= address && address <= t.EndAddress);

    [RelayCommand]
    private void SelectAllReadable()
    {
        foreach (var result in ScanResults)
        {
            result.IsSelected = result.IsReadable;
        }
    }

    /// <summary>批量移除点位：参数是 DataGrid 当前选中的行（支持框选 / Ctrl / Shift 多选）</summary>
    [RelayCommand]
    private void RemoveSelectedTag(IList? selectedRows)
    {
        // SelectedItems 是随界面选择实时变化的活集合，先快照再删，避免边遍历边被改
        var targets = selectedRows?.OfType<PlcTag>().ToList() ?? [];
        if (targets.Count == 0)
        {
            TagStatus = "未选中任何点位";
            return;
        }

        var tagConfigs = TagConfigs;
        foreach (var tag in targets)
        {
            tagConfigs.Remove(tag);
        }

        _shell.OnTagsChanged();
        TagStatus = targets.Count == 1
            ? $"已删除点位「{targets[0].Name}」"
            : $"已删除 {targets.Count} 个点位";
    }

    [RelayCommand]
    private void SaveConfig()
    {
        try
        {
            // 顺带把「存储映射」页的组表草稿一起提交，两个页签的改动一起生效
            _shell.Storage.CommitMappingAndSave();
            TagStatus = $"配置已保存：{_shell.Store.FilePath}";
        }
        catch (Exception ex)
        {
            TagStatus = $"保存失败：{ex.Message}";
        }
    }

    // ---------------- 订阅：点位变了要重建卡片、刷新归组与建表预览 ----------------

    private void AttachTagSubscriptions()
    {
        var tagConfigs = TagConfigs;
        tagConfigs.CollectionChanged += OnTagConfigsChanged;
        foreach (var tag in tagConfigs)
        {
            tag.PropertyChanged += OnTagPropertyChanged;
        }
    }

    private void OnTagConfigsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (PlcTag tag in e.OldItems)
            {
                tag.PropertyChanged -= OnTagPropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (PlcTag tag in e.NewItems)
            {
                tag.PropertyChanged += OnTagPropertyChanged;
            }
        }

        _shell.Storage.RefreshAfterTagsChanged();
    }

    /// <summary>点位属性变了：地址会改变归组，名称/列名/换算会改变建表预览，都刷新一遍</summary>
    private void OnTagPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        _shell.Storage.RefreshAfterTagsChanged();
    }

    /// <summary>支持输入 40001 这样的人读地址，自动换算成 0-based 协议地址</summary>
    private static bool TryParseAddress(string? text, out ushort address)
    {
        address = 0;
        if (!int.TryParse(text?.Trim(), out int value) || value < 0 || value > 40001 + 65535)
        {
            return false;
        }

        if (value >= 40001)
        {
            value -= 40001;
        }

        if (value > 65535)
        {
            return false;
        }

        address = (ushort)value;
        return true;
    }
}
