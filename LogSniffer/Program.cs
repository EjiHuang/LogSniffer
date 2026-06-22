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

    #region UI Controls

    private static MultiLineTextBox? _logTextBox;
    private static TextBlock? _statusText;
    private static Button? _attachButton;
    private static Button? _launchButton;
    private static Button? _openFileButton;
    private static CheckBox? _autoScrollCheckBox;

    #endregion

    #region Entry Point

    static void Main(string[] args)
    {
        PlatformBackendRegister();

        _viewModel.RefreshProcessList();

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

                HookScrollBarTracking();
            });

        Application
            .Create()
            .UseTheme(ThemeVariant.Dark)
            .Run(window);
    }

    #endregion

    #region Platform Backend

    /// <summary>
    /// 注册当前操作系统对应的平台后端和渲染后端。
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

    #endregion

    #region Panel Build

    /// <summary>
    /// 构建顶部工具栏：功能按钮行 + 过滤输入行。
    /// 按钮行和过滤行包裹在垂直 StackPanel 中，DockTop 到主面板。
    /// </summary>
    private static StackPanel BuildTopBar()
    {
        // ── Row 1: 功能按钮 ──
        var buttonRow = new StackPanel().Orientation(Orientation.Horizontal);
        var processesComboBox = new ComboBox().Size(250, double.NaN).Margin(5, 0);
        var refreshButton = new Button().Margin(5, 0).Content("Refresh");
        _attachButton = new Button().Margin(5, 0).Content("Attach");
        _launchButton = new Button().Margin(5, 0).Content("Launch");
        _autoScrollCheckBox = new CheckBox().Margin(5, 0)
            .Content("Auto-scroll");

        _autoScrollCheckBox.BindIsChecked(_viewModel.AutoScroll);

        // 进程列表绑定
        processesComboBox.ItemsSource(_viewModel.ProcessListView);
        processesComboBox.BindSelectedIndex(_viewModel.SelectedProcessIndex);

        // 刷新进程列表
        refreshButton.OnClick(() =>
        {
            _viewModel.RefreshProcessList();
            FlushLogToTextBox();
        });

        // 打开 / 关闭文件监控
        _openFileButton = new Button().Margin(5, 0).Content("Open File");
        _openFileButton.OnClick(() =>
        {
            if (_viewModel.IsFileMonitoring)
            {
                _viewModel.StopFileMonitor();
                _viewModel.StatusText.Value = "Ready";
                UpdateButtonStates();
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
            FlushLogToTextBox();
        });

        // 清空日志
        var clearButton = new Button().Margin(5, 0).Content("Clear");
        clearButton.OnClick(() =>
        {
            _viewModel.ClearLog();
            if (_logTextBox is not null)
                _logTextBox.Text = "";
        });

        // 附加 / 停止附加
        _attachButton.OnClick(() =>
        {
            _viewModel.ToggleAttach();
            UpdateButtonStates();
            FlushLogToTextBox();
        });

        // 启动 / 停止外部进程
        _launchButton.OnClick(() =>
        {
            if (_viewModel.IsLauncherRunning)
            {
                _viewModel.DetachFromProcess();
                UpdateButtonStates();
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
            FlushLogToTextBox();
        });

        // 订阅日志更新 → 刷新 UI + 按钮状态（调度到 UI 线程）
        _viewModel.LogUpdated += () =>
        {
            Application.Current.Dispatcher?.BeginInvoke(() =>
            {
                FlushLogToTextBox();
                UpdateButtonStates();
            });
        };

        buttonRow.Children(
            new TextBlock { Text = "Processes:", Width = 70 },
            processesComboBox,
            refreshButton,
            _attachButton,
            _launchButton,
            _openFileButton,
            clearButton,
            _autoScrollCheckBox);

        // ── Row 2: 过滤输入 ──
        var filterRow = new DockPanel().LastChildFill(true).Margin(0, 10, 0, 0);
        var filterLabel = new TextBlock { Text = "Filter:", Width = 70 };
        var filterTextBox = new TextBox().Margin(5, 0, 0, 0).Size(double.NaN, double.NaN);
        var regexCheckBox = new CheckBox().Margin(5, 0, 0, 0).Content("Regex").DockRight();

        filterTextBox.BindText(_viewModel.FilterText);
        regexCheckBox.BindIsChecked(_viewModel.IsRegex);

        filterRow.Children(filterLabel, regexCheckBox, filterTextBox);

        // 过滤条件变更 → 重建日志显示
        _viewModel.FilterChanged += () =>
        {
            Application.Current.Dispatcher?.BeginInvoke(() => RebuildLogDisplay());
        };

        // ── 组装 ──
        var topBarContainer = new StackPanel();
        topBarContainer.Children(buttonRow, filterRow);
        return topBarContainer;
    }

    /// <summary>
    /// 构建状态栏：左侧显示状态信息，右侧显示行数。
    /// </summary>
    private static Border BuildStatusBar()
    {
        var border = new Border().Margin(5, 0);
        var panel = new DockPanel().LastChildFill(true);

        var lineCountText = new TextBlock().DockRight();
        lineCountText.BindText(_viewModel.LineCount, count => $"Lines: {count}");

        _statusText = new TextBlock();
        _statusText.BindText(_viewModel.StatusText);

        panel.Children(lineCountText, _statusText);
        border.Child = panel;
        return border;
    }

    /// <summary>
    /// 构建日志查看器（只读多行文本框）。
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

    #endregion

    #region Log Display

    /// <summary>
    /// 将 ViewModel 中的增量日志追加到 UI 控件。
    /// 仅当 AutoScroll 开启且用户在底部时自动滚动。
    /// </summary>
    private static void FlushLogToTextBox()
    {
        var pending = _viewModel.DrainPendingLogs();
        if (pending is null || _logTextBox is null)
            return;

        bool doScroll = _viewModel.AutoScroll.Value;
        _logTextBox.AppendText(pending, scrollToCaret: doScroll);
    }

    /// <summary>
    /// 过滤条件变化时从全量缓冲区重建整个日志显示。
    /// 设 Text 会导致滚动条归零从而触发 AutoScroll 关闭，
    /// 因此在操作完成后恢复 AutoScroll 原值。
    /// </summary>
    private static void RebuildLogDisplay()
    {
        if (_logTextBox is null)
            return;

        _viewModel.DrainPendingLogs();
        var filtered = _viewModel.GetFilteredAllLogs();
        bool wasAutoScroll = _viewModel.AutoScroll.Value;

        _logTextBox.Text = filtered ?? "";
        if (wasAutoScroll)
        {
            _logTextBox.ScrollToCaret();
            _viewModel.AutoScroll.Value = true;
        }
    }

    #endregion

    #region Scrollbar

    /// <summary>
    /// 监听垂直滚动条的<em>手动</em>滚动：
    /// 用户滚动离开底部 → 关闭自动滚动；
    /// 用户滚动到底部 → 不重新开启（仅能通过勾选复选框开启）。
    /// </summary>
    private static void HookScrollBarTracking()
    {
        if (FindVerticalScrollBar(_logTextBox!) is ScrollBar vBar)
        {
            double oldValue = vBar.Value;
            vBar.ValueChanged += value =>
            {
                // 往上滚动离开底部 → 关闭自动滚动
                if (oldValue > value)
                {
                    _viewModel.AutoScroll.Value = false;
                }

                oldValue = value;
            };
        }
    }

    /// <summary>
    /// 检查 MultiLineTextBox 是否已滚动到最底部。
    /// </summary>
    private static bool IsTextBoxAtBottom()
    {
        if (FindVerticalScrollBar(_logTextBox!) is not ScrollBar vBar || !vBar.IsVisible)
            return true;

        const double tolerance = 0.5;
        return vBar.Value >= vBar.Maximum - tolerance;
    }

    /// <summary>
    /// 通过视觉树查找 MultiLineTextBox 内部的垂直 ScrollBar。
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
                    return false;
                }
                return true;
            });
            return result;
        }
        return null;
    }

    #endregion

    #region Button State

    /// <summary>
    /// 更新按钮状态：三个操作按钮（Attach / Launch / Open File）互斥，
    /// 同一时间只能有一个活跃。各自的按钮在活跃时显示 "Stop"。
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

    #endregion
}
