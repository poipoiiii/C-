namespace HmiCore.Models;

/// <summary>某个点位本轮读失败：点位 + 简短原因（如 Modbus 异常码 2 = 地址不存在）</summary>
public sealed record TagFailure(PlcTag Tag, string Reason);

/// <summary>
/// 一轮采集的结果：读成功的样本 + 读失败的点位。
/// 分开发给界面，是为了让坏点位只标灰自己，不拖垮整轮（切从站后点位表不匹配时的常见情况）。
/// </summary>
public sealed record AcquisitionBatch(IReadOnlyList<Sample> Samples, IReadOnlyList<TagFailure> Failures)
{
    public DateTime Timestamp => Samples.Count > 0 ? Samples[0].Timestamp : DateTime.Now;
}
