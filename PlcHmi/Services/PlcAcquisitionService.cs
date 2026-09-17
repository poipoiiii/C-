using NModbus;
using PlcDataCollector.Services;
using PlcHmi.Models;

namespace PlcHmi.Services;

/// <summary>
/// 采集循环：按周期读 Modbus → 换算 →（可选）落库 → 通知界面。
/// 两类异常分开处理：
/// - 单个地址读失败（Modbus 异常码，比如地址超出从站范围）→ 只把该点位标记失败，其余点位照常采集落库；
/// - 传输层异常（断网、连接被断开）→ 整轮作废并上报，因为这时候重试也没意义。
/// </summary>
public sealed class PlcAcquisitionService : IDisposable     /// 明确禁止继承
{
    private readonly ModbusService _modbus;
    private readonly IReadOnlyList<PlcTag> _tags;       // _代表只能类内部访问
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public PlcAcquisitionService(ModbusService modbus, IReadOnlyList<PlcTag> tags)
    {
        _modbus = modbus;
        _tags = tags;
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
    /// 逐点读取。地址类错误（Modbus 异常码）只记为该点位失败，继续读下一个；
    /// 传输层错误直接抛出，由外层按整轮故障处理。
    /// </summary>
    private AcquisitionBatch ReadAll()
    {
        var samples = new List<Sample>(_tags.Count);
        var failures = new List<TagFailure>();

        foreach (var tag in _tags)
        {
            try
            {
                short raw = _modbus.ReadRegister(tag.Address);
                samples.Add(new Sample(tag, raw, raw * tag.Scale + tag.Offset, DateTime.Now));
            }
            catch (SlaveException ex)
            {
                failures.Add(new TagFailure(tag, DescribeSlaveException(ex)));
            }
        }

        return new AcquisitionBatch(samples, failures);
    }

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
