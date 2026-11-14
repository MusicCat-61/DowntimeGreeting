@echo off
setlocal

rem ============================================================
rem  DowntimeGreeting - сборка единого exe-файла
rem  Требуется .NET SDK 10. Результат: publish\DowntimeGreeting.exe
rem  Рядом с exe никаких файлов не нужно: настройки и логи
rem  создаются в %LOCALAPPDATA%\DowntimeGreeting.
rem  Дополнительно создаётся файл контрольной суммы для релиза.
rem ============================================================

cd /d "%~dp0"

set PROJECT=DowntimeGreeting.csproj
set OUTDIR=publish
set RUNTIME=win-x64
set EXENAME=DowntimeGreeting.exe

echo === DowntimeGreeting: публикация ===
echo.

where dotnet >nul 2>nul
if errorlevel 1 goto nodotnet

echo Очистка каталога %OUTDIR%...
if exist "%OUTDIR%" rmdir /s /q "%OUTDIR%"

echo Сборка единого exe: %RUNTIME%, self-contained
echo.

dotnet publish "%PROJECT%" -c Release -r %RUNTIME% --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o "%OUTDIR%"

if errorlevel 1 goto failed

rem Удаляем всё, кроме самого exe: публикуется ровно один файл.
for %%F in ("%OUTDIR%\*") do if /i not "%%~nxF"=="%EXENAME%" del /f /q "%%F" >nul 2>nul
for /d %%D in ("%OUTDIR%\*") do rmdir /s /q "%%D" >nul 2>nul

rem Контрольная сумма для релиза: приложение сверяет её перед установкой.
echo Создание контрольной суммы...
powershell -NoProfile -ExecutionPolicy Bypass -Command "$h=(Get-FileHash -Algorithm SHA256 '%OUTDIR%\%EXENAME%').Hash.ToLower(); $n='%EXENAME%'; Set-Content -Path ('%OUTDIR%\'+$n+'.sha256') -Value ($h+'  '+$n) -Encoding ASCII -NoNewline"

if errorlevel 1 echo [ВНИМАНИЕ] Не удалось создать файл контрольной суммы.

echo.
echo === Готово ===
echo Файл:  %CD%\%OUTDIR%\%EXENAME%
for %%F in ("%OUTDIR%\%EXENAME%") do echo Размер: %%~zF байт
echo Сумма: %CD%\%OUTDIR%\%EXENAME%.sha256
echo.
echo Настройки при первом запуске: %LOCALAPPDATA%\DowntimeGreeting\appsettings.json
echo Логи: %LOCALAPPDATA%\DowntimeGreeting\logs
echo.
echo Установка в автозагрузку: запустите exe и нажмите "Добавить в автозагрузку"
echo либо: "%OUTDIR%\%EXENAME%" --install-task
echo.
echo Для релиза на GitHub приложите оба файла:
echo   %EXENAME%
echo   %EXENAME%.sha256
echo.
pause
exit /b 0

:nodotnet
echo [ОШИБКА] Не найден dotnet. Установите .NET SDK 10 с сайта dotnet.microsoft.com/download
pause
exit /b 1

:failed
echo.
echo [ОШИБКА] Публикация не удалась.
pause
exit /b 1