using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace LogSniffer.Helpers;

/// <summary>
/// 捕获 Windows 全局 OutputDebugString 输出，按 PID 过滤。
/// 覆盖 Debug.Write / Trace.Write / OutputDebugString 调用。
/// 原理：通过 DBWIN_BUFFER 共享内存读取（类似 DebugView 的做法）。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class OutputDebugStringMonitor : IDisposable
{
    private readonly int _processId;
    private readonly Action<DateTime, string> _onOutput;
    private readonly CancellationTokenSource _cts = new();
    private readonly IntPtr _bufferReadyEvent;
    private readonly IntPtr _dataReadyEvent;
    private readonly IntPtr _sharedBuffer;
    private Task? _listenTask;
    private bool _disposed;

    // ════════════════════════════════════════════════════════
    // 原生 API
    // ════════════════════════════════════════════════════════

    private const string DbgWinBufferReady = "DBWIN_BUFFER_READY";
    private const string DbgWinDataReady   = "DBWIN_DATA_READY";
    private const string DbgWinBuffer      = "DBWIN_BUFFER";
    private const uint   BufferSize        = 4096;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateEventW(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenEventW(uint dwDesiredAccess, bool bInheritHandle, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateFileMappingW(IntPtr hFile, IntPtr lpFileMappingAttributes, uint flProtect, uint dwMaximumSizeHigh, uint dwMaximumSizeLow, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr MapViewOfFile(IntPtr hFileMappingObject, uint dwDesiredAccess, uint dwFileOffsetHigh, uint dwFileOffsetLow, UIntPtr dwNumberOfBytesToMap);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UnmapViewOfFile(IntPtr lpBaseAddress);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetEvent(IntPtr hEvent);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    private const uint SYNCHRONIZE        = 0x00100000;
    private const uint PAGE_READWRITE     = 0x04;
    private const uint FILE_MAP_READ      = 0x04;
    private const uint WAIT_OBJECT_0      = 0x00000000;
    private const uint WAIT_TIMEOUT       = 0x00000102;
    private const uint INFINITE           = 0xFFFFFFFF;

    public OutputDebugStringMonitor(int processId, Action<DateTime, string> onOutput)
    {
        _processId = processId;
        _onOutput = onOutput;

        // 创建/打开全局同步事件和共享内存
        _bufferReadyEvent = CreateEventW(IntPtr.Zero, false, false, DbgWinBufferReady);
        if (_bufferReadyEvent == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        _dataReadyEvent   = OpenEventW(SYNCHRONIZE, false, DbgWinDataReady);

        _sharedBuffer = CreateFileMappingW(
            new IntPtr(-1), // INVALID_HANDLE_VALUE
            IntPtr.Zero, PAGE_READWRITE,
            0, BufferSize, DbgWinBuffer);
    }

    public void Start()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(OutputDebugStringMonitor));

        var bufferAddr = MapViewOfFile(_sharedBuffer, FILE_MAP_READ, 0, 0, new UIntPtr(BufferSize));

        _listenTask = Task.Run(() =>
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    // 通知全局缓冲区：我们准备好了
                    SetEvent(_bufferReadyEvent);

                    // 等待数据
                    uint result = WaitForSingleObject(_dataReadyEvent, 100);
                    if (result == WAIT_TIMEOUT)
                        continue;
                    if (result != WAIT_OBJECT_0)
                        break; // 出错或已取消

                    if (_cts.IsCancellationRequested)
                        break;

                    // 从共享内存读取
                    int pid  = Marshal.ReadInt32(bufferAddr);
                    int size = Marshal.ReadInt32(bufferAddr, 4);

                    if (pid == _processId && size > 0)
                    {
                        string? text = Marshal.PtrToStringAnsi(bufferAddr + 8, Math.Min(size, (int)BufferSize - 8));
                        if (text is not null)
                            _onOutput(DateTime.Now, text);
                    }
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                UnmapViewOfFile(bufferAddr);
            }
        }, _cts.Token);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();

        if (_bufferReadyEvent != IntPtr.Zero) CloseHandle(_bufferReadyEvent);
        if (_dataReadyEvent   != IntPtr.Zero) CloseHandle(_dataReadyEvent);
        if (_sharedBuffer     != IntPtr.Zero) CloseHandle(_sharedBuffer);

        _cts.Dispose();
    }
}
