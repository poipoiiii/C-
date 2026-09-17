using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlcHmi.Models;
using PlcHmi.Services;

namespace PlcHmi.ViewModels;

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
            ScanStatus = "地址格式不对，示例：40001（人读地址）或 0（协议地址）";
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
            TagStatus = "没有勾选可读的地址";
            return;
        }

        var tagConfigs = TagConfigs;
        int added = 0;
        foreach (var item in selected.Where(item => tagConfigs.All(t => t.Address != item.Address)))
        {
            tagConfigs.Add(new PlcTag(item.Address, $"点位{item.Address}", string.Empty, 1, 0, $"点位{item.Address}"));
            added++;
        }

        _shell.OnTagsChanged();
        TagStatus = $"已加入 {added} 个点位，跳过重复 {selected.Count - added} 个。记得按需改名称与 Scale。";
    }

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
            TagStatus = "没有选中点位";
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
