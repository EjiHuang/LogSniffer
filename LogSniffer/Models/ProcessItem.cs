namespace LogSniffer.Models;

public class ProcessItem
{
    /// <summary>
    /// 进程 ID。
    /// </summary>
    public int Pid { get; set; }

    /// <summary>
    /// 进程名称。
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 运行时类型：
    /// CoreCLR  — .NET Core / .NET 5+（通过 EventPipe 诊断）
    /// Framework — .NET Framework CLR（通过 ETW 诊断）
    /// </summary>
    public ProcessRuntime Runtime { get; set; }
}

/// <summary>
/// 进程的 .NET 运行时类型。
/// </summary>
public enum ProcessRuntime
{
    /// <summary>
    /// .NET Core / .NET 5+ (CoreCLR)。
    /// </summary>
    CoreCLR,

    /// <summary>
    /// .NET Framework (CLR)。
    /// </summary>
    Framework,
}
