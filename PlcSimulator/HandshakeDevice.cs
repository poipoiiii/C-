namespace PlcSimulator;

/// <summary>握手测试设备的寄存器约定（协议地址）</summary>
internal sealed record HandshakeSpec(ushort SignalAddress, ushort AckAddress, int IntervalMs);

/// <summary>
/// 模拟 PLC 侧的握手时序，用来配合上位机的「触发记录」验证整条链路：
///   置信号 1 → 等上位机把应答写 1 → 清信号 → 等上位机把应答写 0 → 间隔到了再触发。
/// 这套顺序和上位机的状态机严格对应，所以任何一侧错位都会立刻卡住并能看出来。
/// 注意：信号/应答寄存器必须排在每秒刷新的数据区之外，否则会被数据刷新冲掉。
/// </summary>
internal sealed class HandshakeDevice
{
    private enum Stage
    {
        /// <summary>等触发时机（同时要确认上一轮应答已清零）</summary>
        WaitingToRaise,

        /// <summary>已置信号，等上位机应答</summary>
        WaitingAck,

        /// <summary>已清信号，等上位机把应答清零收尾</summary>
        WaitingAckClear
    }

    /// <summary>前几个周期逐步打印，方便肉眼对时序；之后再触发就只按周期汇总，免得刷屏</summary>
    private const int VerboseCycles = 5;
    private const int SummaryEveryCycles = 10;

    private readonly byte _unitId;
    private readonly string _name;
    private readonly HandshakeSpec _spec;
    private readonly LimitedSlaveDataStore _store;

    private Stage _stage = Stage.WaitingToRaise;
    private DateTime _nextTriggerAt = DateTime.Now;
    private int _cycles;
    private int _missedAckWarnings;

    public HandshakeDevice(byte unitId, string name, HandshakeSpec spec, LimitedSlaveDataStore store)
    {
        _unitId = unitId;
        _name = name;
        _spec = spec;
        _store = store;
    }

    public int CompletedCycles => _cycles;

    public string Describe() =>
        $"从站 {_unitId} 握手测试：信号 {Display(_spec.SignalAddress)} 置 1 → 上位机应答 {Display(_spec.AckAddress)}=1 → " +
        $"本设备清信号 → 上位机应答清零；每 {_spec.IntervalMs} ms 触发一次";

    /// <summary>推进一次状态机。仿真循环每 100ms 调一次，比上位机的轮询间隔快，才不会成为瓶颈</summary>
    public void Step(DateTime now)
    {
        switch (_stage)
        {
            case Stage.WaitingToRaise:
                // 上一轮的应答没清零就不开始新的一轮 —— 现场 PLC 也是这么被约束的
                if (now < _nextTriggerAt || Read(_spec.AckAddress) != 0)
                {
                    return;
                }

                Write(_spec.SignalAddress, 1);
                _stage = Stage.WaitingAck;
                Log($"置信号 {Display(_spec.SignalAddress)}=1，等上位机应答");
                return;

            case Stage.WaitingAck:
                // 自检：信号必须一直是 1。万一被每秒的数据刷新冲掉，这里会立刻暴露 ——
                // 信号/应答必须排在数据区之外，这条检查也是防止以后把地址改坏。
                if (Read(_spec.SignalAddress) != 1)
                {
                    Log("✗ 信号被冲掉了：检查信号地址是否落在每秒刷新的数据区里");
                    _stage = Stage.WaitingToRaise;
                    _nextTriggerAt = now.AddMilliseconds(_spec.IntervalMs);
                    return;
                }

                if (Read(_spec.AckAddress) != 1)
                {
                    // 每 5 秒（50 个 100ms 周期）提醒一次，别把控制台刷满
                    if (++_missedAckWarnings % 50 == 0)
                    {
                        Log($"⚠ 已等 {(now - _nextTriggerAt).TotalSeconds:F1} 秒还没收到应答，检查上位机是否在监听");
                    }

                    return;
                }

                Write(_spec.SignalAddress, 0);
                _stage = Stage.WaitingAckClear;
                Log($"收到应答 {Display(_spec.AckAddress)}=1 → 清信号 {Display(_spec.SignalAddress)}=0");
                return;

            case Stage.WaitingAckClear:
                if (Read(_spec.AckAddress) != 0)
                {
                    return;
                }

                _stage = Stage.WaitingToRaise;
                _cycles++;
                _missedAckWarnings = 0;
                _nextTriggerAt = now.AddMilliseconds(_spec.IntervalMs);

                if (_cycles <= VerboseCycles)
                {
                    Log($"第 {_cycles} 次握手完成，{_spec.IntervalMs} ms 后下一次");
                }
                else if (_cycles % SummaryEveryCycles == 0)
                {
                    Log($"已累计完成 {_cycles} 次握手（正常）");
                }

                return;
        }
    }

    private ushort Read(ushort address) => _store.HoldingRegisters.ReadPoints(address, 1)[0];

    private void Write(ushort address, ushort value) => _store.HoldingRegisters.WritePoints(address, [value]);

    private void Log(string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {_unitId}# {message}");

    /// <summary>协议地址 0 对应人读地址 40001</summary>
    private static string Display(ushort address) => $"4{address + 1:D4}";
}
