using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace PlcHmi.Models;

/// <summary>
/// 机型：4 字母代号 + 中文型号名。代号就是配置文件名（data/configs/AIXP.json），
/// 界面上下拉框显示 <see cref="Display"/>。
/// </summary>
public sealed record MachineModel(string Code, string Name)
{
    /// <summary>下拉框与状态栏里显示的样子：「AIXP · 分离封装机」</summary>
    public string Display => $"{Code} · {Name}";
}

/// <summary>
/// 机台号 → 机型的唯一判定依据：机台号前 4 位就是机型代号（AIXP999 → AIXP）。
/// 这张表写在代码里而不是配置文件里 —— 万一配置缺失，也绝不能把机台号解析成"没有机型"。
/// </summary>
public static class MachineModels
{
    /// <summary>机台号 = 机型代号（4 字母）+ 台号（3 位数字）</summary>
    private static readonly Regex MachineNoPattern = new(@"^([A-Za-z]{4})(\d{3})$", RegexOptions.Compiled);

    /// <summary>机台号的格式要求（提示语统一用它，免得各处写得不一致）</summary>
    public const string FormatHint = "4 个字母 + 3 位数字，例如 AIXP999";

    /// <summary>已知机型表；第一个兼作"什么线索都没有"时的兜底机型</summary>
    public static IReadOnlyList<MachineModel> All { get; } =
    [
        new("AIXP", "分离封装机"),
        new("AITN", "辊压机"),
        new("AIBM", "成型机"),
        new("AIIR", "提纯机"),
        new("AIPC", "大片机")
    ];

    /// <summary>
    /// 机台号规范化：去掉两端空格、字母部分补成大写（aixp001 → AIXP001）。
    /// 格式不对时也照样去空格转大写返回，交给界面去提示"认不出机型"。
    /// </summary>
    public static string Normalize(string? machineNo)
    {
        string trimmed = machineNo?.Trim() ?? string.Empty;

        var match = MachineNoPattern.Match(trimmed);
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() + match.Groups[2].Value : trimmed.ToUpperInvariant();
    }

    /// <summary>机型代号 → 机型；不在表里返回 null（现场允许手工选到别的档案）</summary>
    public static MachineModel? Find(string? code) =>
        string.IsNullOrWhiteSpace(code)
            ? null
            : All.FirstOrDefault(m => string.Equals(m.Code, code.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 机台号推机型：格式必须是 4 字母 + 3 数字，且前 4 位要在已知机型表里。
    /// 大小写不敏感（现场偶尔敲小写），返回的代号一律大写。
    /// </summary>
    public static bool TryResolve(string? machineNo, [NotNullWhen(true)] out MachineModel? model)
    {
        model = null;

        var match = MachineNoPattern.Match(machineNo?.Trim() ?? string.Empty);
        if (!match.Success)
        {
            return false;
        }

        model = Find(match.Groups[1].Value);
        return model is not null;
    }
}
