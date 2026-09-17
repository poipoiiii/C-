using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using PlcHmi.Models;

namespace PlcHmi.Services;

/// <summary>
/// 界面配置（采集点位表 + 存储组）的读写：plc-config.json 放在仓库根目录，
/// 与 .env 同级，便于跟着代码一起查看和版本管理。
/// </summary>
public static class ConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        // 中文直接写出来，别转成 \uXXXX，配置文件要给人看
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string FilePath { get; } = Path.Combine(ResolveRootDirectory(), "plc-config.json");

    public static PlcConfig Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return CreateDefault();
            }

            var json = File.ReadAllText(FilePath);
            var config = JsonSerializer.Deserialize<PlcConfig>(json, Options);
            if (config is null || config.Tags.Count == 0)
            {
                return CreateDefault();
            }

            return Migrate(config);
        }
        catch (Exception)
        {
            // 配置损坏时退回默认点位，避免界面起不来
            return CreateDefault();
        }
    }

    public static void Save(PlcConfig config)
    {
        File.WriteAllText(FilePath, JsonSerializer.Serialize(config, Options));
    }

    private static PlcConfig CreateDefault() => new()
    {
        Tags = [.. PlcTag.Default],
        Groups = [new StorageGroup(StorageGroup.DefaultName, "DataRecords", StorageMode.Long, 0, 20)]
    };

    /// <summary>
    /// 把老配置迁移成新区段模型并回写：
    /// 1) 老配置只有一个 Storage 段（单表 + 单模式）→ 建成一个「默认组」；
    /// 2) 只有一个组、但区段盖不住点位的（老配置没有区段信息，反序列化拿到的只是默认值）
    ///    → 按所有点位的地址范围把它扩开。
    /// </summary>
    private static PlcConfig Migrate(PlcConfig config)
    {
        bool changed = false;

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

    /// <summary>从运行目录向上找仓库根（认 .env 或 .git），找不到就用运行目录</summary>
    private static string ResolveRootDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (int depth = 0; depth < 8 && directory is not null; depth++)
        {
            if (File.Exists(Path.Combine(directory.FullName, ".env")) ||
                Directory.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return AppContext.BaseDirectory;
    }
}
