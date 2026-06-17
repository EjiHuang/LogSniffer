using LogSniffer.Models;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace LogSniffer.Helpers;

/// <summary>
/// 进程帮助类。参考 dnSpy 的实现：
/// - .NET Framework 进程通过 COM ICLRMetaHost::EnumerateLoadedRuntimes 检测
/// - .NET Core 进程通过 \\.\pipe\dotnet-diagnostic-{pid} 命名管道检测
/// </summary>
[SupportedOSPlatform("windows")]
public static class ProcessHelper
{
    // ════════════════════════════════════════════════════════════
    // .NET Core 检测：诊断管道
    // ════════════════════════════════════════════════════════════

    private const string DiagnosticPipePrefix = "dotnet-diagnostic-";

    private static HashSet<int> GetCoreClrProcessIds()
    {
        var pidSet = new HashSet<int>();
        try
        {
            foreach (var pipePath in Directory.GetFiles(@"\\.\pipe\", DiagnosticPipePrefix + "*"))
            {
                var name = Path.GetFileName(pipePath.AsSpan());
                var pidStr = name[DiagnosticPipePrefix.Length..];
                if (int.TryParse(pidStr, out int pid) && pid > 0)
                    pidSet.Add(pid);
            }
        }
        catch
        {
            // 枚举失败则跳过
        }
        return pidSet;
    }

    // ════════════════════════════════════════════════════════════
    // .NET Framework 检测：COM ICLRMetaHost（dnSpy 的做法）
    // ════════════════════════════════════════════════════════════

    // CLSID_CLRMetaHost  : {9280188D-0E8E-4867-B30C-7FA83884E8DE}
    // IID_ICLRMetaHost   : {D332DB9E-B9B3-4125-8207-A14884F53216}
    private static readonly Guid ClsidClrMetaHost = new(0x9280188D, 0x0E8E, 0x4867, 0xB3, 0x0C, 0x7F, 0xA8, 0x38, 0x84, 0xE8, 0xDE);
    private static readonly Guid IidIClrMetaHost  = new(0xD332DB9E, 0xB9B3, 0x4125, 0x82, 0x07, 0xA1, 0x48, 0x84, 0xF5, 0x32, 0x16);

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("D332DB9E-B9B3-4125-8207-A14884F53216")]
    private interface ICLRMetaHost
    {
        [PreserveSig]
        int GetRuntime([MarshalAs(UnmanagedType.LPWStr)] string pwzVersion, [In] ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppRuntime);

        [PreserveSig]
        int GetVersionFromFile([MarshalAs(UnmanagedType.LPWStr)] string pwzFilePath, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder pwzBuffer, ref uint pcchBuffer);

        [PreserveSig]
        int EnumerateInstalledRuntimes([MarshalAs(UnmanagedType.Interface)] out object ppEnumerator);

        [PreserveSig]
        int EnumerateLoadedRuntimes(IntPtr hndProcess, [MarshalAs(UnmanagedType.Interface)] out object ppEnumerator);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("00000100-0000-0000-C000-000000000046")]
    private interface IEnumUnknown
    {
        [PreserveSig]
        int Next(uint celt, [Out, MarshalAs(UnmanagedType.IUnknown)] out object? rgelt, out uint pceltFetched);

        [PreserveSig]
        int Skip(uint celt);

        [PreserveSig]
        int Reset();

        [PreserveSig]
        int Clone([MarshalAs(UnmanagedType.Interface)] out IEnumUnknown ppenum);
    }

    [DllImport("mscoree.dll")]
    private static extern int CLRCreateInstance([In] ref Guid clsid, [In] ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppInterface);

    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint PROCESS_VM_READ = 0x0010;
    private const uint PROCESS_DUP_HANDLE = 0x0040;
    private const uint SYNCHRONIZE = 0x00100000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    /// <summary>
    /// 使用 ICLRMetaHost 检测 .NET Framework CLR。
    /// 在 NativeAOT 下自动跳过（COM Interop 不可用）。
    /// </summary>
    private static HashSet<int> GetFrameworkClrProcessIds()
    {
        var pidSet = new HashSet<int>();

        // NativeAOT 下 COM Interop 不可用，直接返回空集合
        if (!RuntimeFeature.IsDynamicCodeSupported)
            return pidSet;

        // 创建 ICLRMetaHost 实例
        var clsid = ClsidClrMetaHost;
        var iid = IidIClrMetaHost;
        int hr = CLRCreateInstance(ref clsid, ref iid, out object obj);
        if (hr < 0)
            return pidSet;

        var metaHost = (ICLRMetaHost)obj;

        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                IntPtr hProcess = OpenProcess(
                    PROCESS_QUERY_INFORMATION | PROCESS_VM_READ | PROCESS_DUP_HANDLE | SYNCHRONIZE,
                    false, (uint)proc.Id);

                if (hProcess == IntPtr.Zero)
                    continue;

                try
                {
                    hr = metaHost.EnumerateLoadedRuntimes(hProcess, out object enumObj);
                    if (hr < 0)
                        continue;

                    var enumerator = (IEnumUnknown)enumObj;
                    if (enumerator.Next(1, out _, out uint fetched) == 0 && fetched > 0)
                        pidSet.Add(proc.Id);
                }
                finally
                {
                    CloseHandle(hProcess);
                }
            }
            catch
            {
                // 进程无法访问
            }
            finally
            {
                proc.Dispose();
            }
        }

        return pidSet;
    }

    // ════════════════════════════════════════════════════════════
    // 公共 API
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 获取当前运行的 .NET 进程列表（同时覆盖 .NET Framework 和 .NET Core）
    /// </summary>
    public static List<ProcessItem> GetDotNetProcesses()
    {
        var coreClrPids = GetCoreClrProcessIds();
        var frameworkPids = GetFrameworkClrProcessIds();

        // 合并 Framework CLR 和 CoreCLR 的 PID
        var allPids = new HashSet<int>(frameworkPids);
        allPids.UnionWith(coreClrPids);

        var processes = new List<ProcessItem>();
        foreach (var pid in allPids)
        {
            try
            {
                using var proc = Process.GetProcessById(pid);
                processes.Add(new ProcessItem
                {
                    Pid = proc.Id,
                    Name = proc.ProcessName,
                    Runtime = coreClrPids.Contains(pid)
                        ? ProcessRuntime.CoreCLR
                        : ProcessRuntime.Framework
                });
            }
            catch
            {
                // 进程可能已退出
            }
        }

        return processes;
    }
}
