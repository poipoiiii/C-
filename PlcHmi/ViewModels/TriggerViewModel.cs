using System.Collections;
using System.Collections.ObjectModel;
using System.Data;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlcHmi.Models;
using PlcHmi.Services;
using Serilog;

namespace PlcHmi.ViewModels;

/// <summary>
/// 「触发记录」页：规则表 + 轮询间隔 + 最近的触发记录。
/// 监听的启停跟连接绑定（由外壳负责），这里只负责界面上那一份状态：
/// 规则增删、报警提示、最近记录查询。
/// </summary>
public partial class TriggerViewModel : ObservableObject
{
    /// <summary>最近触发记录一次取多少条</summary>
    private const int RecentRecordCount = 50;

    private readonly MainViewModel _shell;
    private readonly AppServices _services;
    private readonly DispatcherTimer _recordTimer;

    /// <summary>已经报警、还没恢复的规则名（报警条只列谁卡住了）</summary>
    private readonly HashSet<string> _stuckRules = [];

    private int _triggerCount;

    public TriggerViewModel(MainViewModel shell, AppServices services)
    {
        _shell = shell;
        _services = services;

        // 监听期间每 5 秒刷一次最近触发记录
        _recordTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _recordTimer.Tick += async (_, _) =>
        {
            if (_shell.TriggerWatch is not null)
            {
                await RefreshTriggerRecordsAsync();
            }
        };
        _recordTimer.Start();
    }

    /// <summary>触发记录规则（直接就是配置对象里的集合）</summary>
    public ObservableCollection<TriggerRule> Triggers => _shell.Config.Triggers;

    [ObservableProperty] private TriggerRule? _selectedTrigger;

    /// <summary>信号轮询间隔（毫秒）：触发频繁就调小，默认 100</summary>
    [ObservableProperty] private int _triggerPollIntervalMs = 100;

    [ObservableProperty] private string _triggerStatus = "未监听";
    [ObservableProperty] private string _triggerAlarm = string.Empty;
    [ObservableProperty] private DataView? _triggerView;
    [ObservableProperty] private string _triggerRecordsInfo = "未加载";

    /// <summary>换了机型：规则集合实例换了，重新绑定并复位界面状态</summary>
    public void OnConfigChanged()
    {
        SelectedTrigger = null;
        _triggerCount = 0;
        _stuckRules.Clear();
        TriggerAlarm = string.Empty;
        OnPropertyChanged(nameof(Triggers));
    }

    // ---------------- 外壳（监听生命周期）回调 ----------------

    public void OnWatchStarted()
    {
        TriggerStatus = $"监听中：{Triggers.Count(t => t.Enabled)} 条规则 · {TriggerPollIntervalMs} ms 轮询";
    }

    public void OnWatchStopped()
    {
        _stuckRules.Clear();
        TriggerAlarm = string.Empty;
        TriggerStatus = "未监听";
    }

    /// <summary>该记一条记录了：先刷界面，落库丢到后台，别拖慢 100ms 的轮询</summary>
    public void OnTriggered(TriggerRule rule, DateTime timestamp)
    {
        Log.Information("触发：{Rule} @ {Timestamp:HH:mm:ss}（信号 {Signal}）", rule.Name, timestamp, rule.DisplaySignal);

        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            _triggerCount++;
            TriggerStatus = $"监听中：{Triggers.Count(t => t.Enabled)} 条规则 · 累计 {_triggerCount} 次 · " +
                            $"最近「{rule.Name}」@ {timestamp:HH:mm:ss}";
        });

        _ = PersistTriggerAsync(rule, timestamp);
    }

    private async Task PersistTriggerAsync(TriggerRule rule, DateTime timestamp)
    {
        try
        {
            await _services.TriggerLogStore.InsertAsync(
                TriggerLogStore.DefaultTableName, rule, timestamp, _shell.MachineNo);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "触发记录落库失败：{Rule}", rule.Name);
            _ = Application.Current.Dispatcher.BeginInvoke(() => TriggerAlarm = $"触发记录落库失败：{ex.Message}");
        }
    }

    public void OnHandshakeCompleted(TriggerRule rule)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            _stuckRules.Remove(rule.Name);
            UpdateAlarmText();
        });
    }

    public void OnAlarm(TriggerRule rule, string message)
    {
        Log.Warning("触发报警：{Rule} —— {Message}", rule.Name, message);

        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            _stuckRules.Add(rule.Name);
            UpdateAlarmText();
            TriggerStatus = message;      // 详细原因放状态栏，卡在哪条规则的「状态」列也能看到
        });
    }

    /// <summary>监听没起来（没有启用的规则）：状态栏说明一句</summary>
    public void OnWatchSkipped()
    {
        TriggerStatus = Triggers.Count == 0 ? "没有触发规则" : "触发规则全部停用";
    }

    /// <summary>报警条只列"哪几条规则卡住"，详细原因在状态栏与规则表的「状态」列</summary>
    private void UpdateAlarmText()
    {
        TriggerAlarm = _stuckRules.Count == 0
            ? string.Empty
            : $"握手卡住：{string.Join("、", _stuckRules)} —— 等 PLC 清信号，未强行复位";
    }

    partial void OnTriggerPollIntervalMsChanged(int value)
    {
        if (_shell.TriggerWatch is { } watch)
        {
            watch.PollIntervalMs = value;
            TriggerStatus = $"监听中：轮询间隔已改为 {value} ms";
        }
    }

    // ---------------- 规则增删与记录查询 ----------------

    [RelayCommand]
    private async Task AddTriggerRuleAsync()
    {
        int index = Triggers.Count + 1;
        while (Triggers.Any(t => string.Equals(t.Name, $"触发{index}", StringComparison.OrdinalIgnoreCase)))
        {
            index++;
        }

        var rule = new TriggerRule($"触发{index}", 0, 0);
        Triggers.Add(rule);
        SelectedTrigger = rule;
        _shell.SaveConfigNow();
        await _shell.RestartTriggerWatchAsync();

        TriggerStatus = $"已新增规则「{rule.Name}」：把信号/应答地址改成现场实际地址（0 基协议地址）";
    }

    [RelayCommand]
    private async Task RemoveTriggerRuleAsync(IList? selectedRows)
    {
        // SelectedItems 是活集合，先快照再删
        var targets = selectedRows?.OfType<TriggerRule>().ToList() ?? [];
        if (targets.Count == 0)
        {
            TriggerStatus = "没有选中规则";
            return;
        }

        foreach (var rule in targets)
        {
            Triggers.Remove(rule);
        }

        _shell.SaveConfigNow();
        await _shell.RestartTriggerWatchAsync();
        TriggerStatus = $"已删除 {targets.Count} 条触发规则";
    }

    [RelayCommand]
    private void SaveTriggerRules()
    {
        try
        {
            _shell.SaveConfigNow();
            TriggerStatus = $"触发规则已保存：{_shell.Store.FilePath}";
        }
        catch (Exception ex)
        {
            TriggerStatus = $"保存失败：{ex.Message}";
        }
    }

    [RelayCommand]
    public async Task RefreshTriggerRecordsAsync()
    {
        try
        {
            var data = await _services.TriggerLogStore.QueryRecentAsync(TriggerLogStore.DefaultTableName, RecentRecordCount);
            TriggerView = data.DefaultView;
            TriggerRecordsInfo =
                $"最近 {data.Rows.Count} 条 · 表 {TriggerLogStore.DefaultTableName} · 更新于 {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            TriggerRecordsInfo = $"读取触发记录失败：{ex.Message}";
        }
    }

    public void Stop() => _recordTimer.Stop();
}
