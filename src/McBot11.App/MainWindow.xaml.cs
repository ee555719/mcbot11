using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace McBot11.App;

/// <summary>主窗口交互逻辑。</summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        _vm.Logs.CollectionChanged += OnLogsChanged;
        Closing += OnWindowClosing;
    }

    private void OnLogsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_vm.Logs.Count == 0) return;
        // 延迟到布局阶段之后再滚动：CollectionChanged 同步调用 ScrollIntoView
        // 会与虚拟化 ListBox 的 ItemContainerGenerator.Verify() 冲突导致崩溃
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
        {
            try
            {
                if (_vm.Logs.Count > 0) LogList.ScrollIntoView(_vm.Logs[^1]);
            }
            catch { /* 滚动失败不影响日志显示 */ }
        }));
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        try { _vm.Dispose(); } catch { }
    }
}
