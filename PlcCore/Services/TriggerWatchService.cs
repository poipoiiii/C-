using NModbus;
using PlcCore.Services;
using PlcCore.Models;

namespace PlcCore.Services;

/// <summary>
/// 触发记录监听：高频轮询信号寄存器，按握手时序推进，模拟上升沿。
/// 为什么不搭在采集循环上：一次握手要推进两步（信号=1、信号=0），
/// 现场每秒 1~2 次触发时，靠 1 秒一轮的采集根本跟不上、会丢事件。
/// 所以独立高频轮询（默认 100ms）—— 信号位往往只有一两个寄存器，读起来很便宜。
/// 连接和采集共用一条，读写互斥由 <see cref="ModbusService"/> 内部保证。
/// </summary>
public sealed class TriggerWatchService : IDisposable
{
    /// <summary>握手推进到哪一步</summary>
    private enum Step
    {
        /// <summary>等 PLC 把信号置 1</summary>
        WaitingSignal,

        /// <summary>已应答，等 PLC 把信号清 0</summary>
        WaitingSignalClear
    }

    private readonly IModbusClient _modbus;
    private readonly IReadOnlyList<TriggerRule> _rules;

    private readonly Dictionary<TriggerRule, Step> _steps = [];
    private readonly Dictionary<TriggerRule, DateTime> _ackSentAt = [];

    /// <summary>已经报过警的规则，避免每个轮询周期刷一遍；恢复正常后移除</summary>
    private readonly HashSet<TriggerRule> _alarmed = [];

    private CancellationTokenSource? _cts;
    private Task? _loop;

    public TriggerWatchService(IModbusClient modbus, IReadOnlyList<TriggerRule> rules)
    {
        _modbus = modbus;
        _rules = rules;
    }

    /// <summary>信号轮询间隔（毫秒）。触发越频繁就要越小，默认 100ms</summary>
    public int PollIntervalMs { get; set; } = 100;

    /// <summary>该记一条记录了：在后台线程触发，界面负责落库与刷新</summary>
    public event Action<TriggerRule, DateTime>? Triggered;

    /// <summary>一条完整的握手走完了（应答已清零，可以接下一次触发）</summary>
    public event Action<TriggerRule>? HandshakeCompleted;

    /// <summary>业务报警（握手超时、信号地址读不到），同一条规则恢复前只报一次</summary>
    public event Action<TriggerRule, string>? Alarm;

    /// <summary>传输层故障，在后台线程触发</summary>
    public event Action<string>? Failed;

    public bool IsRunning => _loop is { IsCompleted: false };

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        var cts = _cts;
        var loop = _loop;
        if (cts is null || loop is null)
        {
            return;
        }

        cts.Cancel();
        try
        {
            await loop;
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }

        cts.Dispose();
        _cts = null;
        _loop = null;

        _steps.Clear();
        _ackSentAt.Clear();
        _alarmed.Clear();
        foreach (var rule in _rules)
        {
            rule.StatusText = "未启动";
        }
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            bool transportFailed = false;

            foreach (var rule in _rules.Where(r => r.Enabled))
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    AdvanceHandshake(rule);
                }
                catch (SlaveException ex)
                {
                    rule.StatusText = $"地址异常：Modbus 异常码 {ex.SlaveExceptionCode}";
                    RaiseAlarm(rule, $"信号寄存器 {rule.DisplaySignal} 读不到：" +
                                     $"Modbus 异常码 {ex.SlaveExceptionCode}（地址不存在？）");
                }
                catch (Exception ex)
                {
                    // 传输层故障：所有规则都会失败，报一次就够，并放慢节奏免得刷屏
                    Failed?.Invoke(ex.Message);
                    transportFailed = true;
                    break;
                }
            }

            try
            {
                await Task.Delay(transportFailed ? 1000 : Math.Max(PollIntervalMs, 10), token);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>推进一条规则的握手状态机，一次只走一步</summary>
    private void AdvanceHandshake(TriggerRule rule)
    {
        short signal = _modbus.ReadRegister(rule.SignalAddress);

        if (!_steps.TryGetValue(rule, out var step))
        {
            // 首次进入：信号已经置位说明是上一次运行留下的（或刚好赶上），
            // 直接补完握手把它复位，但不再记一条 —— 免得每次重启都多一条记录
            if (signal != 0)
            {
                _modbus.WriteSingleRegister(rule.AckAddress, 1);
                _steps[rule] = Step.WaitingSignalClear;
                _ackSentAt[rule] = DateTime.Now;
                rule.StatusText = "启动时信号已置位：补完握手复位（不记录）";
                return;
            }

            _steps[rule] = Step.WaitingSignal;
            rule.StatusText = "等待信号";
            return;
        }

        switch (step)
        {
            case Step.WaitingSignal:
                if (signal == 0)
                {
                    rule.StatusText = "等待信号";
                    return;
                }

                // 先记录再应答：记录一定发生在应答之前，PLC 才可能在后面重新置位信号
                Triggered?.Invoke(rule, DateTime.Now);
                _modbus.WriteSingleRegister(rule.AckAddress, 1);
                _steps[rule] = Step.WaitingSignalClear;
                _ackSentAt[rule] = DateTime.Now;
                _alarmed.Remove(rule);
                rule.StatusText = "已应答，等 PLC 清信号";
                return;

            case Step.WaitingSignalClear:
                if (signal != 0)
                {
                    var waited = DateTime.Now - _ackSentAt[rule];
                    if (waited.TotalMilliseconds >= rule.TimeoutMs)
                    {
                        rule.StatusText = $"超时：等 PLC 清信号已 {waited.TotalSeconds:F0} 秒";
                        RaiseAlarm(rule, $"规则「{rule.Name}」已应答 {waited.TotalSeconds:F0} 秒，" +
                                         $"PLC 还没把信号 {rule.DisplaySignal} 清 0，保持等待（不强行复位）");
                    }

                    return;
                }

                _modbus.WriteSingleRegister(rule.AckAddress, 0);
                _steps[rule] = Step.WaitingSignal;
                _ackSentAt.Remove(rule);
                _alarmed.Remove(rule);
                rule.StatusText = "等待信号";
                HandshakeCompleted?.Invoke(rule);
                return;
        }
    }

    private void RaiseAlarm(TriggerRule rule, string message)
    {
        if (_alarmed.Add(rule))
        {
            Alarm?.Invoke(rule, message);
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
    }
}
