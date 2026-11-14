@echo off
setlocal

rem ============================================================
rem  DowntimeGreeting - сборка единого exe-файла
rem  Требуется .NET SDK 10. Результат: publish\DowntimeGreeting.exe
rem  Рядом с exe никаких файлов не нужно: настройки и логи
rem  создаются в %LOCALAPPDATA%\DowntimeGreeting.
rem ============================================================

cd /d "%~dp0"

set PROJECT=DowntimeGreeting.csproj
set OUTDIR=publish
set RUNTIME=win-x64

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
for %%F in ("%OUTDIR%\*") do if /i not "%%~nxF"=="DowntimeGreeting.exe" del /f /q "%%F" >nul 2>nul
for /d %%D in ("%OUTDIR%\*") do rmdir /s /q "%%D" >nul 2>nul

echo.
echo === Готово ===
echo Файл:  %CD%\%OUTDIR%\DowntimeGreeting.exe
for %%F in ("%OUTDIR%\DowntimeGreeting.exe") do echo Размер: %%~zF байт
echo.
echo Настройки при первом запуске: %LOCALAPPDATA%\DowntimeGreeting\appsettings.json
echo Логи: %LOCALAPPDATA%\DowntimeGreeting\logs
echo.
echo Установка в автозагрузку: запустите exe и нажмите "Добавить в автозагрузку"
echo либо: "%OUTDIR%\DowntimeGreeting.exe" --install-task
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