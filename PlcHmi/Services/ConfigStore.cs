using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using PlcHmi.Models;

namespace PlcHmi.Services;

/// <summary>
/// 机型配置的读写：一个机型一份 data/configs/&lt;机型&gt;.json，是**现场副本** ——
/// 首次运行由 <see cref="EnsureConfigFile"/> 从随包发布的机型模板（Templates/&lt;机型&gt;.json）复制而来，
/// 之后现场改的就是这一份；模板保持干净，同型号的配置才能完全一致、整份复制给另一台机器。
/// 一个实例对应一个机型；换机型就换实例（见 MainViewModel）。
/// </summary>
public sealed class ConfigStore
{
    /// <summary>机型名要当文件名用：先卡死字符集，顺带挡掉路径穿越</summary>
    private static readonly Regex ProfileNamePattern =
        new(@"^[\u4e00-\u9fa5A-Za-z0-9_-]{1,32}$", RegexOptions.Compiled);

    /// <summary>配置文件的 JSON 写法：缩进 + 中文不转义（配置文件要给人看）；本机档案也共用它</summary>
    internal static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private ConfigStore(string profile)
    {
        Profile = profile;
        FilePath = PathFor(profile);
    }

    /// <summary>机型代号 → data/configs 下的配置文件路径</summary>
    public static string PathFor(string profile) => Path.Combine(AppPaths.Configs, profile + ".json");

    /// <summary>机型名（= data/configs 下的文件名去掉 .json）</summary>
    public string Profile { get; }

    /// <summary>本机型的配置文件全路径</summary>
    public string FilePath { get; }

    /// <summary>最近一次 Load 的告警（配置损坏 / 版本高于程序），没问题时为 null</summary>
    public string? LoadWarning { get; private set; }

    /// <summary>打开指定机型的配置；机型名不合法直接抛，避免拼出目录穿越的路径</summary>
    public static ConfigStore Open(string profile)
    {
        string name = profile?.Trim() ?? string.Empty;
        if (!IsValidProfileName(name))
        {
            throw new ArgumentException(
                $"机型名不合法：{profile}（只能用中英文、数字、下划线、短横线，最长 32 个字符）", nameof(profile));
        }

        return new ConfigStore(name);
    }

    public static bool IsValidProfileName(string? name) => name is not null && ProfileNamePattern.IsMatch(name);

    /// <summary>data/configs 下已有的机型，按名字排序</summary>
    public static IReadOnlyList<string> ListProfiles() =>
    [
        .. Directory.EnumerateFiles(AppPaths.Configs, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .Where(IsValidProfileName)
            .OrderBy(name => name, StringComparer.CurrentCulture)
    ];

    /// <summary>
    /// 保证 data/configs/&lt;机型&gt;.json 存在：没有就从随包发布的机型模板复制一份当现场副本。
    /// 返回 false 表示模板也没有（安装包不完整）—— 调用方按内置默认点位建档案，并提示用户核对。
    /// </summary>
    public static bool EnsureConfigFile(string profile)
    {
        if (File.Exists(PathFor(profile)))
        {
            return true;
        }

        string template = Path.Combine(AppPaths.Templates, profile + ".json");
        if (!File.Exists(template))
        {
            return false;
        }

        File.Copy(template, PathFor(profile));
        return true;
    }

    public PlcConfig Load()
    {
        LoadWarning = null;

        if (!File.Exists(FilePath))
        {
            return CreateDefault();
        }

        PlcConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<PlcConfig>(File.ReadAllText(FilePath), JsonOptions);
        }
        catch (JsonException)
        {
            LoadWarning = $"配置 {FilePath} 解析失败，已退回默认点位（原文件未改动）";
            return CreateDefault();
        }

        if (config is null || config.Tags.Count == 0)
        {
            return CreateDefault();
        }

        if (config.ConfigVersion > PlcConfig.CurrentVersion)
        {
            LoadWarning =
                $"配置 {FilePath} 的格式版本是 {config.ConfigVersion}，高于本程序的 {PlcConfig.CurrentVersion}：" +
                "已按默认点位启动，请升级程序，或改用与程序匹配的配置";
            return CreateDefault();
        }

        return Migrate(config);
    }

    public void Save(PlcConfig config)
    {
        config.ConfigVersion = PlcConfig.CurrentVersion;
        config.MachineName = Profile;
        File.WriteAllText(FilePath, JsonSerializer.Serialize(config, JsonOptions));
    }

    private PlcConfig CreateDefault() => new()
    {
        ConfigVersion = PlcConfig.CurrentVersion,
        MachineName = Profile,
        Tags = [.. PlcTag.Default],
        Groups = [new StorageGroup(StorageGroup.DefaultName, "DataRecords", StorageMode.Wide, 0, 20)]
    };

    /// <summary>
    /// 把老配置迁移成新区段模型并回写：
    /// 1) 老配置只有一个 Storage 段（单表 + 单模式）→ 建成一个「默认组」；
    /// 2) 只有一个组、但区段盖不住点位的（老配置没有区段信息，反序列化拿到的只是默认值）
    ///    → 按所有点位的地址范围把它扩开。
    /// </summary>
    private PlcConfig Migrate(PlcConfig config)
    {
        bool changed = config.ConfigVersion != PlcConfig.CurrentVersion || config.MachineName != Profile;
        config.ConfigVersion = PlcConfig.CurrentVersion;
        config.MachineName = Profile;

        if (config.Groups.Count == 0)
        {
            var legacy = config.Storage ?? new StorageConfig();
            var tags = config.Tags;
            int start = tags.Count > 0 ? tags.Min(t => t.Address) : 0;
            int count = tags.Count > 0 ? tags.Max(t => t.Address) - start + 1 : 20;
            config.Groups.Add(new StorageGroup(
                StorageGroup.DefaultName, legacy.TableName, legacy.Mode, (ushort)start, (ushort)count));
            changed = true;
        }

        if (config.Storage is not null)
        {
            // 迁移完成，这个字段不再写回文件
            config.Storage = null;
            changed = true;
        }

        // 单组配置：区段以点位地址为准扩开，否则这些点位会全变成「未归组」
        if (config.Groups.Count == 1 && config.Tags.Count > 0)
        {
            var group = config.Groups[0];
            int min = config.Tags.Min(t => t.Address);
            int max = config.Tags.Max(t => t.Address);
            if (min < group.StartAddress || max > group.EndAddress)
            {
                group.StartAddress = (ushort)min;
                group.Count = (ushort)(max - min + 1);
                changed = true;
            }
        }

        if (changed)
        {
            Save(config);
        }

        return config;
    }
}
