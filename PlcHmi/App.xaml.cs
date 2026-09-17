using System.IO;
using System.Windows;
using DotNetEnv;

namespace PlcHmi;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // 数据库连接配置：.env 在仓库根目录，从运行目录向上查找（与 PlcDataCollector 一致）
        try
        {
            Env.TraversePath().Load();
        }
        catch (FileNotFoundException)
        {
            // 没有 .env 也允许启动，界面会在读写数据库时提示缺少配置
        }

        base.OnStartup(e);
    }
}
