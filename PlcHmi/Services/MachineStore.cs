using System.IO;
using System.Text.Json;
using PlcHmi.Models;

namespace PlcHmi.Services;

/// <summary>
/// 本机档案读写：data/machine.json，装"这台设备"的东西（机台号 / 连接参数 / 当前机型）。
/// 只有一份，所以做成静态方法；读坏了不阻断启动 —— 退回默认值，界面上随时能改回来。
/// </summary>
public static class MachineStore
{
    public static MachineProfile Load()
    {
        try
        {
            if (!File.Exists(AppPaths.MachineFile))
            {
                return new MachineProfile();
            }

            return JsonSerializer.Deserialize<MachineProfile>(
                File.ReadAllText(AppPaths.MachineFile), ConfigStore.JsonOptions) ?? new MachineProfile();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new MachineProfile();
        }
    }

    public static void Save(MachineProfile machine) =>
        File.WriteAllText(AppPaths.MachineFile, JsonSerializer.Serialize(machine, ConfigStore.JsonOptions));
}
