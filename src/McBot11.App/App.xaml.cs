using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using McBot11.Core;

namespace McBot11.App;

/// <summary>
/// 应用程序：全局异常兜底（未处理异常写 logs\ 并尽量不终止进程）。
/// </summary>
public partial class App : Application
{
    private static readonly FileLogger CrashLogger = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        // UI 线程未处理异常：写日志并标记已处理（界面继续运行）
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        // 非 UI 线程未处理异常：写日志（进程可能随后终止）
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        // 后台任务未观察异常：写日志
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        base.OnStartup(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        CrashLogger.Error("Crash", "UI 线程未处理异常（已拦截，程序继续运行）", e.Exception);
        e.Handled = true;
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            CrashLogger.Error("Crash", "非 UI 线程未处理异常（进程即将退出）", ex);
        else
            CrashLogger.Error("Crash", "非 UI 线程未处理异常: " + e.ExceptionObject);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        CrashLogger.Error("Crash", "后台任务未观察异常", e.Exception);
        e.SetObserved();
    }
}
