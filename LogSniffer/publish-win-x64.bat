@echo off
setlocal
REM === LogSniffer Win x64 NativeAOT Publish ===
REM Usage:
REM   publish-win-x64.bat              default: Direct2D
REM   publish-win-x64.bat Direct2D
REM   publish-win-x64.bat Gdi
REM   publish-win-x64.bat MewVG

set BACKEND=%1
if "%BACKEND%"=="" set BACKEND=Direct2D
set PROJECT_DIR=%~dp0
if "%PROJECT_DIR:~-1%"=="\" set PROJECT_DIR=%PROJECT_DIR:~0,-1%
set PUBLISH_DIR=%PROJECT_DIR%\bin\Release\net10.0\win-x64\publish

set VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer
if exist "%VSWHERE%\vswhere.exe" set PATH=%VSWHERE%;%PATH%

echo.
echo === LogSniffer AOT Publish ===
echo Target:     win-x64
echo Config:     Release
echo Backend:    %BACKEND%
echo Output:     %PUBLISH_DIR%
echo.

echo [1/2] Restoring packages...
dotnet restore "%PROJECT_DIR%" -r win-x64
if %ERRORLEVEL% NEQ 0 (
    echo [FAIL] Restore failed
    exit /b %ERRORLEVEL%
)

echo.
echo [2/2] Publishing (AOT compile, may take several minutes)...
dotnet publish "%PROJECT_DIR%" -c Release -r win-x64 --self-contained true -p:PublishAot=true -p:TrimMode=full -p:MewUIBackend=%BACKEND% -p:InvariantGlobalization=true -p:DebugType=none -p:StripSymbols=true -p:IlcOptimizationPreference=Size

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo [FAIL] Publish failed. Try another backend: publish-win-x64.bat Gdi
    exit /b %ERRORLEVEL%
)

echo.
echo === [OK] Publish succeeded ===
echo Output: %PUBLISH_DIR%
echo Run:    LogSniffer.exe

endlocal
