using System.Text;

namespace LogSniffer.Helpers;

/// <summary>
/// 监控日志文件变化，将新增内容实时输出。
/// 主路径：FileSystemWatcher 事件驱动，立即增量读取，通过文件位置天然去重。
/// 兜底：低频轮询（2s），仅在 FileSystemWatcher 漏事件时补偿。
/// </summary>
public sealed class FileMonitorHelper : IDisposable
{
    private readonly string _filePath;
    private readonly Action<string> _onOutput;
    private readonly string _fileName;
    private readonly string _directory;

    private FileSystemWatcher? _watcher;
    private System.Timers.Timer? _pollTimer;
    private long _lastPosition;
    private int _readGate;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    /// <summary>轮询间隔 (毫秒)，纯兜底机制。</summary>
    private const int PollIntervalMs = 2000;

    /// <summary>
    /// 是否正在监控文件。
    /// </summary>
    public bool IsMonitoring { get; private set; }

    /// <summary>
    /// 当前监控的文件完整路径。
    /// </summary>
    public string FilePath => _filePath;

    /// <summary>
    /// 初始化文件监控器。
    /// </summary>
    /// <param name="filePath">要监控的日志文件路径。</param>
    /// <param name="onOutput">输出回调，每次读到新内容时调用。</param>
    public FileMonitorHelper(string filePath, Action<string> onOutput)
    {
        _filePath = Path.GetFullPath(filePath);
        _onOutput = onOutput ?? throw new ArgumentNullException(nameof(onOutput));
        _fileName = Path.GetFileName(_filePath);
        _directory = Path.GetDirectoryName(_filePath)
                     ?? throw new ArgumentException("Cannot determine directory from file path.");
    }

    /// <summary>
    /// 启动文件监控：先读取现有内容，再通过 FileSystemWatcher + 轮询监听增量。
    /// </summary>
    public void Start()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(FileMonitorHelper));
        if (IsMonitoring)
            return;

        if (!File.Exists(_filePath))
        {
            _onOutput($"[FileMonitor] File not found: {_filePath}\n");
            return;
        }

        try
        {
            ReadInitialContent();

            _watcher = new FileSystemWatcher(_directory, _fileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = false,
                InternalBufferSize = 65536,
            };

            _watcher.Changed += OnFileChanged;
            _watcher.Created += OnFileChanged;
            _watcher.Deleted += OnFileDeleted;
            _watcher.Renamed += OnFileRenamed;
            _watcher.Error += OnWatcherError;

            _watcher.EnableRaisingEvents = true;
            IsMonitoring = true;

            _pollTimer = new System.Timers.Timer(PollIntervalMs);
            _pollTimer.AutoReset = true;
            _pollTimer.Elapsed += (_, _) => TriggerRead();
            _pollTimer.Start();

            _onOutput($"[FileMonitor] Monitoring changes...\n");

            // 追赶读取：弥补初始读取与 watcher 启动之间的竞态窗口
            TriggerRead();
        }
        catch (Exception ex)
        {
            _onOutput($"[FileMonitor] Error: {ex.Message}\n");
            Stop();
        }
    }

    /// <summary>
    /// 停止文件监控，释放 Watcher 和定时器。
    /// </summary>
    public void Stop()
    {
        if (!IsMonitoring)
            return;

        IsMonitoring = false;

        if (_pollTimer is not null)
        {
            _pollTimer.Stop();
            _pollTimer.Dispose();
            _pollTimer = null;
        }

        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnFileChanged;
            _watcher.Created -= OnFileChanged;
            _watcher.Deleted -= OnFileDeleted;
            _watcher.Renamed -= OnFileRenamed;
            _watcher.Error -= OnWatcherError;
            _watcher.Dispose();
            _watcher = null;
        }

        _onOutput($"[FileMonitor] Stopped monitoring: {_filePath}\n");
    }

    /// <summary>
    /// 释放所有资源。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
        _cts.Cancel();
        _cts.Dispose();
    }

    #region Initial Read

    /// <summary>
    /// 读取文件末尾最多 1 MB 的内容作为初始显示。
    /// </summary>
    private void ReadInitialContent()
    {
        var fileInfo = new FileInfo(_filePath);

        if (fileInfo.Length == 0)
        {
            _lastPosition = 0;
            _onOutput($"[FileMonitor] Opened: {_filePath} (empty)\n");
            return;
        }

        const long maxInitialRead = 1_048_576; // 1 MB
        long startPos = fileInfo.Length > maxInitialRead ? fileInfo.Length - maxInitialRead : 0;
        string initialContent;

        using (var fs = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(fs, Encoding.UTF8))
        {
            if (startPos > 0)
            {
                fs.Seek(startPos, SeekOrigin.Begin);
                reader.ReadLine(); // 跳过不完整的第一行
                initialContent = reader.ReadToEnd();
                _onOutput($"[FileMonitor] File is large ({fileInfo.Length / 1024:N0} KB), showing last {maxInitialRead / 1024:N0} KB.\n");
            }
            else
            {
                initialContent = reader.ReadToEnd();
            }

            _lastPosition = fs.Position;
        }

        if (!string.IsNullOrEmpty(initialContent))
        {
            _onOutput($"[FileMonitor] Opened: {_filePath}\n");
            _onOutput(initialContent);
            if (!initialContent.EndsWith('\n'))
                _onOutput("\n");
        }
        else
        {
            _onOutput($"[FileMonitor] Opened: {_filePath} (empty)\n");
        }
    }

    #endregion

    #region FSWatcher Events

    /// <summary>
    /// FileSystemWatcher 事件 → 立即触发增量读取。
    /// </summary>
    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        if (e.ChangeType != WatcherChangeTypes.Changed &&
            e.ChangeType != WatcherChangeTypes.Created)
            return;

        TriggerRead();
    }

    /// <summary>
    /// 文件被删除时停止监控。
    /// </summary>
    private void OnFileDeleted(object sender, FileSystemEventArgs e)
    {
        _onOutput($"[FileMonitor] File deleted: {_filePath}\n");
        Stop();
    }

    /// <summary>
    /// 文件被重命名时停止监控。
    /// </summary>
    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        _onOutput($"[FileMonitor] File renamed: {_filePath}\n");
        Stop();
    }

    /// <summary>
    /// Watcher 内部缓冲区溢出时不停止，轮询定时器会兜底。
    /// </summary>
    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        _onOutput($"[FileMonitor] Watcher notice (polling fallback active): {e.GetException().Message}\n");
    }

    #endregion

    #region Incremental Read

    /// <summary>
    /// 触发一次增量读取。使用 interlocked gate 避免重复线程堆积。
    /// 多个并发调用只会有第一个真正执行。
    /// </summary>
    private void TriggerRead()
    {
        if (_disposed || !IsMonitoring)
            return;

        if (Interlocked.CompareExchange(ref _readGate, 1, 0) != 0)
            return;

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                ReadIncrementalContent();
            }
            finally
            {
                Interlocked.Exchange(ref _readGate, 0);
            }
        });
    }

    /// <summary>
    /// 从文件当前位置读取新增内容并输出。
    /// 通过 _lastPosition 实现天然去重。
    /// </summary>
    private void ReadIncrementalContent()
    {
        if (_disposed || !IsMonitoring)
            return;

        try
        {
            if (!File.Exists(_filePath))
            {
                _onOutput($"[FileMonitor] File no longer exists.\n");
                Stop();
                return;
            }

            var fileInfo = new FileInfo(_filePath);
            long currentLength = fileInfo.Length;

            if (currentLength < _lastPosition)
                _lastPosition = 0; // 文件被截断，从头开始

            if (currentLength <= _lastPosition)
                return; // 没有新内容

            long bytesToRead = currentLength - _lastPosition;
            if (bytesToRead > 131_072)
                bytesToRead = 131_072;

            using var fs = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            fs.Seek(_lastPosition, SeekOrigin.Begin);

            var buffer = new byte[bytesToRead];
            int bytesRead = fs.Read(buffer, 0, (int)bytesToRead);

            if (bytesRead > 0)
            {
                string text = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                _onOutput(text);
            }

            _lastPosition = fs.Position;
        }
        catch (IOException)
        {
            // 文件被锁定，下次触发时重试
        }
        catch (Exception ex)
        {
            _onOutput($"[FileMonitor] Read error: {ex.Message}\n");
        }
    }

    #endregion
}
