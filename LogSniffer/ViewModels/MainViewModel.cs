using Aprillz.MewUI;
using LogSniffer.Helpers;
using LogSniffer.Models;
using System.Collections.ObjectModel;
using System.Text;
using System.Text.RegularExpressions;

namespace LogSniffer.ViewModels;

public class MainViewModel
{
    #region Public Properties

    /// <summary>
    /// 当前运行中的 .NET 进程列表。
    /// </summary>
    public ObservableCollection<ProcessItem> ProcessItems { get; set; }

    /// <summary>
    /// 进程列表的 ComboBox 绑定源。
    /// </summary>
    public ItemsView<ProcessItem> ProcessListView { get; set; }

    /// <summary>
    /// 用户在 ComboBox 中选中的进程。
    /// </summary>
    public ProcessItem? SelectedProcess { get; set; }

    /// <summary>
    /// 是否已附加到进程的诊断事件流。
    /// </summary>
    public bool IsDiagnosticAttached => _diagnosticClient?.IsListening ?? false;

    /// <summary>
    /// 是否正在运行外部启动的进程。
    /// </summary>
    public bool IsLauncherRunning => _launcher?.IsRunning ?? false;

    /// <summary>
    /// 是否正在监控日志文件。
    /// </summary>
    public bool IsFileMonitoring => _fileMonitor?.IsMonitoring ?? false;

    #endregion

    #region Events

    /// <summary>
    /// 有新日志时触发（任意线程），View 层负责调度到 UI 线程。
    /// </summary>
    public event Action? LogUpdated;

    /// <summary>
    /// 过滤条件变化时触发（FilterText 或 IsRegex 任一变更）。
    /// </summary>
    public event Action? FilterChanged;

    #endregion

    #region Observable Values

    /// <summary>
    /// 选中进程在 ProcessItems 中的索引，绑定到 ComboBox 的 SelectedIndex。
    /// </summary>
    public ObservableValue<int> SelectedProcessIndex { get; } = new(0);

    /// <summary>
    /// 日志过滤文本（大小写不敏感），空字符串表示不过滤。
    /// </summary>
    public ObservableValue<string> FilterText { get; } = new("");

    /// <summary>
    /// 是否将 FilterText 视为正则表达式。
    /// </summary>
    public ObservableValue<bool> IsRegex { get; } = new(true);

    /// <summary>
    /// 是否自动滚动到日志底部。
    /// </summary>
    public ObservableValue<bool> AutoScroll { get; } = new(true);

    /// <summary>
    /// 状态栏文本，绑定到 TextBlock。
    /// </summary>
    public ObservableValue<string> StatusText { get; } = new("Ready");

    /// <summary>
    /// 当前日志总行数。
    /// </summary>
    public ObservableValue<int> LineCount { get; } = new(0);

    #endregion

    #region Private Fields

    /// <summary>
    /// 编译后的正则表达式缓存（IsRegex 为 true 时有效）。
    /// </summary>
    private Regex? _cachedRegex;

    /// <summary>
    /// 增量日志缓冲区（Drain 后清空）。
    /// </summary>
    private readonly StringBuilder _pendingLogs = new();

    /// <summary>
    /// 全量日志缓冲区（不受 Drain 影响，用于过滤条件变化时重建显示）。
    /// </summary>
    private readonly StringBuilder _allLogs = new();

    private DiagnosticClientHelper? _diagnosticClient;
    private ProcessLauncherHelper? _launcher;
    private FileMonitorHelper? _fileMonitor;
    private string _launchedPath = "";

    #endregion

    #region Constructor

    public MainViewModel()
    {
        ProcessItems = [];
        ProcessListView = ItemsView.Create(
            ProcessItems,
            item => $"[{item.Runtime}] {item.Name}:{item.Pid}");

        SelectedProcessIndex.Subscribe(() =>
        {
            if (SelectedProcessIndex.Value >= 0 && SelectedProcessIndex.Value < ProcessItems.Count)
                SelectedProcess = ProcessItems[SelectedProcessIndex.Value];
            else
                SelectedProcess = null;
        });

        // 过滤条件变更 → 清空正则缓存 + 通知 View 重建显示
        FilterText.Changed += () =>
        {
            _cachedRegex = null;
            FilterChanged?.Invoke();
        };

        IsRegex.Changed += () =>
        {
            _cachedRegex = null;
            FilterChanged?.Invoke();
        };
    }

    #endregion

    #region Process List

    /// <summary>
    /// 刷新进程列表（扫描所有运行中的 .NET 进程）。
    /// </summary>
    /// <param name="isFirstTime">是否为首次刷新</param>
    internal void RefreshProcessList()
    {
        var processes = ProcessHelper.GetDotNetProcesses();

        ProcessItems.Clear();
        foreach (var proc in processes)
            ProcessItems.Add(proc);

        ProcessListView.Invalidate();

        // 首次刷新时如果有进程则默认选中第一个
        if (ProcessItems.Count > 0)
        {
            SelectedProcessIndex.Value = 0;
            SelectedProcess = ProcessItems[0];
        }
    }

    #endregion

    #region Diagnostics Attach

    /// <summary>
    /// 切换附加/分离状态。
    /// </summary>
    internal void ToggleAttach()
    {
        if (IsDiagnosticAttached)
            DetachFromProcess();
        else
            AttachToProcess();
    }

    /// <summary>
    /// 附加到选中进程的诊断事件流（EventPipe 或 ETW）。
    /// </summary>
    private void AttachToProcess()
    {
        if (SelectedProcess is null)
        {
            AppendLog("[Error] No process selected.\n");
            return;
        }

        try
        {
            _diagnosticClient?.Dispose();
            _diagnosticClient = new DiagnosticClientHelper(
                SelectedProcess.Pid,
                SelectedProcess.Runtime,
                AppendLog);
            _diagnosticClient.StartListening();
            StatusText.Value = $"Attached to {SelectedProcess.Name}:{SelectedProcess.Pid}";
        }
        catch (Exception ex)
        {
            AppendLog($"[Error] Failed to attach: {ex.Message}\n");
            StatusText.Value = "Ready";
        }
    }

    /// <summary>
    /// 分离进程诊断，同时停止任何启动的外部进程或文件监控。
    /// </summary>
    internal void DetachFromProcess()
    {
        _diagnosticClient?.Dispose();
        _diagnosticClient = null;
        _launcher?.Dispose();
        _launcher = null;
        StopFileMonitor();
        StatusText.Value = "Ready";
    }

    #endregion

    #region File Monitoring

    /// <summary>
    /// 打开并监控日志文件，实时输出新增内容。
    /// </summary>
    internal void OpenFile(string filePath)
    {
        try
        {
            DetachFromProcess();
            _fileMonitor = new FileMonitorHelper(filePath, AppendLog);
            _fileMonitor.Start();
            StatusText.Value = $"Monitoring {filePath}";
        }
        catch (Exception ex)
        {
            AppendLog($"[Error] Failed to open file: {ex.Message}\n");
            StatusText.Value = "Ready";
        }
    }

    /// <summary>
    /// 停止文件监控。
    /// </summary>
    internal void StopFileMonitor()
    {
        _fileMonitor?.Dispose();
        _fileMonitor = null;
    }

    #endregion

    #region Process Launch

    /// <summary>
    /// 启动一个可执行文件并捕获其 stdout/stderr 输出。
    /// </summary>
    internal void LaunchProcess(string filePath)
    {
        try
        {
            DetachFromProcess();
            _launchedPath = filePath;
            _launcher = new ProcessLauncherHelper(AppendLog);
            _launcher.Start(filePath);
            StatusText.Value = $"Running {filePath} (PID {_launcher.ProcessId})";
        }
        catch (Exception ex)
        {
            _launchedPath = "";
            StatusText.Value = "Ready";
            AppendLog($"[Error] Failed to launch: {ex.Message}\n");
        }
    }

    #endregion

    #region Log Buffer

    /// <summary>
    /// 取出待处理的增量日志并清空缓冲区。如设置了过滤条件则仅返回匹配行。
    /// 返回 null 表示没有新日志。
    /// </summary>
    internal string? DrainPendingLogs()
    {
        lock (_pendingLogs)
        {
            if (_pendingLogs.Length == 0)
                return null;

            var text = _pendingLogs.ToString();
            _pendingLogs.Clear();
            return string.IsNullOrEmpty(FilterText.Value) ? text : FilterByText(text);
        }
    }

    /// <summary>
    /// 获取全量日志的过滤后文本，用于过滤条件变化时重建显示。
    /// 返回 null 表示没有任何日志。
    /// </summary>
    internal string? GetFilteredAllLogs()
    {
        lock (_allLogs)
        {
            if (_allLogs.Length == 0)
                return null;
            var text = _allLogs.ToString();
            return string.IsNullOrEmpty(FilterText.Value) ? text : FilterByText(text);
        }
    }

    /// <summary>
    /// 清空全部日志缓冲区并刷新 UI。
    /// </summary>
    internal void ClearLog()
    {
        lock (_pendingLogs)
            _pendingLogs.Clear();
        lock (_allLogs)
            _allLogs.Clear();
        LineCount.Value = 0;
    }

    /// <summary>
    /// 追加日志（后台线程安全），同时写入增量和全量缓冲区。
    /// </summary>
    private void AppendLog(string text)
    {
        lock (_pendingLogs)
            _pendingLogs.Append(text);
        lock (_allLogs)
            _allLogs.Append(text);

        int newLines = 0;
        foreach (char c in text)
        {
            if (c == '\n')
                newLines++;
        }
        if (newLines > 0)
            LineCount.Value += newLines;

        LogUpdated?.Invoke();
    }

    #endregion

    #region Filtering

    /// <summary>
    /// 按行过滤文本：普通模式为大小写不敏感的包含匹配，
    /// 正则模式下将 FilterText 视为正则表达式。
    /// </summary>
    private string FilterByText(string text)
    {
        var filter = FilterText.Value;
        if (!IsRegex.Value)
            return FilterBySubstring(text, filter);

        Regex? regex;
        try
        {
            regex = _cachedRegex ??= new Regex(filter,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        catch (RegexParseException)
        {
            return string.Empty;
        }

        var lines = text.Split('\n');
        var sb = new StringBuilder(text.Length);
        foreach (var line in lines)
        {
            if (regex.IsMatch(line))
            {
                sb.Append(line);
                sb.Append('\n');
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// 纯文本子串过滤（大小写不敏感）。
    /// </summary>
    private static string FilterBySubstring(string text, string filter)
    {
        var lines = text.Split('\n');
        var sb = new StringBuilder(text.Length);
        foreach (var line in lines)
        {
            if (line.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                sb.Append(line);
                sb.Append('\n');
            }
        }
        return sb.ToString();
    }

    #endregion
}
