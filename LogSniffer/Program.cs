using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using LogSniffer.Models;
using LogSniffer.ViewModels;

#if DEBUG

[assembly: System.Reflection.Metadata.MetadataUpdateHandler(
    typeof(Aprillz.MewUI.HotReload.MewUiMetadataUpdateHandler))]

#endif

namespace LogSniffer;

internal class Program
{
    private readonly static MainViewModel _viewModel = new();

    // UI 控件引用
    private static MultiLineTextBox? _logTextBox;
    private static TextBlock? _statusText;
    private static Button? _attachButton;
    private static Button? _launchButton;
    private static Button? _openFileButton;
    private static CheckBox? _autoScrollCheckBox;
    private static bool _autoScroll = true;

    static void Main(string[] args)
    {
        // 注册平台后端
        PlatformBackendRegister();

        // 启动时自动刷新进程列表
        _viewModel.RefreshProcessList();

        // 创建窗口
        var window = new Window()
            .OnBuild(w =>
            {
                var mainPanel = new DockPanel().LastChildFill();

                w.Title = "LogSniffer";
                w.Resizable(800, 600, minWidth: 800, minHeight: 600);
                w.Content = mainPanel;

                var topBar = BuildTopBar().DockTop();
                var statusBar = BuildStatusBar().DockBottom();
                var logViewer = BuildLogViewer().Margin(0, 10);
                mainPanel.Children(topBar, statusBar, logViewer);

                // 监听用户手动滚动，同步 Auto-scroll 复选框状态
                HookScrollBarTracking();
            });

        // 创建程序并启动窗口
        Application
            .Create()
            .UseTheme(ThemeVariant.Dark)
            .Run(window);
    }

    /// <summary>
    /// 构建状态栏
    /// </summary>
    private static Border BuildStatusBar()
    {
        var border = new Border().Margin(5, 0);
        _statusText = new TextBlock().Text("Ready");

        border.Child = _statusText;
        return border;
    }

    /// <summary>
    /// 构建日志查看器
    /// </summary>
    private static Border BuildLogViewer()
    {
        var border = new Border();
        _logTextBox = new MultiLineTextBox()
            .IsReadOnly(true)
            .Wrap(true);

        border.Child = _logTextBox;
        return border;
    }

    /// <summary>
    /// 构建顶部工具栏
    /// </summary>
    private static StackPanel BuildTopBar()
    {
        var stackPanel = new StackPanel().Orientation(Orientation.Horizontal);
        var processesComboBox = new ComboBox().Size(250, double.NaN).Margin(5, 0);
        var refreshButton = new Button().Margin(5, 0).Content("Refresh");
        _attachButton = new Button().Margin(5, 0).Content("Attach");
        _launchButton = new Button().Margin(5, 0).Content("Launch");
        _autoScrollCheckBox = new CheckBox().Margin(5, 0)
            .IsChecked(true)
            .Content("Auto-scroll");

        _autoScrollCheckBox.OnCheckedChanged(isChecked =>
        {
            _autoScroll = isChecked;
            if (isChecked && _logTextBox is not null)
            {
                _logTextBox.CaretPosition = int.MaxValue;
                _logTextBox.ScrollToCaret();
            }
        });

        // 绑定数据源
        processesComboBox.ItemsSource(_viewModel.ProcessListView);

        // ComboBox 选中项变更
        processesComboBox.OnSelectionChanged(args =>
        {
            if (processesComboBox.SelectedItem is ProcessItem item)
            {
                _viewModel.SelectedProcess = item;
            }
        });

        // 刷新按钮
        refreshButton.OnClick(() =>
        {
            _viewModel.RefreshProcessList();
            FlushLogToTextBox();
        });

        // 打开文件按钮: 打开并监控日志文件
        _openFileButton = new Button().Margin(5, 0).Content("Open File");
        _openFileButton.OnClick(() =>
        {
            if (_viewModel.IsFileMonitoring)
            {
                _viewModel.StopFileMonitor();
                UpdateButtonStates();
                UpdateStatusText();
                FlushLogToTextBox();
                return;
            }

            var fileDialog = Application.Current.PlatformHost.FileDialog;
            var files = fileDialog.OpenFile(new OpenFileDialogOptions
            {
                Title = "Open log file",
                Filter = "Log files (*.txt;*.log)|*.txt;*.log|All files (*.*)|*.*",
            });

            if (files is not { Length: > 0 })
                return;

            _viewModel.OpenFile(files[0]);
            UpdateButtonStates();
            UpdateStatusText();
            FlushLogToTextBox();
        });

        // 清空日志按钮
        var clearButton = new Button().Margin(5, 0).Content("Clear");
        clearButton.OnClick(() =>
        {
            _viewModel.ClearLog();
            if (_logTextBox is not null)
                _logTextBox.Text = "";
        });

        // 附加按钮：仅在未启动外部程序时可用
        _attachButton.OnClick(() =>
        {
            _viewModel.ToggleAttach();
            UpdateButtonStates();
            UpdateStatusText();
            FlushLogToTextBox();
        });

        // 启动按钮：仅在未附加进程时可用
        _launchButton.OnClick(() =>
        {
            if (_viewModel.IsLauncherRunning)
            {
                _viewModel.DetachFromProcess();
                UpdateButtonStates();
                UpdateStatusText();
                FlushLogToTextBox();
                return;
            }

            var fileDialog = Application.Current.PlatformHost.FileDialog;
            var files = fileDialog.OpenFile(new OpenFileDialogOptions
            {
                Title = "Select executable",
                Filter = "Executable files (*.exe)|*.exe|All files (*.*)|*.*",
            });

            if (files is not { Length: > 0 })
                return;

            _viewModel.LaunchProcess(files[0]);
            UpdateButtonStates();
            UpdateStatusText();
            FlushLogToTextBox();
        });

        // 订阅日志更新：通过调度器实时投递到 UI 线程，同时刷新按钮和状态栏。
        // 注：不在 OnBuild 中捕获 Application.Current.Dispatcher（此时可能尚未初始化），
        // 改为在事件触发时延迟访问——此时应用已完全运行。
        _viewModel.LogUpdated += () =>
        {
            Application.Current.Dispatcher?.BeginInvoke(() =>
            {
                FlushLogToTextBox();
                UpdateButtonStates();
                UpdateStatusText();
            });
        };

        stackPanel.Children(
            new TextBlock { Text = "Processes:" },
            processesComboBox,
            refreshButton,
            _attachButton,
            _launchButton,
            _openFileButton,
            clearButton,
            _autoScrollCheckBox);

        return stackPanel;
    }

    /// <summary>
    /// 将 ViewModel 中的增量日志追加到 UI 控件。
    /// 只有启用了自动滚动且用户没有手动向上滚动时才滚动到底部。
    /// </summary>
    private static void FlushLogToTextBox()
    {
        var pending = _viewModel.DrainPendingLogs();
        if (pending is null || _logTextBox is null)
            return;

        bool doScroll = _autoScroll && IsTextBoxAtBottom();
        _logTextBox.AppendText(pending, scrollToCaret: doScroll);
    }

    /// <summary>
    /// 监听垂直滚动条的手动滚动，自动同步 Auto-scroll 复选框状态。
    /// </summary>
    private static void HookScrollBarTracking()
    {
        if (FindVerticalScrollBar(_logTextBox) is ScrollBar vBar)
        {
            vBar.ValueChanged += value =>
            {
                bool atBottom = value >= vBar.Maximum - 0.5;
                _autoScroll = atBottom;
                _autoScrollCheckBox?.IsChecked(atBottom);
            };
        }
    }

    /// <summary>
    /// 检查 MultiLineTextBox 是否滚动到了最底部。
    /// </summary>
    private static bool IsTextBoxAtBottom()
    {
        if (FindVerticalScrollBar(_logTextBox) is not ScrollBar vBar || !vBar.IsVisible)
            return true;

        const double tolerance = 0.5;
        return vBar.Value >= vBar.Maximum - tolerance;
    }

    /// <summary>
    /// 通过视觉树找到 MultiLineTextBox 内部的垂直 ScrollBar。
    /// </summary>
    private static ScrollBar? FindVerticalScrollBar(Element element)
    {
        if (element is IVisualTreeHost host)
        {
            ScrollBar? result = null;
            host.VisitChildren(child =>
            {
                if (child is ScrollBar bar && bar.Orientation == Orientation.Vertical)
                {
                    result = bar;
                    return false; // 找到了，停止遍历
                }
                return true; // 继续查找
            });
            return result;
        }
        return null;
    }

    /// <summary>
    /// 更新按钮状态：三个按钮互斥——同一时间只能有一个活跃。
    /// 各自的 Stop 按钮仅停止自己启动的操作。
    /// </summary>
    private static void UpdateButtonStates()
    {
        bool anyActive = _viewModel.IsDiagnosticAttached ||
                         _viewModel.IsLauncherRunning ||
                         _viewModel.IsFileMonitoring;

        if (_attachButton is not null)
        {
            _attachButton.Content(_viewModel.IsDiagnosticAttached ? "Stop" : "Attach");
            _attachButton.IsEnabled = !anyActive || _viewModel.IsDiagnosticAttached;
        }

        if (_launchButton is not null)
        {
            _launchButton.Content(_viewModel.IsLauncherRunning ? "Stop" : "Launch");
            _launchButton.IsEnabled = !anyActive || _viewModel.IsLauncherRunning;
        }

        if (_openFileButton is not null)
        {
            _openFileButton.Content(_viewModel.IsFileMonitoring ? "Stop" : "Open File");
            _openFileButton.IsEnabled = !anyActive || _viewModel.IsFileMonitoring;
        }
    }

    /// <summary>
    /// 更新状态栏文本
    /// </summary>
    private static void UpdateStatusText()
    {
        if (_statusText is null)
            return;
        _statusText.Text = _viewModel.StatusText;
    }

    /// <summary>
    /// 平台后端注册
    /// </summary>
    private static void PlatformBackendRegister()
    {
        if (OperatingSystem.IsWindows())
        {
            Win32Platform.Register();
            GdiBackend.Register();
        }
        else if (OperatingSystem.IsMacOS())
        {
            MacOSPlatform.Register();
            MewVGMacOSBackend.Register();
        }
        else if (OperatingSystem.IsLinux())
        {
            X11Platform.Register();
            MewVGX11Backend.Register();
        }
    }
}
