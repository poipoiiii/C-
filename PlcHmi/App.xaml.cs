using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using DotNetEnv;
using HmiCore.Config;
using PlcHmi.Services;
using Serilog;

namespace PlcHmi;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // 数据库连接配置：.env 在仓库根目录，从运行目录向上查找
        try
        {
            Env.TraversePath().Load();
        }
        catch (FileNotFoundException)
        {
            // 没有 .env 也允许启动，界面会在读写数据库时提示缺少配置
        }

        ConfigureLogging();
        HookGlobalExceptionHandlers();
        TraceDataBindingErrors();

        Log.Information("程序启动 · 日志目录 {LogDirectory} · 机型配置目录 {ConfigDirectory}",
            AppPaths.Logs, AppPaths.Configs);

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("程序退出");
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    /// <summary>
    /// 日志落 data/logs，按天一个文件，只保留 AppPaths.LogRetentionDays 天 ——
    /// 现场连续跑一年也不会把磁盘撑满。
    /// 分工：状态栏是给操作工看的现象，日志文件是给事后排查看的时间线。
    /// </summary>
    private static void ConfigureLogging()
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(AppPaths.Logs, "plc-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: AppPaths.LogRetentionDays,
                shared: true)
            .CreateLogger();
    }

    /// <summary>把 WPF 的绑定错误接进日志（绑错名字只会"没有数据"，不报错）</summary>
    private static void TraceDataBindingErrors()
    {
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(new BindingErrorTraceListener());
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
    }

    /// <summary>兜住没人处理的异常：至少留下日志，别让界面默默退出</summary>
    private void HookGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += (_, args) => Log.Error(args.Exception, "界面线程未处理异常");

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Error(args.ExceptionObject as Exception, "后台线程未处理异常（进程即将退出）");
            Log.CloseAndFlush();
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "未观察的任务异常");
            args.SetObserved();
        };
    }
}
