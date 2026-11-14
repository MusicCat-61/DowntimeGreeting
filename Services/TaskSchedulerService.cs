using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using DowntimeGreeting.Localization;
using Serilog;

namespace DowntimeGreeting.Services;

/// <summary>Статус задачи в Планировщике Windows.</summary>
/// <param name="IsRegistered">Задача зарегистрирована.</param>
/// <param name="Message">Пояснение для пользователя.</param>
public sealed record ScheduledTaskStatus(bool IsRegistered, string Message);

/// <summary>Результат операции над задачей Планировщика Windows.</summary>
/// <param name="Success">Операция выполнена успешно.</param>
/// <param name="Message">Сообщение для пользователя.</param>
public sealed record TaskOperationResult(bool Success, string Message);

/// <summary>
/// Управляет задачей Планировщика Windows, которая запускает приложение при входе пользователя.
/// </summary>
/// <remarks>
/// Вместо COM-интерфейса Task Scheduler используется <c>schtasks.exe</c> с XML-манифестом задачи:
/// это позволяет задать все нужные параметры (запуск от имени текущего пользователя без
/// повышения прав, отсутствие условий «только от сети» и «остановить при переходе на батарею»).
/// Те же действия доступны через скрипты <c>Install-Task.ps1</c> и <c>Uninstall-Task.ps1</c>.
/// </remarks>
public sealed class TaskSchedulerService
{
    /// <summary>Имя задачи в Планировщике Windows по умолчанию.</summary>
    public const string DefaultTaskName = "DowntimeGreeting";

    /// <summary>Аргумент командной строки, с которым задача запускает приложение.</summary>
    public const string GreetArgument = "--greet";

    /// <summary>Задержка запуска после входа пользователя, чтобы рабочий стол успел появиться.</summary>
    private static readonly TimeSpan LogonDelay = TimeSpan.FromSeconds(2);

    /// <summary>Ограничение времени выполнения задачи.</summary>
    private static readonly TimeSpan ExecutionTimeLimit = TimeSpan.FromMinutes(5);

    private const string SchtasksExecutable = "schtasks.exe";

    private readonly string _taskName;

    /// <summary>
    /// Инициализирует новый экземпляр <see cref="TaskSchedulerService"/>.
    /// </summary>
    /// <param name="taskName">Имя задачи; по умолчанию <see cref="DefaultTaskName"/>.</param>
    public TaskSchedulerService(string taskName = DefaultTaskName)
    {
        _taskName = string.IsNullOrWhiteSpace(taskName) ? DefaultTaskName : taskName;
    }

    /// <summary>Имя используемой задачи.</summary>
    public string TaskName => _taskName;

    /// <summary>
    /// Проверяет, зарегистрирована ли задача в Планировщике Windows.
    /// </summary>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Статус задачи.</returns>
    public async Task<ScheduledTaskStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        (int exitCode, _) = await RunSchtasksAsync(
            string.Create(CultureInfo.InvariantCulture, $"/Query /TN \"{_taskName}\""),
            cancellationToken).ConfigureAwait(false);

        if (exitCode == 0)
        {
            return new ScheduledTaskStatus(true, Loc.F("L_StartupRegisteredFormat", _taskName));
        }

        return new ScheduledTaskStatus(false, Loc.T("L_StartupNotRegistered"));
    }

    /// <summary>
    /// Регистрирует (пересоздаёт) задачу автозапуска для указанного исполняемого файла.
    /// </summary>
    /// <param name="executablePath">Полный путь к <c>DowntimeGreeting.exe</c>.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Результат операции.</returns>
    /// <exception cref="ArgumentException">Путь пуст или файл не найден.</exception>
    public async Task<TaskOperationResult> RegisterAsync(string executablePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new ArgumentException("Путь к исполняемому файлу не задан.", nameof(executablePath));
        }

        string fullPath = Path.GetFullPath(executablePath);
        if (!File.Exists(fullPath))
        {
            return new TaskOperationResult(false, Loc.F("L_StartupFileMissingFormat", fullPath));
        }

        string xmlPath = Path.Combine(Path.GetTempPath(), $"DowntimeGreeting-Task-{Guid.NewGuid():N}.xml");
        try
        {
            string xml = BuildTaskXml(fullPath);
            // schtasks.exe принимает манифест задачи только в кодировке Unicode (UTF-16).
            File.WriteAllText(xmlPath, xml, Encoding.Unicode);

            (int exitCode, string output) = await RunSchtasksAsync(
                string.Create(CultureInfo.InvariantCulture, $"/Create /TN \"{_taskName}\" /XML \"{xmlPath}\" /F"),
                cancellationToken).ConfigureAwait(false);

            if (exitCode == 0)
            {
                Log.Information("Задача {TaskName} зарегистрирована для {Path}", _taskName, fullPath);
                return new TaskOperationResult(true, Loc.F("L_StartupAddDoneFormat", _taskName));
            }

            Log.Error("schtasks /Create завершился с кодом {ExitCode}. Вывод: {Output}", exitCode, output);
            return new TaskOperationResult(false, BuildFailureMessage(exitCode));
        }
        finally
        {
            TryDeleteFile(xmlPath);
        }
    }

    /// <summary>
    /// Удаляет задачу автозапуска из Планировщика Windows.
    /// </summary>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Результат операции.</returns>
    public async Task<TaskOperationResult> UnregisterAsync(CancellationToken cancellationToken = default)
    {
        (int exitCode, string output) = await RunSchtasksAsync(
            string.Create(CultureInfo.InvariantCulture, $"/Delete /TN \"{_taskName}\" /F"),
            cancellationToken).ConfigureAwait(false);

        if (exitCode == 0)
        {
            Log.Information("Задача {TaskName} удалена", _taskName);
            return new TaskOperationResult(true, Loc.F("L_StartupRemoveDoneFormat", _taskName));
        }

        Log.Warning("schtasks /Delete завершился с кодом {ExitCode}. Вывод: {Output}", exitCode, output);
        return new TaskOperationResult(false, BuildFailureMessage(exitCode));
    }

    /// <summary>
    /// Формирует XML-манифест задачи (формат Планировщика Windows, версия 1.2).
    /// </summary>
    /// <param name="executablePath">Полный путь к исполняемому файлу.</param>
    /// <returns>XML-манифест задачи.</returns>
    private string BuildTaskXml(string executablePath)
    {
        string userId = ResolveCurrentUserSid();
        string command = SecurityElement.Escape(executablePath) ?? executablePath;
        string workingDirectory = SecurityElement.Escape(Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory) ?? string.Empty;
        string description = SecurityElement.Escape("Приветственная плашка с длительностью простоя компьютера.") ?? string.Empty;
        int delaySeconds = (int)Math.Round(LogonDelay.TotalSeconds);
        int limitMinutes = (int)Math.Round(ExecutionTimeLimit.TotalMinutes);

        return $"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>{description}</Description>
    <Author>{SecurityElement.Escape(Environment.UserName)}</Author>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{SecurityElement.Escape(userId)}</UserId>
      <Delay>PT{delaySeconds}S</Delay>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>{SecurityElement.Escape(userId)}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT{limitMinutes}M</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>{command}</Command>
      <Arguments>{GreetArgument}</Arguments>
      <WorkingDirectory>{workingDirectory}</WorkingDirectory>
    </Exec>
  </Actions>
</Task>
""";
    }

    /// <summary>
    /// Возвращает SID текущего пользователя (используется в триггере и в принципале задачи).
    /// </summary>
    /// <returns>SID пользователя или строка <c>ДОМЕН\Пользователь</c>, если SID получить не удалось.</returns>
    private static string ResolveCurrentUserSid()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            string? sid = identity.User?.Value;
            if (!string.IsNullOrWhiteSpace(sid))
            {
                return sid;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or PlatformNotSupportedException)
        {
            Log.Warning(ex, "Не удалось получить SID пользователя, используется имя учётной записи");
        }

        return $"{Environment.UserDomainName}\\{Environment.UserName}";
    }

    /// <summary>
    /// Запускает <c>schtasks.exe</c> с указанными аргументами.
    /// </summary>
    /// <param name="arguments">Аргументы командной строки.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Код завершения и текст вывода (может быть искажён кодировкой консоли).</returns>
    private static async Task<(int ExitCode, string Output)> RunSchtasksAsync(string arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(SchtasksExecutable, arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Не удалось запустить schtasks.exe");

        Task<string> outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        string output = outputTask.Result;
        if (string.IsNullOrWhiteSpace(output))
        {
            output = errorTask.Result;
        }

        return (process.ExitCode, output);
    }

    /// <summary>
    /// Формирует понятное пользователю сообщение об ошибке по коду завершения <c>schtasks.exe</c>.
    /// </summary>
    /// <param name="exitCode">Код завершения.</param>
    /// <returns>Сообщение об ошибке.</returns>
    private static string BuildFailureMessage(int exitCode) => exitCode switch
    {
        1 => Loc.T("L_StartupAccessDenied"),
        2 => Loc.T("L_StartupTaskFileBroken"),
        8 => Loc.T("L_StartupAlreadyRunning"),
        _ => Loc.F("L_StartupGenericFailureFormat", exitCode)
    };

    /// <summary>
    /// Удаляет временный файл, игнорируя ошибки.
    /// </summary>
    /// <param name="path">Путь к файлу.</param>
    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug(ex, "Не удалось удалить временный файл {Path}", path);
        }
    }
}