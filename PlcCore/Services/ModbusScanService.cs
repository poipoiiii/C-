using NModbus;
using PlcCore.Services;
using PlcCore.Models;

namespace PlcCore.Services;

/// <summary>
/// 寄存器扫描：Modbus 没有“列出所有点位”的指令，只能试读。
/// 策略是分段批量读，成功的段整段标记可读；失败的段再逐点重读，定位出到底是哪几个地址不可读。
/// </summary>
public sealed class ModbusScanService
{
    /// <summary>每段读完后的间隔，避免把真实 PLC 打满</summary>
    private const int SegmentDelayMs = 20;

    /// <summary>逐点重试的间隔</summary>
    private const int PointDelayMs = 5;

    public Task<IReadOnlyList<ScanResult>> ScanAsync(
        IModbusClient modbus,
        ushort startAddress,
        ushort endAddress,
        ushort batchSize,
        IProgress<string>? progress,
        CancellationToken token)
    {
        return Task.Run<IReadOnlyList<ScanResult>>(() =>
        {
            var results = new List<ScanResult>();
            int batch = Math.Max((int)batchSize, 1);

            for (int segmentStart = startAddress; segmentStart <= endAddress; segmentStart += batch)
            {
                token.ThrowIfCancellationRequested();

                int count = Math.Min(batch, endAddress - segmentStart + 1);
                try
                {
                    var values = modbus.ReadRegisters((ushort)segmentStart, (ushort)count);
                    for (int i = 0; i < count; i++)
                    {
                        results.Add(new ScanResult((ushort)(segmentStart + i), (short)values[i], "可读"));
                    }

                    progress?.Report($"已扫描 {(ushort)(segmentStart + count - 1)}，本段 {count} 个地址可读");
                }
                catch (Exception)
                {
                    // 段内有坏地址会导致整段失败，逐点重读才能分清好坏
                    for (int i = 0; i < count; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        var address = (ushort)(segmentStart + i);
                        try
                        {
                            var value = modbus.ReadRegisters(address, 1);
                            results.Add(new ScanResult(address, (short)value[0], "可读"));
                        }
                        catch (Exception ex)
                        {
                            results.Add(new ScanResult(address, null, Describe(ex)));
                        }

                        Thread.Sleep(PointDelayMs);
                    }

                    progress?.Report($"已扫描 {(ushort)(segmentStart + count - 1)}，本段有不可读地址");
                }

                Thread.Sleep(SegmentDelayMs);
            }

            return results;
        }, token);
    }

    private static string Describe(Exception ex) => ex switch
    {
        SlaveException slave => $"读异常：Modbus 异常码 {slave.SlaveExceptionCode}",
        _ => $"读异常：{ex.Message}"
    };
}
