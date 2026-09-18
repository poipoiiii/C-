using NModbus;
using PlcCore.Services;
using PlcCore.Models;

namespace PlcCore.Services;

/// <summary>一个读取区段：从 Start 起连续读 Count 个寄存器。对应界面上一个存储组的区段。</summary>
public sealed record ReadRange(ushort Start, ushort Count);

/// <summary>
/// 采集循环：按周期读 Modbus → 换算 →（可选）落库 → 通知界面。
/// 两类异常分开处理：
/// - 单个地址读失败（Modbus 异常码，比如地址超出从站范围）→ 只把该点位标记失败，其余点位照常采集落库；
/// - 传输层异常（断网、连接被断开）→ 整轮作废并上报，因为这时候重试也没意义。
/// </summary>
public sealed class PlcAcquisitionService : IDisposable     /// 明确禁止继承
{
    /// <summary>一次最多读多少个寄存器：Modbus 单次读保持寄存器的上限就是 125</summary>
    private const int MaxRegistersPerRead = 125;

    private readonly IModbusClient _modbus;
    private readonly IReadOnlyList<PlcTag> _tags;       // _代表只能类内部访问

    /// <summary>要读的区段（一个存储组一段），顺序固定，构造时快照</summary>
    private readonly IReadOnlyList<ReadRange> _ranges;

    /// <summary>地址 → 该地址上的点位：区段里没有点位的地址读回来直接丢掉</summary>
    private readonly Dictionary<ushort, List<PlcTag>> _tagsByAddress;

    private CancellationTokenSource? _cts;
    private Task? _loop;

    public PlcAcquisitionService(IModbusClient modbus, IReadOnlyList<PlcTag> tags, IReadOnlyList<ReadRange> ranges)
    {
        _modbus = modbus;
        _tags = tags;
        _ranges = ranges;
        _tagsByAddress = tags
            .GroupBy(t => t.Address)
            .ToDictionary(g => g.Key, g => g.ToList());
    }

    /// <summary>采集周期（毫秒），运行中修改即时生效</summary>
    public int IntervalMs { get; set; } = 3000;

    /// <summary>是否把采集结果写入数据库</summary>
    public bool PersistEnabled { get; set; } = true;

    /// <summary>
    /// 落库动作：由界面按当前存储映射注入（写哪张表、长表还是宽表）。
    /// 只会拿到读成功的样本；采集服务本身不关心存储细节。
    /// </summary>
    public Func<IReadOnlyList<Sample>, Task>? PersistAsync { get; set; }        ///func代表接受一个只读列表，返回一个任务句柄

    /// <summary>一轮采集结束（含成功样本与失败点位），在后台线程触发</summary>
    public event Action<AcquisitionBatch>? BatchCompleted;

    /// <summary>传输层故障（断网/连接断开），整轮作废，在后台线程触发</summary>
    public event Action<string>? Failed;

    /// <summary>落库失败回调（在后台线程触发），不影响实时显示</summary>
    public event Action<string>? PersistFailed;

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
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var batch = ReadAll();

                // 先刷界面：落库出问题不能挡住实时显示
                BatchCompleted?.Invoke(batch);

                // 一个点位都没读到时不用往表里塞空行
                if (PersistEnabled && PersistAsync is not null && batch.Samples.Count > 0)
                {
                    try
                    {
                        await PersistAsync(batch.Samples);
                    }
                    catch (Exception ex)
                    {
                        PersistFailed?.Invoke(ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                // 走到这里一般是传输层故障（连接断开等）
                Failed?.Invoke(ex.Message);
            }

            try
            {
                await Task.Delay(IntervalMs, token);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// 按存储组的区段读：一个区段就是一组数据（一张表），从起始地址起连续读 Count 个寄存器，
    /// 段内没点位的地址读回来直接丢掉。现场就是一个区段读一次，这里额外按 Modbus 的单次读上限
    /// （125 个）拆开，所以区段上千个寄存器也是一轮轮询内读完，不会退化成逐点读。
    /// 区段里有不存在的地址会导致整段失败（异常码 02），那时回退到段内点位逐点读，
    /// 好点位照常入库；传输层错误直接抛出，由外层按整轮故障处理。
    /// </summary>
    private AcquisitionBatch ReadAll()
    {
        var samples = new List<Sample>(_tags.Count);
        var failures = new List<TagFailure>();

        foreach (var range in _ranges)
        {
            for (int offset = 0; offset < range.Count; offset += MaxRegistersPerRead)
            {
                var count = (ushort)Math.Min(MaxRegistersPerRead, range.Count - offset);
                ReadChunk((ushort)(range.Start + offset), count, samples, failures);
            }
        }

        return new AcquisitionBatch(samples, failures);
    }

    /// <summary>读一段连续寄存器，把落在段内的点位换算成样本；整段失败时回退到段内逐点读</summary>
    private void ReadChunk(ushort start, ushort count, List<Sample> samples, List<TagFailure> failures)
    {
        try
        {
            var values = _modbus.ReadRegisters(start, count);

            // 同一段共用一个时间戳：批量读本来就是在同一时刻取回的
            var timestamp = DateTime.Now;
            for (int i = 0; i < count; i++)
            {
                if (!_tagsByAddress.TryGetValue((ushort)(start + i), out var tagsAtAddress))
                {
                    continue;
                }

                foreach (var tag in tagsAtAddress)
                {
                    samples.Add(ToSample(tag, (short)values[i], timestamp));
                }
            }
        }
        catch (SlaveException)
        {
            for (int i = 0; i < count; i++)
            {
                if (!_tagsByAddress.TryGetValue((ushort)(start + i), out var tagsAtAddress))
                {
                    continue;
                }

                foreach (var tag in tagsAtAddress)
                {
                    try
                    {
                        short raw = _modbus.ReadRegister(tag.Address);
                        samples.Add(ToSample(tag, raw, DateTime.Now));
                    }
                    catch (SlaveException ex)
                    {
                        failures.Add(new TagFailure(tag, DescribeSlaveException(ex)));
                    }
                }
            }
        }
    }

    /// <summary>原始值 → 工程值：项目唯一的业务公式，独立出来便于单测</summary>
    internal static Sample ToSample(PlcTag tag, short raw, DateTime timestamp)
        => new(tag, raw, raw * tag.Scale + tag.Offset, timestamp);

    /// <summary>把 NModbus 那一大段英文异常压成一句话，界面上才看得清</summary>
    private static string DescribeSlaveException(SlaveException ex)
    {
        string reason = ex.SlaveExceptionCode switch
        {
            SlaveExceptionCodes.IllegalFunction => "从站不支持该功能码",
            SlaveExceptionCodes.IllegalDataAddress => "地址不存在（超出从站范围）",
            SlaveExceptionCodes.IllegalDataValue => "数据值非法",
            SlaveExceptionCodes.SlaveDeviceFailure => "从站设备故障",
            SlaveExceptionCodes.Acknowledge => "从站已受理但需等待",
            SlaveExceptionCodes.SlaveDeviceBusy => "从站忙",
            _ => "从站返回异常"
        };

        return $"Modbus 异常码 {ex.SlaveExceptionCode}：{reason}";
    }

    public void Dispose()
    {
        _cts?.Cancel();
    }
}
