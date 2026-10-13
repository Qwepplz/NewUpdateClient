@echo off
setlocal
cd /d "%~dp0"

for %%I in ("%~dp0..") do set "ROOT_DIR=%%~fI"
set "LOCAL_ROOT=%ROOT_DIR%\local"
set "DOTNET_CLI_HOME=%LOCAL_ROOT%\dotnet-home"
set "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=0"
set "DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=1"
set "DOTNET_NOLOGO=1"
set "PROJECT_FILE=%ROOT_DIR%\src\UpdateClient\UpdateClient.csproj"
set "OUT_DIR=%LOCAL_ROOT%\dist"
set "PUBLISH_ROOT=%LOCAL_ROOT%\publish\UpdateClient"
set "LINUX_OUT_DIR=%OUT_DIR%\linux-x64"
set "OUT_FILE=%OUT_DIR%\UpdateClient.exe"
set "NO_PAUSE=%BUILD_NO_PAUSE%"
set "EXIT_CODE=0"

:parse_args
if "%~1"=="" goto args_done
if /i "%~1"=="--help" goto usage
if /i "%~1"=="/?" goto usage
if /i "%~1"=="--no-pause" (
    set "NO_PAUSE=1"
    shift
    goto parse_args
)
echo Unknown argument: %~1
set "EXIT_CODE=1"
goto finish

:args_done
where dotnet.exe >nul 2>nul
if errorlevel 1 (
    echo dotnet.exe was not found. Install .NET SDK and retry.
    set "EXIT_CODE=1"
    goto finish
)

if not exist "%PROJECT_FILE%" (
    echo Project file was not found:
    echo %PROJECT_FILE%
    set "EXIT_CODE=1"
    goto finish
)

echo Publishing UpdateClient for Windows and Linux...
if not exist "%OUT_DIR%" mkdir "%OUT_DIR%"
if not exist "%LINUX_OUT_DIR%" mkdir "%LINUX_OUT_DIR%"
call :publish win-x64 "%PUBLISH_ROOT%\win-x64" "%OUT_FILE%"
if not "%EXIT_CODE%"=="0" goto finish
call :publish linux-x64 "%PUBLISH_ROOT%\linux-x64" "%LINUX_OUT_DIR%\UpdateClient"
if not "%EXIT_CODE%"=="0" goto finish
echo Build completed: %OUT_FILE%
echo Build completed: %LINUX_OUT_DIR%\UpdateClient
goto finish

:publish
dotnet publish "%PROJECT_FILE%" -c Release -r "%~1" --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=embedded -p:PublishTrimmed=false -p:PublishAot=false -o "%~2" --nologo
set "EXIT_CODE=%ERRORLEVEL%"
if not "%EXIT_CODE%"=="0" exit /b %EXIT_CODE%
set "PUBLISH_BIN=%~2\UpdateClient"
if /i "%~1"=="win-x64" set "PUBLISH_BIN=%PUBLISH_BIN%.exe"
if not exist "%PUBLISH_BIN%" (
    echo Published executable was not found.
    set "EXIT_CODE=1"
    exit /b 1
)
copy /Y "%PUBLISH_BIN%" "%~3" >nul
if errorlevel 1 (
    echo Cannot copy published executable to dist.
    set "EXIT_CODE=1"
    exit /b 1
)
exit /b 0

:usage
echo Usage:
echo   Build-UpdateClient.bat [--no-pause]
echo.
set "EXIT_CODE=0"
goto finish

:finish
echo.
if not defined NO_PAUSE pause
exit /b %EXIT_CODE%
