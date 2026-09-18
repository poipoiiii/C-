using NModbus;
using HmiCore.Comm;
using HmiCore.Models;

namespace HmiCore.Acquisition;

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

    /// <summary>要读的区段（一个存储组一段）+ 段内点位，顺序固定，构造时快照</summary>
    private readonly IReadOnlyList<(ReadRange Range, IReadOnlyList<PlcTag> Tags)> _plan;

    private CancellationTokenSource? _cts;
    private Task? _loop;

    public PlcAcquisitionService(IModbusClient modbus, IReadOnlyList<PlcTag> tags, IReadOnlyList<ReadRange> ranges)
    {
        _modbus = modbus;
        _plan = [.. ranges.Select(range => (range, InRange(range, tags)))];
    }

    /// <summary>段内点位：整个点位（可能占好几个寄存器）都要落在区段里，按起始地址排序</summary>
    private static IReadOnlyList<PlcTag> InRange(ReadRange range, IReadOnlyList<PlcTag> tags) =>
        [.. tags
            .Where(t => t.Address >= range.Start && t.EndAddress <= range.Start + range.Count - 1)
            .OrderBy(t => t.Address)];

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
    /// 段内没点位的地址读回来直接丢掉。现场就是一个区段读一次。
    /// </summary>
    private AcquisitionBatch ReadAll()
    {
        var samples = new List<Sample>();
        var failures = new List<TagFailure>();

        foreach (var (range, tags) in _plan)
        {
            ReadInChunks(range, tags, samples, failures);
        }

        return new AcquisitionBatch(samples, failures);
    }

    /// <summary>
    /// 把区段切成不超过 Modbus 单次读上限（125 个）的小块，**且块边界不落在多寄存器点位中间**：
    /// 32 位量占两个寄存器，边界切在中间会让两次读各拿一半，拼出来的值一定是错的。
    /// </summary>
    private void ReadInChunks(ReadRange range, IReadOnlyList<PlcTag> tags,
        List<Sample> samples, List<TagFailure> failures)
    {
        int rangeEnd = range.Start + range.Count - 1;

        for (int chunkStart = range.Start; chunkStart <= rangeEnd;)
        {
            int chunkEnd = Math.Min(chunkStart + MaxRegistersPerRead - 1, rangeEnd);

            // 缩到"不切开任何点位"的位置。点位互不重叠（建表前校验过），所以至多缩一次；
            // 写成循环是为了不依赖这个前提。点位最长 4 个寄存器，绝不会缩到起点之前。
            foreach (var tag in tags)
            {
                if (tag.Address <= chunkEnd && tag.EndAddress > chunkEnd)
                {
                    chunkEnd = tag.Address - 1;
                }
            }

            var chunkTags = tags.Where(t => t.Address >= chunkStart && t.EndAddress <= chunkEnd).ToList();
            ReadChunk((ushort)chunkStart, (ushort)(chunkEnd - chunkStart + 1), chunkTags, samples, failures);

            chunkStart = chunkEnd + 1;
        }
    }

    /// <summary>读一段连续寄存器，把段内的点位解码成样本；整段失败时回退到段内逐个点位读</summary>
    private void ReadChunk(ushort start, ushort count, IReadOnlyList<PlcTag> tags,
        List<Sample> samples, List<TagFailure> failures)
    {
        try
        {
            var values = _modbus.ReadRegisters(start, count);

            // 同一段共用一个时间戳：批量读本来就是在同一时刻取回的
            var timestamp = DateTime.Now;
            foreach (var tag in tags)
            {
                samples.Add(ToSample(tag, DecodeFrom(values, (int)(tag.Address - start), tag), timestamp));
            }
        }
        catch (SlaveException)
        {
            foreach (var tag in tags)
            {
                try
                {
                    samples.Add(ToSample(tag, ReadOne(tag), DateTime.Now));
                }
                catch (SlaveException ex)
                {
                    failures.Add(new TagFailure(tag, DescribeSlaveException(ex)));
                }
            }
        }
    }

    /// <summary>从整段缓冲里按点位的数据类型与字序解码（offset 是点位起始地址在缓冲里的位置）</summary>
    private static double DecodeFrom(ushort[] values, int offset, PlcTag tag) =>
        PlcValueCodec.Decode(values.AsSpan(offset, tag.RegisterCount), tag.DataType, tag.WordOrder);

    /// <summary>回退路径：单独读一个点位（16 位量走单点读，多寄存器量一次读回它占的整段）</summary>
    private double ReadOne(PlcTag tag) => tag.RegisterCount == 1
        ? _modbus.ReadRegister(tag.Address)
        : PlcValueCodec.Decode(_modbus.ReadRegisters(tag.Address, (ushort)tag.RegisterCount),
            tag.DataType, tag.WordOrder);

    /// <summary>原始值 → 工程值：项目唯一的业务公式，独立出来便于单测</summary>
    internal static Sample ToSample(PlcTag tag, double raw, DateTime timestamp)
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
