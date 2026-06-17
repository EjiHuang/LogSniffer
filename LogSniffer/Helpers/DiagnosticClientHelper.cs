using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using LogSniffer.Models;
using System.Runtime.CompilerServices;

namespace LogSniffer.Helpers;

/// <summary>
/// 进程诊断帮助类，支持双路径：
/// - .NET Core / .NET 5+：通过 EventPipe (DiagnosticsClient)
/// - .NET Framework    ：通过 ETW 实时会话 (TraceEventSession)
/// </summary>
public sealed class DiagnosticClientHelper : IDisposable
{
    private readonly int _processId;
    private readonly ProcessRuntime _runtime;
    private readonly Action<string> _onOutput;
    private readonly CancellationTokenSource _cts = new();
    private Task? _listenTask;
    private EventPipeSession? _eventPipeSession;
    private TraceEventSession? _etwSession;
    private OutputDebugStringMonitor? _odsMonitor;
    private bool _disposed;

    public bool IsListening { get; private set; }

    /// <param name="processId">目标进程 PID</param>
    /// <param name="runtime">运行时类型（决定用 EventPipe 还是 ETW）</param>
    /// <param name="onOutput">输出回调（在后台线程调用）</param>
    public DiagnosticClientHelper(int processId, ProcessRuntime runtime, Action<string> onOutput)
    {
        _processId = processId;
        _runtime = runtime;
        _onOutput = onOutput ?? throw new ArgumentNullException(nameof(onOutput));
    }

    public void StartListening()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(DiagnosticClientHelper));
        if (IsListening)
            return;

        if (_runtime == ProcessRuntime.CoreCLR)
            StartListeningEventPipe();
        else
            StartListeningEtw();
    }

    // ════════════════════════════════════════════════════════════
    // .NET Core：EventPipe 路径
    // ════════════════════════════════════════════════════════════

    private void StartListeningEventPipe()
    {
        var client = new DiagnosticsClient(_processId);

        var providers = new List<EventPipeProvider>
        {
            // .NET 运行时事件（异常、GC 等所有关键字）
            new("Microsoft-Windows-DotNETRuntime",
                System.Diagnostics.Tracing.EventLevel.LogAlways,
                (long)ClrEventKeywords.All),

            // Microsoft.Extensions.Logging EventSource 日志事件
            new("Microsoft-Extensions-Logging",
                System.Diagnostics.Tracing.EventLevel.LogAlways,
                -1),
        };

        _eventPipeSession = client.StartEventPipeSession(providers, requestRundown: true);
        IsListening = true;

        _listenTask = Task.Run(() =>
        {
            try
            {
                using var source = new EventPipeEventSource(_eventPipeSession.EventStream);
                source.Dynamic.All += OnEvent;
                source.Process();
            }
            catch (Exception ex)
            {
                _onOutput($"[EventPipe Error] {ex.GetType().Name}: {ex.Message}\n");
            }
            finally
            {
                if (!_cts.IsCancellationRequested)
                {
                    IsListening = false;
                    _onOutput($"[EventPipe] Disconnected from PID {_processId}\n");
                }
            }
        }, _cts.Token);

        _onOutput($"[EventPipe] Attached to PID {_processId} (CoreCLR)\n");

        // Windows：额外启动 OutputDebugString 监听（捕获 Debug.Write / Trace.Write）
        if (OperatingSystem.IsWindows())
        {
            try
            {
                _odsMonitor = new OutputDebugStringMonitor(_processId, (timestamp, msg) =>
                    _onOutput($"[{timestamp:HH:mm:ss.fff}] [Debug] {msg}\n"));
                _odsMonitor.Start();
                _onOutput("[OutputDebugString] Listening for Debug.Write/Trace.Write...\n");
            }
            catch (Exception ex)
            {
                _onOutput($"[OutputDebugString] Unavailable: {ex.Message}\n");
            }
        }
    }

    // ════════════════════════════════════════════════════════════
    // .NET Framework：ETW 路径（参考 dnSpy 的做法）
    // ════════════════════════════════════════════════════════════

    private void StartListeningEtw()
    {
        // NativeAOT 下 ETW COM 会话不可用
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            throw new NotSupportedException(
                ".NET Framework ETW monitoring is not supported in NativeAOT builds. " +
                "Use regular self-contained publish for .NET Framework support.");
        }
        // 唯一的 ETW 会话名（避免冲突）
        var sessionName = $"LogSniffer_Etw_{_processId}_{Guid.NewGuid():N}";

        try
        {
            _etwSession = new TraceEventSession(sessionName, TraceEventSessionOptions.Create);

            // 启用 .NET CLR 运行时提供程序（.NET Framework 适用）
            // 关键字与 EventPipe 路径一致：Exception + GC
            // Microsoft-Windows-DotNETRuntime 提供程序 GUID
            // {E13C0D23-CCBC-4E12-931B-D9CC2EEE27E4}
            var clrProviderGuid = new Guid("e13c0d23-ccbc-4e12-931b-d9cc2eee27e4");
            _etwSession.EnableProvider(
                clrProviderGuid,
                Microsoft.Diagnostics.Tracing.TraceEventLevel.Informational,
                (ulong)(ClrEventKeywords.Exception | ClrEventKeywords.GC));

            IsListening = true;

            // ETW 是阻塞式处理的 —— 在当前线程上 Process()，所以需要后台线程
            _listenTask = Task.Run(() =>
            {
                using var source = _etwSession.Source;
                source.Dynamic.All += OnEvent;
                source.Process();
            }, _cts.Token);

            _onOutput($"[ETW] Attached to PID {_processId} (CLR/.NET Framework)\n");
        }
        catch (Exception ex)
        {
            _etwSession?.Dispose();
            _etwSession = null;
            throw new InvalidOperationException(
                $"Failed to start ETW session. Administrator privileges may be required. {ex.Message}", ex);
        }
    }

    // ════════════════════════════════════════════════════════════
    // 停止 & 清理
    // ════════════════════════════════════════════════════════════

    public void StopListening()
    {
        if (!IsListening)
            return;

        _cts.Cancel();

        try { _eventPipeSession?.Stop(); } catch { }
        try { _etwSession?.Stop(); } catch { }

        _odsMonitor?.Dispose();
        _odsMonitor = null;

        _eventPipeSession?.Dispose();
        _eventPipeSession = null;
        _etwSession?.Dispose();
        _etwSession = null;

        IsListening = false;
        _onOutput($"[Detached from PID {_processId}]\n");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopListening();
        _cts.Dispose();
    }

    // ════════════════════════════════════════════════════════════
    // 事件处理（EventPipe / ETW 共享）
    // ════════════════════════════════════════════════════════════

    private void OnEvent(TraceEvent evt)
    {
        var timestamp = evt.TimeStamp.ToString("HH:mm:ss.fff");
        var provider = evt.ProviderName;
        var eventName = evt.EventName;
        var payload = FormatPayload(evt);

        // CLR 运行时异常事件 —— 友好格式
        if (provider == "Microsoft-Windows-DotNETRuntime" && eventName.StartsWith("Exception/"))
        {
            _onOutput($"[{timestamp}] [Exception] {payload}\n");
            return;
        }

        // 跳过 CLR 运行时及 Rundown 事件（GC、JIT、加载等噪音）
        // 注意：Rundown provider 名称是 Microsoft-Windows-DotNETRuntimeRundown
        if (provider == "Microsoft-Windows-DotNETRuntime" ||
            provider == "Microsoft-Windows-DotNETRuntimeRundown")
            return;

        _onOutput($"[{timestamp}] [{provider}/{eventName}] {payload}\n");
    }

    private static string FormatPayload(TraceEvent evt)
    {
        var names = evt.PayloadNames;
        if (names.Length == 0)
            return string.Empty;

        var parts = new List<string>(names.Length);
        for (int i = 0; i < names.Length; i++)
        {
            var value = evt.PayloadValue(i)?.ToString() ?? "(null)";
            parts.Add($"{names[i]}={value}");
        }
        return string.Join(", ", parts);
    }

    [Flags]
    private enum ClrEventKeywords : long
    {
        None      = 0,
        GC        = 0x1,
        Exception = 0x8000,
        /// <summary>所有 CLR 运行时关键字 (Default = ~0)</summary>
        All       = -1,
    }
}

