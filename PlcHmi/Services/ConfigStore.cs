using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using PlcHmi.Models;

namespace PlcHmi.Services;

/// <summary>
/// 界面配置（采集点位表 + 存储映射）的读写：plc-config.json 放在仓库根目录，
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
                return new PlcConfig { Tags = [.. PlcTag.Default] };
            }

            var json = File.ReadAllText(FilePath);
            var config = JsonSerializer.Deserialize<PlcConfig>(json, Options);
            if (config is null || config.Tags.Count == 0)
            {
                return new PlcConfig { Tags = [.. PlcTag.Default] };
            }

            return config;
        }
        catch (Exception)
        {
            // 配置损坏时退回默认点位，避免界面起不来
            return new PlcConfig { Tags = [.. PlcTag.Default] };
        }
    }

    public static void Save(PlcConfig config)
    {
        File.WriteAllText(FilePath, JsonSerializer.Serialize(config, Options));
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
