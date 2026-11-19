@echo off
rem Double-click to open the RE4 EFX preview window.
rem Builds VfxStudio.Re4Preview (Release) first when the source changed; if the build fails
rem (for example another preview window still locks the dlls) the existing build is started.
rem Extra arguments are passed through, e.g. RE4Preview.cmd --efx <file> --offset <hex>.
setlocal
cd /d "%~dp0"
set "EXE=%~dp0VfxStudio.Re4Preview\bin\Release\net8.0\VfxStudio.Re4Preview.exe"

echo Building VfxStudio.Re4Preview (Release)...
dotnet build VfxStudio.Re4Preview -c Release -nologo -v q
if errorlevel 1 (
    if exist "%EXE%" (
        echo Build failed; starting the existing build.
        timeout /t 3 >nul
    ) else (
        echo Build failed and no previous build exists.
        pause
        exit /b 1
    )
)

rem The preview is a console program: its log window opens minimized next to the preview window.
start "RE4 Preview log" /min "%EXE%" --repo "%~dp0." %*
endlocal
