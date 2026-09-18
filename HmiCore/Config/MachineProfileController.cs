using System.IO;
using HmiCore.Models;

namespace HmiCore.Config;

/// <summary>
/// 机型 / 机台号控制器：机型配置的装载与切换、本机档案（data/machine.json）的读写，
/// 以及"老配置里的机台号与连接参数搬进本机档案"的一次性迁移。
/// 只管文件与配置，不碰界面：状态用返回值 / 事件报出去，中文文案、日志与线程切换留在视图模型
/// （控制器里不允许出现任何 WPF 类型）。
///
/// 机台号与连接参数属于本机档案，不跟着机型走；换机型只换 <see cref="Config"/> / <see cref="Store"/>。
/// 真正的切机型动作（先断开连接再整份换配置、重建四个页签）由视图模型编排，见
/// <see cref="SwitchTo"/>。
/// </summary>
public sealed class MachineProfileController
{
    /// <summary>本机档案（data/machine.json）：机台号 + 连接参数 + 当前机型</summary>
    public MachineProfile Machine { get; }

    /// <summary>当前机型的配置；切换机型时整份换掉</summary>
    public PlcConfig Config { get; private set; }

    /// <summary>当前机型的配置读写（一个实例对应一个机型）</summary>
    public ConfigStore Store { get; private set; }

    /// <summary>当前机型代号（= data/configs 下的文件名）</summary>
    public string Profile => Store.Profile;

    /// <summary>机台号：现场这台设备的编号（4 字母 + 3 数字，前 4 位决定机型）</summary>
    public string MachineNo => Machine.MachineNo;

    /// <summary>还没填机台号</summary>
    public bool NeedsMachineNo => string.IsNullOrWhiteSpace(MachineNo);

    /// <summary>机台号能解析出的机型（格式对且代号在已知机型表里）；认不出为 null</summary>
    public MachineModel? ResolvedModel => MachineModels.TryResolve(MachineNo, out var model) ? model : null;

    /// <summary>
    /// 机台号还没填对（空着，或不是五个机型之一）。
    /// 正常路径下界面是硬校验的，只有手工改过的 machine.json 才可能带着一个认不出的号码进来。
    /// </summary>
    public bool NeedsMachineNoFix => ResolvedModel is null;

    /// <summary>机型模板缺失（安装包不完整）：本次装载只好按内置默认点位建了档案</summary>
    public bool TemplateMissing { get; private set; }

    /// <summary>本次装载做了一次性迁移：老配置里的机台号/连接参数已经搬进本机档案，老字段也清掉了</summary>
    public bool MigratedMachineProfile { get; private set; }

    /// <summary>最近一次本机档案落盘失败；没失败过为 null（构造期的失败还没有订阅者，靠它带出去）</summary>
    public Exception? PersistError { get; private set; }

    /// <summary>本机档案落盘失败（不打断操作）：怎么提示与记日志由视图模型决定</summary>
    public event Action<Exception>? PersistFailed;

    /// <param name="args">命令行参数：<c>--config</c> / <c>--machine</c> 优先于本机档案，调试用</param>
    public MachineProfileController(string[] args)
    {
        Machine = MachineStore.Load();

        // 机型：命令行 --config / --machine > 本机档案（机台号、上次选的机型）> 已有配置 > 已知机型第一个
        string profile = ProfileResolver.Resolve(args, Machine);
        Config = LoadConfig(profile, out var store);
        Store = store;

        MigrateMachineProfile();                     // 老配置里的机台号/连接参数搬进本机档案

        // 迁移可能刚把机台号捡回来：再判一次机型（老档案叫「默认」，而机台号说明它其实是 AIXP）
        string resolved = ProfileResolver.Resolve(args, Machine);
        if (resolved != profile)
        {
            // 老档案是这台设备现场调好的真实配置：新机型的档案还没有时，直接拿它当初始内容，
            // 别用模板把点位表盖掉（老档案本身留着不动，下拉里还能选回去）
            if (!File.Exists(ConfigStore.PathFor(resolved)))
            {
                ConfigStore.CopyConfigFile(Store.Profile, resolved);
            }

            profile = resolved;
            Config = LoadConfig(profile, out store);
            Store = store;
        }

        if (Machine.Profile != profile)
        {
            Machine.Profile = profile;
            SaveMachine();
        }
    }

    /// <summary>
    /// 换机型：读入目标机型的配置并整份换上。读失败会抛出，此时当前机型保持不变（调用方继续用原机型）。
    /// </summary>
    public void SwitchTo(string profile)
    {
        var config = LoadConfig(profile, out var store);

        // 先读出来再换：读失败时上面就抛了，这里的当前机型没被动过
        Config = config;
        Store = store;
    }

    /// <summary>
    /// 记下机台号：写进本机档案并落盘，返回按机台号认出的机型（认不出机型时为 null，维持当前机型）。
    /// 认出的机型与当前机型不同时，本机档案里的「当前机型」已经改成它，调用方据此切机型。
    /// </summary>
    public MachineModel? UpdateMachineNo(string machineNo)
    {
        Machine.MachineNo = machineNo;

        if (!MachineModels.TryResolve(machineNo, out var model))
        {
            SaveMachine();
            return null;
        }

        if (model.Code != Store.Profile)
        {
            Machine.Profile = model.Code;
        }

        SaveMachine();
        return model;
    }

    /// <summary>本机档案落盘；失败只报 <see cref="PersistFailed"/> 与 <see cref="PersistError"/>，不抛出、不打断操作</summary>
    public void SaveMachine()
    {
        try
        {
            MachineStore.Save(Machine);
            PersistError = null;
        }
        catch (Exception ex)
        {
            PersistError = ex;
            PersistFailed?.Invoke(ex);
        }
    }

    /// <summary>
    /// 老配置把机台号与连接参数存在机型配置里（那时"一台机器一份配置"），现在搬到 data/machine.json：
    /// 机器级的东西跟着设备走，机型配置才能做到同型号完全一致、整份复制给另一台机器用。
    /// </summary>
    private void MigrateMachineProfile()
    {
        bool machineChanged = false;

        if (string.IsNullOrWhiteSpace(Machine.MachineNo) && !string.IsNullOrWhiteSpace(Config.LegacyMachineNo))
        {
            Machine.MachineNo = Config.LegacyMachineNo.Trim();
            machineChanged = true;
        }

        if (Config.LegacyConnection is { } legacy)
        {
            Machine.Ip = legacy.Ip;
            Machine.Port = legacy.Port;
            Machine.SlaveId = legacy.SlaveId;
            Machine.IntervalMs = legacy.IntervalMs;
            machineChanged = true;
        }

        if (Config.LegacyMachineNo is null && Config.LegacyConnection is null)
        {
            if (machineChanged)
            {
                SaveMachine();
            }

            return;
        }

        // 老字段清掉：机器级信息以后只写在 machine.json 里
        Config.LegacyMachineNo = null;
        Config.LegacyConnection = null;
        Store.Save(Config);
        SaveMachine();
        MigratedMachineProfile = true;
    }

    /// <summary>
    /// 打开一个机型的配置：现场副本不在就从随包发布的机型模板复制一份
    /// （第一次上这个型号、或者换到没见过的机型时就是这么建档案的）；
    /// 模板也缺就按内置默认点位建，并把 <see cref="TemplateMissing"/> 立起来（安装包不完整）。
    /// </summary>
    private PlcConfig LoadConfig(string profile, out ConfigStore store)
    {
        store = ConfigStore.Open(profile);
        TemplateMissing = !ConfigStore.EnsureConfigFile(profile);

        var config = store.Load();
        ReconcileDefaultColumnNames(config.Tags);
        return config;
    }

    /// <summary>
    /// 兼容修复：早期版本改「名称」不会同步「宽表列名」，配置里会留下「名称=热封数量、列名=点位22」这种不一致。
    /// 列名若仍是扫描加入时的默认占位名（点位N），就按名称纠正过来；手工改过的列名不动。
    /// </summary>
    private static void ReconcileDefaultColumnNames(IEnumerable<PlcTag> tags)
    {
        foreach (var tag in tags)
        {
            if (tag.ColumnName == $"点位{tag.Address}" && tag.Name != tag.ColumnName)
            {
                tag.ColumnName = tag.Name;
            }
        }
    }
}
