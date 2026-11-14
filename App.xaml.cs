using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using DowntimeGreeting.Common;
using DowntimeGreeting.Localization;
using DowntimeGreeting.Models;
using DowntimeGreeting.Services;
using DowntimeGreeting.Updates;
using Serilog;

namespace DowntimeGreeting;

/// <summary>
/// Точка входа приложения: настраивает логирование, определяет режим запуска,
/// обеспечивает единственный экземпляр и показывает нужное окно.
/// </summary>
/// <remarks>
/// Режимы запуска:
/// <list type="bullet">
/// <item>без аргументов — главное окно управления (настройки, автозагрузка, предпросмотр);</item>
/// <item><c>--greet</c> — только плашка с последующим выходом (используется задачей Планировщика);</item>
/// <item><c>--install-task</c> / <c>--uninstall-task</c> — регистрация или удаление задачи автозапуска без окна.</item>
/// </list>
/// </remarks>
public partial class App : Application
{
    /// <summary>Аргумент запуска: показать плашку и выйти.</summary>
    public const string GreetArgument = TaskSchedulerService.GreetArgument;

    /// <summary>Аргумент запуска: зарегистрировать задачу автозапуска.</summary>
    public const string InstallTaskArgument = "--install-task";

    /// <summary>Аргумент запуска: удалить задачу автозапуска.</summary>
    public const string UninstallTaskArgument = "--uninstall-task";

    /// <summary>Аргумент запуска: отрисовать превью окон в PNG без показа на экране.</summary>
    public const string RenderPreviewArgument = "--render-preview";

    /// <summary>Аргумент запуска: сгенерировать иконку приложения (<c>.ico</c>).</summary>
    public const string MakeIconArgument = "--make-icon";

    /// <summary>Имя мьютекса, обеспечивающего единственный экземпляр приложения.</summary>
    private const string SingleInstanceMutexName = @"Local\DowntimeGreeting.SingleInstance";

    /// <summary>Имя события «показать плашку» для передачи запроса первому экземпляру.</summary>
    private const string ShowGreetingEventName = @"Local\DowntimeGreeting.ShowGreeting";

    /// <summary>Имя события «активировать главное окно» для передачи запроса первому экземпляру.</summary>
    private const string ActivateWindowEventName = @"Local\DowntimeGreeting.ActivateWindow";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showGreetingEvent;
    private EventWaitHandle? _activateWindowEvent;
    private GreetingWindow? _greetingWindow;
    private string? _lastPhrase;
    private bool _isGreetMode;
    private readonly CancellationTokenSource _updateCancellation = new();

    /// <summary>Текущая конфигурация приложения.</summary>
    public AppConfig Config { get; private set; } = new();

    /// <summary>
    /// Возвращает путь к каталогу журналов приложения.
    /// </summary>
    /// <returns>Каталог <c>%LOCALAPPDATA%\DowntimeGreeting\logs</c>.</returns>
    public static string GetLogDirectory() => AppPaths.GetLogDirectory();

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AttachGlobalExceptionHandlers();

        Config = AppConfig.Load();
        Loc.Initialize(Config.Language);
        InitializeLogging(Config);

        Log.Information(
            "Запуск DowntimeGreeting. Аргументы: [{Arguments}], язык: {Language}, конфигурация: {Config}",
            string.Join(' ', e.Args),
            Loc.CurrentCode,
            Config);

        if (TryHandleCommandLine(e.Args, out int exitCode))
        {
            Shutdown(exitCode);
            return;
        }

        if (e.Args.Any(arg => arg.Equals(RenderPreviewArgument, StringComparison.OrdinalIgnoreCase)))
        {
            int previewExitCode = RenderPreviews(e.Args);
            Shutdown(previewExitCode);
            return;
        }

        if (e.Args.Any(arg => arg.Equals(MakeIconArgument, StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                string iconPath = e.Args.Length > 1 && !e.Args[1].StartsWith('-')
                    ? Path.GetFullPath(e.Args[1])
                    : Path.Combine(AppContext.BaseDirectory, "app.ico");

                IconGenerator.CreateIcon(iconPath);
                Shutdown(0);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Не удалось создать иконку");
                Shutdown(1);
            }

            return;
        }

        _isGreetMode = e.Args.Any(arg => arg.Equals(GreetArgument, StringComparison.OrdinalIgnoreCase));

        if (!AcquireSingleInstance())
        {
            Shutdown(0);
            return;
        }

        ThemePalette palette = ThemeService.CreatePalette(Config.Accent);
        ThemeService.ApplyPalette(Resources, palette);

        StartInstanceListener();

        // Отчёт предыдущей попытки установки читается до показа окон: если скрипт подмены
        // не справился, пользователь узнает об этом, а не будет искать причину сам.
        ReportPreviousUpdate();

        if (_isGreetMode)
        {
            _ = ShowGreetingAsync();
        }
        else
        {
            var window = new MainWindow(Config);
            MainWindow = window;
            window.Show();

            // Проверка обновлений — только в главном окне и только в фоне: плашка при входе
            // в систему не должна ждать сеть.
            _ = RunStartupUpdateCheckAsync();
        }
    }

    /// <summary>
    /// Читает отчёт скрипта подмены exe от предыдущего запуска и сообщает результат.
    /// </summary>
    private void ReportPreviousUpdate()
    {
        UpdateApplyReport? report = UpdateService.ConsumeLastApplyReport();

        if (report is null)
        {
            return;
        }

        // Успешная установка уже видна по номеру версии в окне «О программе», поэтому
        // сообщаем только о неудаче: молчаливый сбой выглядит как «обновление не пришло».
        if (!report.Succeeded)
        {
            Log.Warning("Предыдущая установка обновления не удалась: {Detail}", report.Detail);
        }
    }

    /// <summary>
    /// Проверяет обновления при каждом запуске приложения.
    /// </summary>
    /// <remarks>
    /// Проверка выполняется при каждом старте: пользователь должен узнавать о новой версии
    /// сразу, а не через сутки. Ошибки сети не показываются — они попадают в журнал,
    /// а работа приложения от них не зависит.
    /// </remarks>
    /// <returns>Задача, представляющая асинхронную операцию.</returns>
    private async Task RunStartupUpdateCheckAsync()
    {
        try
        {
            // Небольшая задержка, чтобы окно успело отрисоваться до сетевого запроса.
            await Task.Delay(TimeSpan.FromSeconds(3), _updateCancellation.Token).ConfigureAwait(true);

            UpdateCheckResult result = await UpdateService
                .CheckAsync(_updateCancellation.Token)
                .ConfigureAwait(true);

            if (!result.HasUpdate || result.Latest is null)
            {
                return;
            }

            Log.Information("Доступно обновление {Version}", result.Latest);

            if (MainWindow is MainWindow mainWindow)
            {
                mainWindow.ShowUpdateAvailable(result);
            }
        }
        catch (OperationCanceledException)
        {
            // Приложение закрывается.
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Фоновая проверка обновлений не удалась");
        }
    }

    /// <summary>
    /// Показывает приветственную плашку, предварительно рассчитав время простоя.
    /// </summary>
    /// <returns>Задача, представляющая асинхронную операцию.</returns>
    public async Task ShowGreetingAsync()
    {
        if (_greetingWindow is not null && !_greetingWindow.IsClosing)
        {
            Log.Debug("Плашка уже отображается — повторный показ не требуется");
            return;
        }

        try
        {
            var eventLogService = new EventLogService();
            DowntimeInfo info = await eventLogService.GetDowntimeInfoAsync();

            var phraseService = new PhraseService(Config.Phrases, Config.PhrasesEn);
            string phrase = phraseService.GetNextPhrase(_lastPhrase);
            _lastPhrase = phrase;

            var window = new GreetingWindow(Config, info, phrase);
            window.Closed += OnGreetingWindowClosed;
            _greetingWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Не удалось показать приветственную плашку");
            if (MainWindow is null)
            {
                Shutdown(1);
            }
        }
    }

    /// <summary>
    /// Применяет новую конфигурацию и обновляет палитру темы в работающих окнах.
    /// </summary>
    /// <param name="config">Новая конфигурация.</param>
    /// <exception cref="ArgumentNullException">Конфигурация равна <see langword="null"/>.</exception>
    public void ApplyConfig(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        Config = config;
        ThemePalette palette = ThemeService.CreatePalette(config.Accent);
        ThemeService.ApplyPalette(Resources, palette);
    }

    /// <summary>
    /// Освобождает ресурсы синхронизации и закрывает журнал при завершении приложения.
    /// </summary>
    /// <param name="e">Аргументы события выхода.</param>
    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("Завершение работы, код выхода {ExitCode}", e.ApplicationExitCode);

        _updateCancellation.Cancel();
        _updateCancellation.Dispose();

        _showGreetingEvent?.Dispose();
        _activateWindowEvent?.Dispose();
        _showGreetingEvent = null;
        _activateWindowEvent = null;

        if (_instanceMutex is not null)
        {
            try
            {
                _instanceMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Мьютекс уже освобождён — ничего страшного.
            }

            _instanceMutex.Dispose();
            _instanceMutex = null;
        }

        Log.CloseAndFlush();
        base.OnExit(e);
    }

    /// <summary>
    /// Отрисовывает превью плашки и главного окна в PNG-файлы, не показывая окна на экране.
    /// </summary>
    /// <param name="args">Аргументы командной строки; второй аргумент — каталог для файлов.</param>
    /// <returns>Код выхода: 0 — успех, 1 — ошибка.</returns>
    private int RenderPreviews(string[] args)
    {
        string outputDirectory = args.Length > 1 && !args[1].StartsWith('-')
            ? Path.GetFullPath(args[1])
            : Path.Combine(AppContext.BaseDirectory, "docs");

        try
        {
            Directory.CreateDirectory(outputDirectory);

            // Окна закрываются по очереди, поэтому автозавершение приложения при закрытии
            // последнего окна нужно отключить на время рендера.
            ShutdownMode previousShutdownMode = ShutdownMode;
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            try
            {
                // Демонстрационные данные: сутки с лишним простоя и штатное выключение.
                var shutdown = new DateTime(2026, 11, 12, 23, 14, 0, DateTimeKind.Local);
                var demoInfo = new DowntimeInfo
                {
                    BootTime = shutdown + TimeSpan.FromDays(1) + TimeSpan.FromHours(14) + TimeSpan.FromMinutes(23),
                    LastShutdownTime = shutdown,
                    Downtime = TimeSpan.FromDays(1) + TimeSpan.FromHours(14) + TimeSpan.FromMinutes(23),
                    Source = DowntimeSource.EventLogShutdown,
                    IsApproximate = false
                };

                // Случай перезагрузки: подпись «Компьютер перезагрузился».
                var restartInfo = new DowntimeInfo
                {
                    BootTime = shutdown + TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1),
                    LastShutdownTime = shutdown,
                    Downtime = TimeSpan.FromMinutes(1),
                    Source = DowntimeSource.EventLogShutdown,
                    IsApproximate = false,
                    IsRestart = true
                };

                foreach (AppLanguage language in new[] { AppLanguage.Ru, AppLanguage.En })
                {
                    string suffix = language == AppLanguage.Ru ? string.Empty : "-en";
                    IReadOnlyList<string> phrases = language == AppLanguage.Ru ? Config.Phrases : Config.PhrasesEn;
                    string phrase = phrases[0];

                    RenderPreviewFile(
                        $"plaque-dark{suffix}.png",
                        outputDirectory,
                        isLight: false,
                        language,
                        () => new GreetingWindow(Config, demoInfo, phrase, previewMode: true));

                    RenderPreviewFile(
                        $"plaque-light{suffix}.png",
                        outputDirectory,
                        isLight: true,
                        language,
                        () => new GreetingWindow(Config, demoInfo, phrase, previewMode: true));

                    RenderPreviewFile(
                        $"plaque-restart{suffix}.png",
                        outputDirectory,
                        isLight: false,
                        language,
                        () => new GreetingWindow(Config, restartInfo, phrase, previewMode: true));

                    RenderPreviewFile(
                        $"main-window{suffix}.png",
                        outputDirectory,
                        isLight: false,
                        language,
                        () => new MainWindow(Config, previewMode: true));

                    RenderPreviewFile(
                        $"about{suffix}.png",
                        outputDirectory,
                        isLight: false,
                        language,
                        () => new AboutWindow(previewMode: true));

                    // Окно обновления: показываем предложение установить новую версию.
                    RenderPreviewFile(
                        $"update{suffix}.png",
                        outputDirectory,
                        isLight: false,
                        language,
                        () => new UpdateWindow(DemoUpdateResult, previewMode: true));
                }
            }
            finally
            {
                ShutdownMode = previousShutdownMode;
                Loc.SetLanguage(Loc.Parse(Config.Language), notify: false);
            }

            Log.Information("Превью отрисованы в {Directory}", outputDirectory);
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Не удалось отрисовать превью");
            return 1;
        }
    }

    /// <summary>
    /// Демонстрационный результат проверки обновлений для отрисовки превью.
    /// </summary>
    private static UpdateCheckResult DemoUpdateResult
    {
        get
        {
            ReleaseVersion latest = ReleaseVersion.TryParse("1.4.0", out ReleaseVersion parsed)
                ? parsed
                : default;

            return new UpdateCheckResult
            {
                Status = UpdateStatus.UpdateAvailable,
                Current = UpdateService.CurrentVersion,
                Latest = latest,
                Tag = "v1.4.0",
                ReleaseUrl = $"{AppInfo.RepositoryUrl}/releases/tag/v1.4.0",
                AssetSize = 61_790_571,
                Notes = """
                    # Русский

                    ### Что нового

                    - Переключатель языка RU / EN прямо в заголовке окна
                    - Фразы плашки задаются отдельно для каждого языка
                    - Акцентный цвет: колесо и ползунок яркости

                    ### Исправлено

                    - Кнопки автозагрузки больше не срабатывают повторно
                    - Белый акцентный цвет больше не скрывает текст кнопок

                    # English

                    ### What is new

                    - RU / EN language switch right in the title bar
                    - Plaque phrases are configured per language
                    - Accent color: wheel and brightness slider

                    ### Fixed

                    - Autostart buttons no longer fire repeatedly
                    - A white accent no longer hides the button labels
                    """
            };
        }
    }

    /// <summary>
    /// Отрисовывает одно окно в PNG с заданной темой и языком.
    /// </summary>
    /// <param name="fileName">Имя файла.</param>
    /// <param name="outputDirectory">Каталог для сохранения.</param>
    /// <param name="isLight">Использовать светлую палитру.</param>
    /// <param name="language">Язык интерфейса для этого кадра.</param>
    /// <param name="windowFactory">Фабрика окна.</param>
    private void RenderPreviewFile(
        string fileName,
        string outputDirectory,
        bool isLight,
        AppLanguage language,
        Func<Window> windowFactory)
    {
        Environment.SetEnvironmentVariable("BOOTGREETING_THEME", isLight ? "light" : "dark");
        ThemeService.ApplyPalette(Resources, ThemeService.CreatePalette(Config.Accent));
        Loc.SetLanguage(language, notify: false);

        Window window = windowFactory();
        try
        {
            window.Show();

            // Даём диспетчеру доработать разметку до отрисовки.
            Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
            window.UpdateLayout();

            // Подложка имитирует рабочий стол за полупрозрачной плашкой:
            // тёмная тема — тёмные обои, светлая — светлые.
            Color backdrop = isLight
                ? Color.FromRgb(0xE8, 0xEC, 0xF2)
                : Color.FromRgb(0x2B, 0x31, 0x3D);

            PreviewRenderer.RenderWindow(window, Path.Combine(outputDirectory, fileName), backdrop: backdrop);
        }
        finally
        {
            window.Close();
            Environment.SetEnvironmentVariable("BOOTGREETING_THEME", null);
        }
    }

    /// <summary>
    /// Обрабатывает служебные аргументы командной строки (установка/удаление задачи).
    /// </summary>
    /// <param name="args">Аргументы командной строки.</param>
    /// <param name="exitCode">Код выхода приложения.</param>
    /// <returns><see langword="true"/>, если приложение завершило работу в CLI-режиме.</returns>
    private static bool TryHandleCommandLine(string[] args, out int exitCode)
    {
        exitCode = 0;

        bool install = args.Any(arg => arg.Equals(InstallTaskArgument, StringComparison.OrdinalIgnoreCase));
        bool uninstall = args.Any(arg => arg.Equals(UninstallTaskArgument, StringComparison.OrdinalIgnoreCase));

        if (!install && !uninstall)
        {
            return false;
        }

        var service = new TaskSchedulerService();
        try
        {
            TaskOperationResult result = install
                ? Task.Run(() => service.RegisterAsync(ResolveExecutablePath())).GetAwaiter().GetResult()
                : Task.Run(() => service.UnregisterAsync()).GetAwaiter().GetResult();

            Log.Information("CLI-операция с задачей «{TaskName}»: {Message}", service.TaskName, result.Message);
            exitCode = result.Success ? 0 : 1;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Ошибка выполнения CLI-операции с задачей Планировщика");
            exitCode = 1;
        }

        Log.CloseAndFlush();
        return true;
    }

    /// <summary>
    /// Определяет путь к исполняемому файлу приложения для регистрации задачи.
    /// </summary>
    /// <returns>Полный путь к <c>DowntimeGreeting.exe</c>.</returns>
    private static string ResolveExecutablePath()
    {
        string? processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath)
            && Path.GetFileName(processPath).Equals("DowntimeGreeting.exe", StringComparison.OrdinalIgnoreCase))
        {
            return processPath;
        }

        string candidate = Path.Combine(AppContext.BaseDirectory, "DowntimeGreeting.exe");
        return File.Exists(candidate) ? candidate : processPath ?? AppContext.BaseDirectory;
    }

    /// <summary>
    /// Настраивает Serilog с ежедневной ротацией и хранением заданного числа файлов.
    /// </summary>
    /// <param name="config">Конфигурация приложения.</param>
    private static void InitializeLogging(AppConfig config)
    {
        try
        {
            string logDirectory = GetLogDirectory();
            Directory.CreateDirectory(logDirectory);

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.File(
                    Path.Combine(logDirectory, "downtimegreeting-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: config.LogRetentionDays,
                    // shared: true — несколько процессов (GUI + плашка из задачи) могут писать одновременно.
                    shared: true,
                    encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Без журнала приложение должно продолжать работать.
            Log.Logger = new LoggerConfiguration().CreateLogger();
            MessageBox.Show(
                $"Не удалось открыть журнал приложения: {ex.Message}",
                "DowntimeGreeting",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Подписывается на глобальные обработчики необработанных исключений.
    /// </summary>
    private void AttachGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    /// <summary>
    /// Обработчик необработанного исключения в потоке интерфейса.
    /// </summary>
    /// <param name="sender">Источник события.</param>
    /// <param name="e">Аргументы события.</param>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Fatal(e.Exception, "Необработанное исключение в потоке интерфейса");

        // Окно ещё не показано — ошибка критическая, пользователь должен её увидеть.
        if (MainWindow is null)
        {
            ShowCriticalError(e.Exception);
        }

        e.Handled = true;
    }

    /// <summary>
    /// Обработчик необработанного исключения в домене приложения.
    /// </summary>
    /// <param name="sender">Источник события.</param>
    /// <param name="e">Аргументы события.</param>
    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Log.Fatal(e.ExceptionObject as Exception, "Необработанное исключение в домене приложения. Терминация: {IsTerminating}", e.IsTerminating);
        Log.CloseAndFlush();
    }

    /// <summary>
    /// Обработчик исключений в задачах, которые остались необработанными.
    /// </summary>
    /// <param name="sender">Источник события.</param>
    /// <param name="e">Аргументы события.</param>
    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Необработанное исключение в фоновой задаче");
        e.SetObserved();
    }

    /// <summary>
    /// Показывает модальное сообщение о критической ошибке.
    /// </summary>
    /// <param name="exception">Исключение.</param>
    private static void ShowCriticalError(Exception exception)
    {
        MessageBox.Show(
            $"Приложение столкнулось с критической ошибкой:{Environment.NewLine}{exception.Message}{Environment.NewLine}{Environment.NewLine}Подробности: {GetLogDirectory()}",
            "DowntimeGreeting",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    /// <summary>
    /// Захватывает мьютекс единственного экземпляра; при повторном запуске оповещает первый экземпляр.
    /// </summary>
    /// <returns><see langword="true"/>, если это первый экземпляр (или защита недоступна).</returns>
    private bool AcquireSingleInstance()
    {
        try
        {
            _instanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
            if (createdNew)
            {
                return true;
            }

            Log.Information("Приложение уже запущено — передаём запрос работающему экземпляру");
            SignalExistingInstance(_isGreetMode);

            _instanceMutex.Dispose();
            _instanceMutex = null;
            return false;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or NotSupportedException)
        {
            Log.Warning(ex, "Не удалось создать мьютекс единственного экземпляра — продолжаем без защиты");
            return true;
        }
    }

    /// <summary>
    /// Оповещает уже запущенный экземпляр о запросе (плашка или активация окна).
    /// </summary>
    /// <param name="greetMode">Признак запроса «показать плашку».</param>
    private static void SignalExistingInstance(bool greetMode)
    {
        string eventName = greetMode ? ShowGreetingEventName : ActivateWindowEventName;
        try
        {
            using EventWaitHandle handle = EventWaitHandle.OpenExisting(eventName);
            handle.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            Log.Debug("Работающий экземпляр ещё не готов принять запрос ({EventName})", eventName);
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Warning(ex, "Нет доступа к событию {EventName}", eventName);
        }
    }

    /// <summary>
    /// Запускает фоновый поток, принимающий запросы от повторно запущенных экземпляров.
    /// </summary>
    private void StartInstanceListener()
    {
        try
        {
            _showGreetingEvent = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, ShowGreetingEventName);
            _activateWindowEvent = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, ActivateWindowEventName);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or NotSupportedException)
        {
            Log.Warning(ex, "Не удалось создать события межпроцессного взаимодействия");
            return;
        }

        var thread = new Thread(ListenForSecondInstance)
        {
            IsBackground = true,
            Name = "DowntimeGreeting.SecondInstanceListener"
        };
        thread.Start();
    }

    /// <summary>
    /// Ожидает запросы от повторно запущенных экземпляров и пробрасывает их в поток интерфейса.
    /// </summary>
    private void ListenForSecondInstance()
    {
        WaitHandle[] handles = [_activateWindowEvent!, _showGreetingEvent!];

        while (true)
        {
            int index;
            try
            {
                index = WaitHandle.WaitAny(handles);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or AbandonedMutexException)
            {
                return;
            }

            if (index == WaitHandle.WaitTimeout)
            {
                continue;
            }

            try
            {
                if (index == 0)
                {
                    Dispatcher.BeginInvoke(new Action(BringMainWindowToFront));
                }
                else
                {
                    Dispatcher.BeginInvoke(new Action(() => _ = ShowGreetingAsync()));
                }
            }
            catch (Exception ex) when (ex is TaskCanceledException or InvalidOperationException)
            {
                // Приложение завершается.
                return;
            }
        }
    }

    /// <summary>
    /// Выводит главное окно на передний план (или показывает плашку, если главного окна нет).
    /// </summary>
    private void BringMainWindowToFront()
    {
        if (MainWindow is { } window)
        {
            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }

            window.Activate();
            return;
        }

        _ = ShowGreetingAsync();
    }

    /// <summary>
    /// Очищает ссылку на закрытую плашку.
    /// </summary>
    /// <param name="sender">Источник события.</param>
    /// <param name="e">Аргументы события.</param>
    private void OnGreetingWindowClosed(object? sender, EventArgs e)
    {
        if (sender is GreetingWindow window)
        {
            window.Closed -= OnGreetingWindowClosed;
        }

        _greetingWindow = null;
        Log.Debug("Ссылка на плашку очищена");
    }
}