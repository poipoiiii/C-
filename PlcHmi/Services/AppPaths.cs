using System.IO;

namespace PlcHmi.Services;

/// <summary>
/// 运行期目录布局：所有产物收在**一个根目录** <c>data/</c> 下，再按用途分子目录，
/// 安装目录里不会到处散落文件，各子目录也各有保留策略（不会无限膨胀）。
///
/// 根目录 = 向上找到 .env / .git 的那一层（开发时就是仓库根），找不到则用 exe 目录（现场部署）；
/// 根目录不可写时（例如装在 Program Files）整体回退到 %ProgramData%\PlcHmi。
///
/// <code>
/// 根目录/
/// ├── .env                  数据库连接（现场每台机器一份，不入版本库）
/// └── data/                 运行期数据（只有这一个运行根目录）
///     ├── configs/          现场配置副本：首次从机型模板复制，之后改的是这一份 &lt;机型&gt;.json
///     ├── machine.json      本机档案：机台号 + 连接参数 + 当前机型
///     ├── logs/             运行日志：按天一个文件，只留 LogRetentionDays 天
///     ├── exports/          历史数据导出：用户自己清理
///     └── updates/          远程更新包：应用成功后只留 UpdateKeepCount 份以便回滚
/// </code>
/// </summary>
public static class AppPaths
{
    /// <summary>日志保留天数：按最后写入时间清理，避免 logs 无限膨胀</summary>
    public const int LogRetentionDays = 30;

    /// <summary>更新包保留份数：应用成功后只留最近几份，避免 updates 无限膨胀</summary>
    public const int UpdateKeepCount = 2;

    /// <summary>仓库根 / 安装根</summary>
    public static string Root { get; } = ResolveRoot();

    /// <summary>运行期根目录：配置 / 日志 / 导出 / 更新包都在它下面</summary>
    public static string Data { get; } = EnsureDirectory(Path.Combine(Root, "data"));

    /// <summary>现场配置副本目录：data/configs/&lt;机型&gt;.json（首次从模板复制，之后现场改这份）</summary>
    public static string Configs { get; } = EnsureDirectory(Path.Combine(Data, "configs"));

    /// <summary>本机档案：机台号 + 连接参数 + 当前机型（每台机器一份，运行期生成，不入版本库）</summary>
    public static string MachineFile => Path.Combine(Data, "machine.json");

    /// <summary>
    /// 机型模板目录：随程序发布（csproj 里作为 Content 复制到输出目录），只读。
    /// 首次运行、或换到一台没用过的机型时，从这里复制一份到 <see cref="Configs"/> 当现场副本。
    /// </summary>
    public static string Templates { get; } = Path.Combine(AppContext.BaseDirectory, "Templates");

    /// <summary>日志目录</summary>
    public static string Logs => EnsureDirectory(Path.Combine(Data, "logs"));

    /// <summary>导出目录（历史数据 CSV / Excel）</summary>
    public static string Exports => EnsureDirectory(Path.Combine(Data, "exports"));

    /// <summary>远程更新目录：下载的包放这里，应用成功后按 UpdateKeepCount 清理</summary>
    public static string Updates => EnsureDirectory(Path.Combine(Data, "updates"));

    /// <summary>向上找 .env / .git 定位仓库根；找不到就用 exe 目录</summary>
    private static string ResolveRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (int depth = 0; depth < 8 && directory is not null; depth++)
        {
            if (File.Exists(Path.Combine(directory.FullName, ".env")) ||
                Directory.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return IsWritable(directory.FullName) ? directory.FullName : ProgramDataRoot();
            }

            directory = directory.Parent;
        }

        return IsWritable(AppContext.BaseDirectory) ? AppContext.BaseDirectory : ProgramDataRoot();
    }

    private static string ProgramDataRoot() => EnsureDirectory(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PlcHmi"));

    /// <summary>写个探针文件试试；装在 Program Files 这类不可写的位置时返回 false</summary>
    private static bool IsWritable(string directory)
    {
        try
        {
            string probe = Path.Combine(directory, $".write-probe-{Guid.NewGuid():N}");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
