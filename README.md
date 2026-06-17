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
dotnet publish -c Release -r win-x64
```

## Dependencies

| Package | Version |
|---|---|
| [Aprillz.MewUI](https://www.nuget.org/packages/Aprillz.MewUI/) | 0.15.2 |
| [Microsoft.Diagnostics.NETCore.Client](https://www.nuget.org/packages/Microsoft.Diagnostics.NETCore.Client/) | 0.2.532401 |
| [Microsoft.Diagnostics.Tracing.TraceEvent](https://www.nuget.org/packages/Microsoft.Diagnostics.Tracing.TraceEvent/) | 3.1.20 |

- **MewUI** — desktop UI framework (controls, theming, platform backends).
- **Diagnostics.NETCore.Client** — attaches to .NET Core processes via EventPipe.
- **TraceEvent** — ETW tracing for .NET Framework processes; also parses EventPipe streams.

## License

MIT — see [LICENSE.txt](LICENSE.txt).
