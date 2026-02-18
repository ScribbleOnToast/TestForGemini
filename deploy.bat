@echo off
setlocal enabledelayedexpansion

:: --- 1. System Check ---
set "USB_PATH=%~1"

if "%USB_PATH%"=="" (
    echo Error: Please provide the USB drive letter or path as an argument.
    echo Usage: %~nx0 E:
    exit /b 1
)

if not exist "%USB_PATH%\" (
    echo Error: USB path %USB_PATH% does not exist or is not writable.
    exit /b 1
)

echo --- Preparing Digital Eye Installer on %USB_PATH% ---

:: --- 2. Build and Publish ---
echo Cleaning old build...
dotnet clean
if %ERRORLEVEL% neq 0 (
    echo Error: dotnet clean failed.
    exit /b 1
)

echo Building and Publishing...
del ".\dist\publish"
dotnet publish ".\DigitalEye.slnx" -r linux-arm64 -c Release p:self-contained=true -p:PublishSingleFile=true -p:PublishReadyToRun=true -o ".\dist\publish"
if %ERRORLEVEL% neq 0 (
    echo Error: dotnet publish failed.
    exit /b 1
)
echo Build complete.

:: --- 3. Create USB Structure ---
echo Creating directory structure on USB...
if not exist "%USB_PATH%\apps" mkdir "%USB_PATH%\apps"
if not exist "%USB_PATH%\configs\systemd" mkdir "%USB_PATH%\configs\systemd"

:: --- 4. Deploy Files ---
echo Syncing application bits...
:: Robocopy /MIR is the equivalent of rsync -av --delete
robocopy ".\dist\publish" "%USB_PATH%\apps" /MIR /R:3 /W:5

echo Copying installer scripts and configs...
copy "deploy\*.sh" "%USB_PATH%\" /Y
copy "deploy\requirements.txt" "%USB_PATH%\configs\" /Y
copy "deploy\digitaleye.service" "%USB_PATH%\configs\systemd\" /Y
copy "model_assets\ollama-linux-arm64.tar.zst" "%USB_PATH%\" /Y

:: --- 5. Finalize ---
:: Windows handles 'sync' automatically on copy completion, 
:: but we'll flush the buffers just in case.
echo Finalizing...

echo --- DigitalEye Installer is ready! ---
echo You can now safely eject the drive.
pause