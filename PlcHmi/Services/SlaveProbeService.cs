using System.Net.Sockets;
using NModbus;

namespace PlcHmi.Services;

/// <summary>单个从站号的探测结果</summary>
public sealed record SlaveProbeResult(byte UnitId, bool Responded, string Detail);

/// <summary>
/// 从站号探测：一个网关（同一个 IP:端口）后面可能挂多台 PLC，靠从站号区分。
/// 做法是「短超时 + 分批并行」，每个从站号开一条独立连接试读一次：
/// 读到数据、或回了 Modbus 异常码（说明从站活着，只是地址不合法）→ 判定为存在；
/// 连不上/超时/连接被断开 → 判定为不存在。
/// 注意：个别网关对任何从站号都响应，这种探测结果只能作为参考，最终以现场点表为准。
/// </summary>
public sealed class SlaveProbeService
{
    /// <summary>每批并行探测多少个从站号（并发太多真机会拒连）</summary>
    private const int BatchSize = 8;

    public async Task<IReadOnlyList<SlaveProbeResult>> ProbeAsync(
        string ip,
        int port,
        byte startId,
        byte endId,
        int timeoutMs,
        IProgress<string>? progress,
        CancellationToken token)
    {
        var results = new List<SlaveProbeResult>();

        for (int first = startId; first <= endId; first += BatchSize)
        {
            token.ThrowIfCancellationRequested();
            int last = Math.Min(first + BatchSize - 1, endId);
            progress?.Report($"正在探测从站 {first}~{last} …");

            var tasks = Enumerable.Range(first, last - first + 1)
                .Select(id => Task.Run(() => ProbeOne(ip, port, (byte)id, timeoutMs), token));

            results.AddRange(await Task.WhenAll(tasks));
        }

        return results;
    }

    private static SlaveProbeResult ProbeOne(string ip, int port, byte unitId, int timeoutMs)
    {
        try
        {
            using var client = new TcpClient();

            try
            {
                var connect = client.ConnectAsync(ip, port);
                if (!connect.Wait(timeoutMs))
                {
                    // 超时返回后这条连接还在建，紧接着 client 被 Dispose 会把它掐断（WSA_OPERATION_ABORTED）。
                    // 必须把它的异常观察掉：否则会以"未观察的任务异常"冒到全局日志，
                    // 探测一轮就刷一批 ERR，把真正要排查的东西埋掉。
                    ObserveQuietly(connect);
                    return new SlaveProbeResult(unitId, false, "连接超时");
                }
            }
            catch (AggregateException ex)
            {
                var reason = ex.InnerException?.Message ?? ex.Message;
                return new SlaveProbeResult(unitId, false, $"连接失败：{reason}");
            }

            var master = new ModbusFactory().CreateMaster(client);
            master.Transport.ReadTimeout = timeoutMs;
            master.Transport.WriteTimeout = timeoutMs;
            master.Transport.Retries = 0;   // 探测不需要重试，否则一个号要等好几个超时

            try
            {
                master.ReadHoldingRegisters(unitId, 0, 1);
                return new SlaveProbeResult(unitId, true, "有响应（读到地址 0）");
            }
            catch (SlaveException ex)
            {
                // 回了异常码说明从站确实在，只是这个地址没定义
                return new SlaveProbeResult(unitId, true, $"有响应（Modbus 异常码 {ex.SlaveExceptionCode}）");
            }
            catch (Exception ex)
            {
                return new SlaveProbeResult(unitId, false, $"无响应：{ex.Message}");
            }
        }
        catch (Exception ex)
        {
            return new SlaveProbeResult(unitId, false, $"无响应：{ex.Message}");
        }
    }

    /// <summary>把没人 await 的任务的异常吃掉（读一下 Exception 就算观察过），只为不污染日志</summary>
    private static void ObserveQuietly(Task task) =>
        _ = task.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}
