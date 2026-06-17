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
    private int _readGate; // 0 = idle, 1 = reading (interlocked)
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    /// <summary>轮询间隔 (毫秒)，纯兜底机制。</summary>
    private const int PollIntervalMs = 2000;

    public bool IsMonitoring { get; private set; }
    public string FilePath => _filePath;

    public FileMonitorHelper(string filePath, Action<string> onOutput)
    {
        _filePath = Path.GetFullPath(filePath);
        _onOutput = onOutput ?? throw new ArgumentNullException(nameof(onOutput));
        _fileName = Path.GetFileName(_filePath);
        _directory = Path.GetDirectoryName(_filePath)
                     ?? throw new ArgumentException("Cannot determine directory from file path.");
    }

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
            // ── 1. 读取现有内容 ──
            ReadInitialContent();

            // ── 2. 启动 FileSystemWatcher (事件驱动主路径) ──
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

            // ── 3. 低频轮询定时器 (纯兜底，FileSystemWatcher 漏事件时补偿) ──
            _pollTimer = new System.Timers.Timer(PollIntervalMs);
            _pollTimer.AutoReset = true;
            _pollTimer.Elapsed += (_, _) => TriggerRead();
            _pollTimer.Start();

            _onOutput($"[FileMonitor] Monitoring changes...\n");

            // ── 4. 追赶读取：弥补初始读取与 watcher 启动之间的竞态窗口 ──
            TriggerRead();
        }
        catch (Exception ex)
        {
            _onOutput($"[FileMonitor] Error: {ex.Message}\n");
            Stop();
        }
    }

    // ════════════════════════════════════════════════════════════════
    // 初始读取
    // ════════════════════════════════════════════════════════════════

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

    // ════════════════════════════════════════════════════════════════
    // 事件处理 (FSWatcher / 轮询统一入口)
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// FileSystemWatcher 事件 → 立即触发读取。
    /// 不做去重/防抖——如果文件没有新内容，ReadIncrementalContent 会快速返回。
    /// </summary>
    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        if (e.ChangeType != WatcherChangeTypes.Changed &&
            e.ChangeType != WatcherChangeTypes.Created)
            return;

        TriggerRead();
    }

    private void OnFileDeleted(object sender, FileSystemEventArgs e)
    {
        _onOutput($"[FileMonitor] File deleted: {_filePath}\n");
        Stop();
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        _onOutput($"[FileMonitor] File renamed: {_filePath}\n");
        Stop();
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        // 缓冲区溢出——不停止，轮询定时器会兜底
        _onOutput($"[FileMonitor] Watcher notice (polling fallback active): {e.GetException().Message}\n");
    }

    /// <summary>
    /// 触发一次增量读取。使用 interlocked gate 避免重复线程堆积。
    /// 多个并发调用只会有第一个真正执行；后续调用发现 _lastPosition 已更新则立即返回。
    /// </summary>
    private void TriggerRead()
    {
        if (_disposed || !IsMonitoring)
            return;

        // 轻量级 gate：0→1 成功则执行，已经是 1 则跳过（说明已有线程在处理）
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

    // ════════════════════════════════════════════════════════════════
    // 增量读取
    // ════════════════════════════════════════════════════════════════

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
            {
                // 文件被截断
                _lastPosition = 0;
            }

            if (currentLength <= _lastPosition)
                return; // 没有新内容——这是天然的"去重"

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

    // ════════════════════════════════════════════════════════════════
    // 停止 & 清理
    // ════════════════════════════════════════════════════════════════

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

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
        _cts.Cancel();
        _cts.Dispose();
    }
}
