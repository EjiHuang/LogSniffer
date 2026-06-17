using System.Diagnostics;

namespace LogSniffer.Helpers;

/// <summary>
/// 启动进程并捕获其 stdout/stderr 输出。
/// </summary>
public sealed class ProcessLauncherHelper : IDisposable
{
    private readonly Action<string> _onOutput;
    private Process? _process;
    private bool _disposed;

    public bool IsRunning { get; private set; }
    public int ProcessId => _process?.Id ?? 0;

    public ProcessLauncherHelper(Action<string> onOutput)
    {
        _onOutput = onOutput;
    }

    public void Start(string filePath, string? arguments = null)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ProcessLauncherHelper));
        if (IsRunning)
            return;

        var psi = new ProcessStartInfo(filePath, arguments ?? string.Empty)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? string.Empty,
        };

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        _process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                _onOutput(e.Data + "\n");
        };

        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                _onOutput($"[stderr] {e.Data}\n");
        };

        _process.Exited += (_, _) =>
        {
            IsRunning = false;
            _onOutput($"[Process exited with code {_process.ExitCode}]\n");
        };

        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        IsRunning = true;

        _onOutput($"[Launched] {filePath} (PID {_process.Id})\n");
    }

    public void Stop()
    {
        if (!IsRunning || _process is null)
            return;

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(3000);
            }
        }
        catch { }
        finally
        {
            _process.Dispose();
            _process = null;
            IsRunning = false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
