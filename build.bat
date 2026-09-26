@echo off
setlocal
set "DOTNET_CLI_UI_LANGUAGE=en-US"
set "VSLANG=1033"
set "ROOT=%~dp0"
set "DOTNET=dotnet"
if exist "%ROOT%.dotnet\dotnet.exe" set "DOTNET=%ROOT%.dotnet\dotnet.exe"
set "PACKAGE_ROOT=%ROOT%obj\portable-package"
set "APP_STAGE=%PACKAGE_ROOT%\app-%RANDOM%%RANDOM%"
set "LAUNCH_STAGE=%PACKAGE_ROOT%\launcher-%RANDOM%%RANDOM%"
set "ZIP=%PACKAGE_ROOT%\payload.zip"
for %%I in ("%ROOT%dist") do set "DIST=%%~fI"
for %%I in ("%DIST%\..") do set "DIST_PARENT=%%~fI"
for %%I in ("%ROOT%.") do set "ROOT_ABS=%%~fI"
if /I not "%DIST_PARENT%"=="%ROOT_ABS%" goto :fail

if not exist "%PACKAGE_ROOT%" mkdir "%PACKAGE_ROOT%"
if errorlevel 1 goto :fail

echo Publishing the self-contained Guard Center application...
"%DOTNET%" publish "%ROOT%src\Guard Center.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=None -o "%APP_STAGE%"
if errorlevel 1 goto :fail

echo Publishing the self-contained UAC Guard Host...
"%DOTNET%" publish "%ROOT%src\Tools\UacGuardHost\GuardCenter.UacGuardHost.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o "%APP_STAGE%"
if errorlevel 1 goto :fail
copy /y "%APP_STAGE%\Guard Center.dll" "%APP_STAGE%\GuardCenter.UacGuardHost.core.dll" >nul
if errorlevel 1 goto :fail
copy /y "%ROOT%src\Assets\Interception\NOTICE.md" "%APP_STAGE%\Assets\Interception\NOTICE.md" >nul
if errorlevel 1 goto :fail
if not exist "%APP_STAGE%\Guard Center.exe" goto :fail
if not exist "%APP_STAGE%\GuardCenter.UacGuardHost.exe" goto :fail
if not exist "%APP_STAGE%\Assets\Interception\interception-shim.dll" goto :fail

echo Packing the application into one executable...
if exist "%ZIP%" del /q "%ZIP%"
powershell -NoProfile -Command "$ErrorActionPreference='Stop'; Add-Type -AssemblyName System.IO.Compression.FileSystem; [IO.Compression.ZipFile]::CreateFromDirectory('%APP_STAGE%','%ZIP%',[IO.Compression.CompressionLevel]::Optimal,$false)"
if errorlevel 1 goto :fail

"%DOTNET%" publish "%ROOT%src\Tools\PortableLauncher\GuardCenter.PortableLauncher.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o "%LAUNCH_STAGE%"
if errorlevel 1 goto :fail
if not exist "%LAUNCH_STAGE%\Guard Center.exe" goto :fail

if exist "%DIST%\Shared\settings.ini" if not exist "%LOCALAPPDATA%\Guard Center\Portable\State\Shared\settings.ini" (
    if not exist "%LOCALAPPDATA%\Guard Center\Portable\State\Shared" mkdir "%LOCALAPPDATA%\Guard Center\Portable\State\Shared"
    copy /y "%DIST%\Shared\settings.ini" "%LOCALAPPDATA%\Guard Center\Portable\State\Shared\settings.ini" >nul
)
if exist "%DIST%" rmdir /s /q "%DIST%"
if exist "%DIST%" goto :fail
mkdir "%DIST%"
if errorlevel 1 goto :fail
copy /y "%LAUNCH_STAGE%\Guard Center.exe" "%DIST%\Guard Center.exe" >nul
if errorlevel 1 goto :fail
"%DIST%\Guard Center.exe" --verify-package
if errorlevel 1 goto :fail
copy /y "%DIST%\Guard Center.exe" "%ROOT%Guard Center.exe" >nul
if errorlevel 1 goto :fail

echo.
echo Done. The dist folder contains one executable:
echo "%DIST%\Guard Center.exe"
echo The same executable is also available at "%ROOT%Guard Center.exe".
echo On first launch, it extracts its runtime to LocalAppData automatically.
echo Press any key to close this window.
pause >nul
exit /b 0

:fail
echo Build failed. Do not distribute the dist folder.
echo Press any key to close this window.
pause >nul
exit /b 1
