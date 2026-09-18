namespace PlcCore.Models;

/// <summary>
/// 本机档案（data/machine.json）：只装"这台设备"的东西 —— 机台号、连接与采集参数、当前机型。
///
/// 之所以从机型配置里搬出来：机型配置要做到**同型号完全一致、可以整份复制给另一台机器**，
/// 那就不能掺机台号和 IP 这类每台机器都不一样的东西。
/// </summary>
public sealed class MachineProfile
{
    /// <summary>机台号，形如 AIXP999：前 4 位决定机型，后 3 位是台号</summary>
    public string MachineNo { get; set; } = "";

    /// <summary>
    /// 当前机型代号（= data/configs 下的文件名）。
    /// 手工选过机型就记在这里，下次开机还用；机台号一改则按机台号重推。
    /// </summary>
    public string Profile { get; set; } = "";

    public string Ip { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 5020;

    public byte SlaveId { get; set; } = 1;

    /// <summary>采集周期（毫秒）</summary>
    public int IntervalMs { get; set; } = 1000;

    /// <summary>界面主题："Light" / "Dark"，跟着设备记着（控制室夜班常开暗色）</summary>
    public string Theme { get; set; } = "Light";
}
