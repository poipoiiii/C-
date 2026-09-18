using HmiCore.Models;

namespace HmiCore.Config;

/// <summary>
/// 启动时决定用哪个机型。顺序（前面的赢）：
/// <list type="number">
/// <item>命令行 <c>--config &lt;机型&gt;</c>：调试/临时指定，压过一切</item>
/// <item>命令行 <c>--machine &lt;机台号&gt;</c>：按机台号前 4 位推</item>
/// <item>本机档案里记着**已知机型**：上次用的 / 现场手工选过的</item>
/// <item>本机档案里的机台号推出来的机型 —— 机台号是机型的权威依据，
///       压过"档案里那个不是已知机型的名字"（例如历史遗留的「默认」）</item>
/// <item>档案里记的自定义档案名（不是已知机型，但确实有一份配置）</item>
/// <item>data/configs 里已有的第一个档案，最后是已知机型的第一个（AIXP）</item>
/// </list>
/// 机台号推不出机型（格式不对 / 代号不在表里）不算错误：现场允许临时接一台没登记的机器，
/// 由用户在机型下拉里手工选。
/// </summary>
public static class ProfileResolver
{
    public static string Resolve(string[] args, MachineProfile machine)
    {
        string? asked = ReadArgument(args, "--config", "-c");
        if (ConfigStore.IsValidProfileName(asked))
        {
            return asked!.Trim();
        }

        if (MachineModels.TryResolve(ReadArgument(args, "--machine"), out var fromArgument))
        {
            return fromArgument.Code;
        }

        if (MachineModels.Find(machine.Profile) is { } remembered)
        {
            return remembered.Code;
        }

        if (MachineModels.TryResolve(machine.MachineNo, out var fromMachineNo))
        {
            return fromMachineNo.Code;
        }

        if (ConfigStore.IsValidProfileName(machine.Profile))
        {
            return machine.Profile.Trim();
        }

        return ConfigStore.ListProfiles().FirstOrDefault() ?? MachineModels.All[0].Code;
    }

    /// <summary>取 <c>--名字 值</c> / <c>--名字=值</c> / <c>短名 值</c> 形式的参数</summary>
    private static string? ReadArgument(string[] args, params string[] names)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];

            foreach (string name in names)
            {
                if (arg.Equals(name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    return args[i + 1].Trim();
                }

                if (arg.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                {
                    return arg[(name.Length + 1)..].Trim();
                }
            }
        }

        return null;
    }
}
