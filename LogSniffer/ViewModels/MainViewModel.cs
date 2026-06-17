using Aprillz.MewUI;
using LogSniffer.Helpers;
using LogSniffer.Models;
using System.Collections.ObjectModel;
using System.Text;

namespace LogSniffer.ViewModels;

public class MainViewModel
{
    public ObservableCollection<ProcessItem> ProcessItems { get; set; }
    public ItemsView<ProcessItem> ProcessListView { get; set; }
    public ProcessItem? SelectedProcess { get; set; }
    public bool IsDiagnosticAttached => _diagnosticClient?.IsListening ?? false;
    public bool IsLauncherRunning => _launcher?.IsRunning ?? false;
    public bool IsFileMonitoring => _fileMonitor?.IsMonitoring ?? false;

    /// <summary>
    /// 有新日志时触发（任意线程），View 层负责调度到 UI 线程。
    /// </summary>
    public event Action? LogUpdated;

    /// <summary>
    /// 状态栏文本（由 View 层读取）。
    /// </summary>
    public string StatusText
    {
        get
        {
            if (_diagnosticClient?.IsListening == true)
                return $"Attached to {SelectedProcess?.Name}:{SelectedProcess?.Pid}";
            if (_launcher?.IsRunning == true)
                return $"Running {_launchedPath} (PID {_launcher.ProcessId})";
            if (_fileMonitor?.IsMonitoring == true)
                return $"Monitoring {_fileMonitor.FilePath}";
            return "Ready";
        }
    }

    private readonly StringBuilder _pendingLogs = new();
    private DiagnosticClientHelper? _diagnosticClient;
    private ProcessLauncherHelper? _launcher;
    private FileMonitorHelper? _fileMonitor;
    private string _launchedPath = "";

    public MainViewModel()
    {
        ProcessItems = [];
        ProcessListView = ItemsView.Create(
            ProcessItems,
            item => $"[{item.Runtime}] {item.Name}:{item.Pid}");
    }

    internal void RefreshProcessList()
    {
        var processes = ProcessHelper.GetDotNetProcesses();

        ProcessItems.Clear();
        foreach (var proc in processes)
        {
            ProcessItems.Add(proc);
        }

        ProcessListView.Invalidate();
    }

    internal void ToggleAttach()
    {
        if (IsDiagnosticAttached)
            DetachFromProcess();
        else
            AttachToProcess();
    }

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
        }
        catch (Exception ex)
        {
            AppendLog($"[Error] Failed to attach: {ex.Message}\n");
        }
    }

    internal void DetachFromProcess()
    {
        _diagnosticClient?.Dispose();
        _diagnosticClient = null;
        _launcher?.Dispose();
        _launcher = null;
        StopFileMonitor();
    }

    /// <summary>
    /// 打开并监控日志文件。
    /// </summary>
    internal void OpenFile(string filePath)
    {
        try
        {
            DetachFromProcess();
            _fileMonitor = new FileMonitorHelper(filePath, AppendLog);
            _fileMonitor.Start();
        }
        catch (Exception ex)
        {
            AppendLog($"[Error] Failed to open file: {ex.Message}\n");
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
        }
        catch (Exception ex)
        {
            _launchedPath = "";
            AppendLog($"[Error] Failed to launch: {ex.Message}\n");
        }
    }

    /// <summary>
    /// 取出待处理的日志文本，返回 null 表示没有新日志。
    /// </summary>
    internal string? DrainPendingLogs()
    {
        lock (_pendingLogs)
        {
            if (_pendingLogs.Length == 0)
                return null;

            var text = _pendingLogs.ToString();
            _pendingLogs.Clear();
            return text;
        }
    }

    internal void ClearLog()
    {
        lock (_pendingLogs)
        {
            _pendingLogs.Clear();
        }
    }

    /// <summary>
    /// 追加日志（后台线程安全）。每次追加都触发 LogUpdated，
    /// 由 MewUI 调度器的 _invokeRequested 机制在底层合并重复的 WM_INVOKE 投递。
    /// </summary>
    private void AppendLog(string text)
    {
        lock (_pendingLogs)
        {
            _pendingLogs.Append(text);
        }

        LogUpdated?.Invoke();
    }
}
