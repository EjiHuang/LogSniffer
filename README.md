# LogSniffer

A Windows desktop tool for viewing .NET runtime diagnostics, process output, and log files in real time.

## Features

- **Attach to .NET processes** — stream runtime events (exceptions, GC, `ILogger`/`EventSource` traces) from both .NET Core and .NET Framework processes.
- **Launch & capture** — run an executable and see its stdout/stderr live.
- **Tail log files** — open a `.log` or `.txt` file and watch new lines appear in real time.
- **Auto-scroll** — stays at the bottom as new content arrives; pauses when you scroll up.

## Requirements

- Windows 10+ (x64)
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Run as Administrator (required for ETW sessions)

## Build

```bash
# Debug
dotnet build

# Release
dotnet build -c Release

# Standalone executable (NativeAOT)
.\publish-win-x64.bat           # default: Direct2D backend
.\publish-win-x64.bat Gdi       # GDI backend
.\publish-win-x64.bat MewVG     # MewVG backend
```

## NativeAOT limitations

When published with NativeAOT, the following features are **not available**:

- **Attach to .NET Framework processes (ETW)** — explicitly unsupported. The ETW session path requires `System.Reflection.Emit` and COM interop, which are incompatible with NativeAOT. A `NotSupportedException` is thrown if you attempt to attach to a .NET Framework process.
- **Attach to .NET Core processes (EventPipe)** — may have reduced functionality. The underlying `TraceEvent` library relies on dynamic code generation for event parsing; some event types may produce incomplete or missing output.

These features work normally in both Debug and Release (non-AOT) builds. **Launch & capture** and **Tail log files** are unaffected and work correctly under NativeAOT.

## Dependencies

| Package | Version |
|---|---|
| [Aprillz.MewUI](https://github.com/aprillz/MewUI) | 0.15.2 |
| [Microsoft.Diagnostics.NETCore.Client](https://www.nuget.org/packages/Microsoft.Diagnostics.NETCore.Client/) | 0.2.532401 |
| [Microsoft.Diagnostics.Tracing.TraceEvent](https://www.nuget.org/packages/Microsoft.Diagnostics.Tracing.TraceEvent/) | 3.1.20 |

- **MewUI** — desktop UI framework (controls, theming, platform backends).
- **Diagnostics.NETCore.Client** — attaches to .NET Core processes via EventPipe.
- **TraceEvent** — ETW tracing for .NET Framework processes; also parses EventPipe streams.

## License

MIT — see [LICENSE.txt](LICENSE.txt).
