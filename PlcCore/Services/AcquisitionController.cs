using PlcCore.Models;

namespace PlcCore.Services;

/// <summary>
/// 采集控制器：采集循环的生命周期（开始 / 停止）、运行期参数调整与落库。
/// 只管"按区段采一轮 + 拆组写库"，不碰界面：一批采完 / 传输层失败 / 落库失败都用事件报出去，
/// 线程切回界面由视图模型自己处理（控制器里不允许出现任何 WPF 类型）。
///
/// 触发监听（<see cref="TriggerWatchService"/>）不在这个控制器里：它有自己的生命周期，
/// 由视图模型跟着采集一起启停。
/// </summary>
public sealed class AcquisitionController : IDisposable
{
    private readonly AppServices _services;

    /// <summary>取当前**生效**配置：切换机型时整份换掉，所以每次现取，不缓存实例</summary>
    private readonly Func<PlcConfig> _config;

    private PlcAcquisitionService? _acquisition;

    public AcquisitionController(AppServices services, Func<PlcConfig> config)
    {
        _services = services;
        _config = config;
    }

    /// <summary>采集周期（毫秒）</summary>
    public int IntervalMs { get; set; } = 1000;

    /// <summary>是否把采集结果写入数据库</summary>
    public bool PersistEnabled { get; set; } = true;

    /// <summary>正在采集</summary>
    public bool IsAcquiring => _acquisition is not null;

    /// <summary>一轮采集结束（含成功样本与失败点位），在后台线程触发</summary>
    public event Action<AcquisitionBatch>? BatchCompleted;

    /// <summary>传输层故障（断网/连接断开），整轮作废，在后台线程触发</summary>
    public event Action<string>? Failed;

    /// <summary>落库失败回调（在后台线程触发），不影响实时显示</summary>
    public event Action<string>? PersistFailed;

    /// <summary>
    /// 开始采集：按当前配置快照点位与读取区段，新建一个采集循环。
    /// 已经在采 / 点位表为空时不启动并返回 false（调用方据此把界面开关拨回去）。
    /// </summary>
    public bool Start(IModbusClient modbus)
    {
        if (_acquisition is not null)
        {
            return false;
        }

        var config = _config();
        if (config.Tags.Count == 0)
        {
            return false;
        }

        var service = new PlcAcquisitionService(modbus, [.. config.Tags], BuildReadRanges(config))
        {
            IntervalMs = IntervalMs,
            PersistEnabled = PersistEnabled,
            PersistAsync = PersistBatchAsync
        };
        service.BatchCompleted += OnBatchCompleted;
        service.Failed += OnFailed;
        service.PersistFailed += OnPersistFailed;
        _acquisition = service;
        service.Start();
        return true;
    }

    /// <summary>停止采集并等循环退出；原来没在采集时返回 false</summary>
    public async Task<bool> StopAsync()
    {
        var service = _acquisition;
        if (service is null)
        {
            return false;
        }

        _acquisition = null;
        service.BatchCompleted -= OnBatchCompleted;
        service.Failed -= OnFailed;
        service.PersistFailed -= OnPersistFailed;
        await service.StopAsync();
        return true;
    }

    /// <summary>运行中改采集周期；没在采集时只记住值，返回是否正在采集</summary>
    public bool ApplyIntervalMs(int value)
    {
        IntervalMs = value;

        if (_acquisition is null)
        {
            return false;
        }

        _acquisition.IntervalMs = value;
        return true;
    }

    /// <summary>运行中改「是否落库」；没在采集时只记住值，返回是否正在采集</summary>
    public bool ApplyPersistEnabled(bool value)
    {
        PersistEnabled = value;

        if (_acquisition is null)
        {
            return false;
        }

        _acquisition.PersistEnabled = value;
        return true;
    }

    private void OnBatchCompleted(AcquisitionBatch batch) => BatchCompleted?.Invoke(batch);

    private void OnFailed(string message) => Failed?.Invoke(message);

    private void OnPersistFailed(string message) => PersistFailed?.Invoke(message);

    /// <summary>交给采集循环的读取区段：只取真有点位的组，空区段读回来也没地方写</summary>
    private static IReadOnlyList<ReadRange> BuildReadRanges(PlcConfig config) =>
        [.. config.Groups
            .Where(g => g.Count > 0 && config.Tags.Any(t => g.Covers(t.Address)))
            .OrderBy(g => g.StartAddress)
            .Select(g => new ReadRange(g.StartAddress, g.Count))];

    /// <summary>
    /// 按存储组把一批数据拆开，各组并发写自己的目标表。
    /// 归组看地址落在哪个区段（用生效配置，不看界面上那份草稿）；
    /// 并发写是为了让慢的那张表不拖住其它组。
    /// </summary>
    private async Task PersistBatchAsync(IReadOnlyList<Sample> batch)
    {
        var config = _config();
        var jobs = new List<Task>();

        foreach (var group in config.Groups)
        {
            if (group.Count == 0)
            {
                continue;
            }

            var samples = batch.Where(s => group.Covers(s.Tag.Address)).ToList();
            if (samples.Count == 0)
            {
                continue;
            }

            jobs.Add(group.Mode == StorageMode.Wide
                ? _services.TableStore.InsertWideAsync(group.TableName, samples, group.Shape)
                : _services.TableStore.InsertLongAsync(group.TableName, samples, group.Shape));
        }

        if (jobs.Count > 0)
        {
            await Task.WhenAll(jobs);
        }

        // 正常情况下采集只会读区段内的点位，走不到这里；真出现说明区段在采集中被改过，提醒一句
        int orphan = batch.Count(s => !config.Groups.Any(g => g.Covers(s.Tag.Address)));
        if (orphan > 0)
        {
            throw new InvalidOperationException(
                $"有 {orphan} 个采样点不在任何存储组的区段内，没地方落库。去「存储映射」页调整区段范围。");
        }
    }

    public void Dispose() => _acquisition?.Dispose();
}
