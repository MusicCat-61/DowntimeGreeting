using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DowntimeGreeting.Common;
using DowntimeGreeting.Localization;
using DowntimeGreeting.Models;
using DowntimeGreeting.Services;
using DowntimeGreeting.Updates;
using Serilog;

namespace DowntimeGreeting;

/// <summary>
/// Главное окно приложения: управление автозагрузкой (задачей Планировщика Windows),
/// предпросмотр плашки и редактирование настроек из <c>appsettings.json</c>.
/// </summary>
public partial class MainWindow : Window
{
    private readonly TaskSchedulerService _taskScheduler = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly string _executablePath;
    private readonly bool _previewMode;

    private AppConfig _config;

    /// <summary>Защита от рекурсии при синхронизации колеса, текстового поля и ползунка яркости.</summary>
    private bool _isSyncingAccent;

    /// <summary>Признак того, что задача автозапуска зарегистрирована (по последнему опросу).</summary>
    private bool _isTaskRegistered;

    /// <summary>Признак выполнения операции с Планировщиком.</summary>
    private bool _isTaskBusy;

    /// <summary>Фразы на русском, редактируемые в окне.</summary>
    private List<string> _phrasesRu = [];

    /// <summary>Фразы на английском, редактируемые в окне.</summary>
    private List<string> _phrasesEn = [];

    /// <summary>
    /// Инициализирует новый экземпляр <see cref="MainWindow"/>.
    /// </summary>
    /// <param name="config">Текущая конфигурация приложения.</param>
    /// <param name="previewMode">
    /// Режим генерации превью: окно не центрируется на экране и не опрашивает Планировщик,
    /// а сразу показывается полностью отрисованным для рендера в PNG.
    /// </param>
    /// <exception cref="ArgumentNullException">Конфигурация равна <see langword="null"/>.</exception>
    public MainWindow(AppConfig config, bool previewMode = false)
    {
        ArgumentNullException.ThrowIfNull(config);

        _config = config;
        _executablePath = ResolveExecutablePath();
        _previewMode = previewMode;

        InitializeComponent();

        if (previewMode)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = PreviewRenderer.OffscreenCoordinate;
            Top = PreviewRenderer.OffscreenCoordinate;
            StatusText.Text = Loc.T("L_StartupOn");
            StatusHintText.Text = Loc.F("L_StartupRegisteredFormat", _taskScheduler.TaskName);
            StatusDot.Fill = (Brush)Application.Current.Resources[ThemeResourceKeys.Accent];

            // Программный рендер не поддерживает DropShadowEffect (даёт непрозрачную подложку).
            WindowBorder.Effect = null;
        }
        else
        {
            FitToWorkArea();
        }

        // До первого опроса Планировщика состояние задачи неизвестно: кнопки заблокированы.
        // В режиме превью состояние задано вручную и опрос не выполняется.
        _isTaskRegistered = previewMode;
        _isTaskBusy = !previewMode;
        UpdateTaskButtons();

        SettingsPathText.Text = Loc.F("L_SettingsPathFormat", AppConfig.DefaultPath);
        SettingsPathText.ToolTip = AppConfig.DefaultPath;

        LoadConfigIntoControls();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    /// <summary>
    /// Уменьшает высоту окна, если рабочая область экрана меньше штатного размера.
    /// </summary>
    private void FitToWorkArea()
    {
        double availableHeight = SystemParameters.WorkArea.Height - 40;
        if (Height > availableHeight)
        {
            Height = Math.Max(MinHeight, availableHeight);
            Log.Debug("Высота окна уменьшена до {Height} под рабочую область", Height);
        }
    }

    /// <summary>
    /// Возвращает путь к исполняемому файлу приложения (используется при регистрации задачи).
    /// </summary>
    /// <returns>Полный путь к <c>DowntimeGreeting.exe</c>.</returns>
    private static string ResolveExecutablePath()
    {
        string? processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            string fileName = Path.GetFileName(processPath);
            if (fileName.Equals("DowntimeGreeting.exe", StringComparison.OrdinalIgnoreCase))
            {
                return processPath;
            }
        }

        // Запуск через «dotnet DowntimeGreeting.dll»: для задачи нужен apphost рядом со сборкой.
        string candidate = Path.Combine(AppContext.BaseDirectory, "DowntimeGreeting.exe");
        if (File.Exists(candidate))
        {
            return candidate;
        }

        return processPath ?? AppContext.BaseDirectory;
    }

    /// <summary>
    /// Заполняет элементы управления значениями из конфигурации.
    /// </summary>
    private void LoadConfigIntoControls()
    {
        AutoCloseSlider.Value = Math.Clamp(
            _config.AutoCloseSeconds,
            AppConfig.MinimumAutoCloseSeconds,
            AppConfig.MaximumAutoCloseSeconds);
        AutoCloseValueText.Text = _config.AutoCloseSeconds.ToString(CultureInfo.InvariantCulture);
        AccentTextBox.Text = _config.AccentColor;
        AccentPreviewBrush.Color = _config.Accent;
        AccentWheel.SelectedColor = _config.Accent;
        AccentValueSlider.Hue = AccentWheel.Hue;
        AccentValueSlider.Saturation = AccentWheel.Saturation;
        AccentValueSlider.Value = AccentWheel.Brightness;

        _phrasesRu = [.. _config.Phrases];
        _phrasesEn = [.. _config.PhrasesEn];
        LoadPhrasesIntoEditor();

        PreviewHintText.Text = Loc.F("L_PlaquePreviewHintFormat", _config.AutoCloseSeconds);
        SaveStatusText.Text = string.Empty;
        UpdateLanguageButtons();
    }

    /// <summary>
    /// Показывает в поле ввода фразы того языка, который сейчас выбран.
    /// </summary>
    private void LoadPhrasesIntoEditor()
    {
        IReadOnlyList<string> source = Loc.Current == AppLanguage.Ru ? _phrasesRu : _phrasesEn;
        PhrasesTextBox.Text = string.Join(Environment.NewLine, source);
    }

    /// <summary>
    /// Сохраняет текущее содержимое поля фраз в список активного языка.
    /// </summary>
    /// <remarks>
    /// Пустые строки при переключении языка отбрасываются молча: пользователь ещё не нажал
    /// «Сохранить», а терять введённый текст при смене языка нельзя. Строгая проверка
    /// выполняется при сохранении настроек.
    /// </remarks>
    private void StashPhrasesFromEditor()
    {
        if (PhrasesTextBox is null)
        {
            return;
        }

        List<string> collected = PhrasesTextBox.Text
            .Split(["\r\n", "\n", "\r"], StringSplitOptions.None)
            .Select(static line => line.Trim())
            .Where(static line => line.Length > 0)
            .ToList();

        if (collected.Count == 0)
        {
            return;
        }

        if (Loc.Current == AppLanguage.Ru)
        {
            _phrasesRu = collected;
        }
        else
        {
            _phrasesEn = collected;
        }
    }

    /// <summary>
    /// Подсвечивает кнопку активного языка.
    /// </summary>
    private void UpdateLanguageButtons()
    {
        if (LangRuButton is null || LangEnButton is null)
        {
            return;
        }

        LangRuButton.Tag = Loc.Current == AppLanguage.Ru ? "Active" : null;
        LangEnButton.Tag = Loc.Current == AppLanguage.En ? "Active" : null;
    }

    /// <summary>
    /// Обработчик загрузки окна: обновляет статус задачи и подписку на изменение цвета.
    /// </summary>
    /// <param name="sender">Источник события.</param>
    /// <param name="e">Аргументы события.</param>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AccentTextBox.TextChanged += OnAccentTextChanged;

        if (_previewMode)
        {
            // Превью: статус уже выставлен в конструкторе, Планировщик не опрашиваем.
            return;
        }

        _ = InitializeTaskStatusAsync();
    }

    /// <summary>
    /// Первичный опрос Планировщика: на время запроса кнопки остаются заблокированными,
    /// после ответа доступность выставляется по фактическому состоянию задачи.
    /// </summary>
    /// <returns>Задача, представляющая асинхронную операцию.</returns>
    private async Task InitializeTaskStatusAsync()
    {
        _isTaskBusy = true;
        UpdateTaskButtons();

        try
        {
            await RefreshTaskStatusAsync();
        }
        finally
        {
            _isTaskBusy = false;
            UpdateTaskButtons();
        }
    }

    /// <summary>
    /// Освобождает ресурсы при закрытии окна.
    /// </summary>
    /// <param name="sender">Источник события.</param>
    /// <param name="e">Аргументы события.</param>
    private void OnClosed(object? sender, EventArgs e)
    {
        _cancellation.Cancel();
        _cancellation.Dispose();
    }

    /// <summary>
    /// Обновляет индикатор состояния задачи автозапуска.
    /// </summary>
    /// <returns>Задача, представляющая асинхронную операцию.</returns>
    private async Task RefreshTaskStatusAsync()
    {
        try
        {
            ScheduledTaskStatus status = await _taskScheduler.GetStatusAsync(_cancellation.Token);
            StatusText.Text = status.IsRegistered
                ? "Автозагрузка включена"
                : "Автозагрузка выключена";
            StatusHintText.Text = status.Message;
            StatusDot.Fill = (Brush)Application.Current.Resources[
                status.IsRegistered ? ThemeResourceKeys.Accent : ThemeResourceKeys.SecondaryText];

            _isTaskRegistered = status.IsRegistered;
        }
        catch (OperationCanceledException)
        {
            // Окно закрывается — ничего не делаем.
            return;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Не удалось получить статус задачи автозапуска");
            StatusText.Text = "Статус автозагрузки неизвестен";
            StatusHintText.Text = "Не удалось опросить Планировщик Windows. Подробности в журнале.";
        }
        finally
        {
            UpdateTaskButtons();
        }
    }

    /// <summary>
    /// Приводит доступность кнопок управления задачей в соответствие с её состоянием.
    /// </summary>
    /// <remarks>
    /// «Добавить» активна только когда задачи нет, «Убрать» — только когда она есть.
    /// Во время выполнения операции обе кнопки блокируются, чтобы нельзя было
    /// запустить регистрацию или удаление повторно.
    /// </remarks>
    private void UpdateTaskButtons()
    {
        bool canInteract = !_isTaskBusy;
        InstallButton.IsEnabled = canInteract && !_isTaskRegistered;
        UninstallButton.IsEnabled = canInteract && _isTaskRegistered;
        SaveButton.IsEnabled = !_isTaskBusy;
    }

    /// <summary>
    /// Переключает признак выполнения операции с задачей и обновляет кнопки.
    /// </summary>
    /// <param name="isBusy">Признак выполнения операции.</param>
    private void SetBusyState(bool isBusy)
    {
        _isTaskBusy = isBusy;

        if (isBusy)
        {
            StatusText.Text = "Выполняется…";
        }
        else if (_isTaskRegistered)
        {
            StatusText.Text = "Автозагрузка включена";
        }
        else
        {
            StatusText.Text = "Автозагрузка выключена";
        }

        UpdateTaskButtons();
    }

    /// <summary>
    /// Обработчик кнопки «Добавить в автозагрузку».
    /// </summary>
    /// <param name="sender">Источник события.</param>
    /// <param name="e">Аргументы события.</param>
    private async void OnInstallButtonClick(object sender, RoutedEventArgs e)
    {
        if (_isTaskBusy)
        {
            // Операция уже выполняется — повторный клик игнорируем.
            return;
        }

        if (!_executablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(_executablePath))
        {
            StatusHintText.Text = "Не удалось определить путь к DowntimeGreeting.exe. Запустите собранное приложение (bin\\Release\\net10.0-windows\\DowntimeGreeting.exe).";
            return;
        }

        SetBusyState(true);
        try
        {
            TaskOperationResult result = await _taskScheduler.RegisterAsync(_executablePath, _cancellation.Token);
            StatusHintText.Text = result.Message;
            if (result.Success)
            {
                await RefreshTaskStatusAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // Окно закрывается.
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Ошибка при регистрации задачи автозапуска");
            StatusHintText.Text = Loc.T("L_StartupAddFailed");
        }
        finally
        {
            SetBusyState(false);
        }
    }

    /// <summary>
    /// Обработчик кнопки «Убрать из автозагрузки».
    /// </summary>
    /// <param name="sender">Источник события.</param>
    /// <param name="e">Аргументы события.</param>
    private async void OnUninstallButtonClick(object sender, RoutedEventArgs e)
    {
        if (_isTaskBusy)
        {
            // Операция уже выполняется — повторный клик игнорируем.
            return;
        }

        SetBusyState(true);
        try
        {
            TaskOperationResult result = await _taskScheduler.UnregisterAsync(_cancellation.Token);
            StatusHintText.Text = result.Message;
            if (result.Success)
            {
                await RefreshTaskStatusAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // Окно закрывается.
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Ошибка при удалении задачи автозапуска");
            StatusHintText.Text = Loc.T("L_StartupRemoveFailed");
        }
        finally
        {
            SetBusyState(false);
        }
    }

    /// <summary>
    /// Обработчик кнопки «Показать сейчас» — предпросмотр плашки.
    /// </summary>
    /// <param name="sender">Источник события.</param>
    /// <param name="e">Аргументы события.</param>
    private void OnPreviewButtonClick(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App app)
        {
            _ = app.ShowGreetingAsync();
        }
    }

    /// <summary>
    /// Обработчик кнопки «Сохранить настройки».
    /// </summary>
    /// <param name="sender">Источник события.</param>
    /// <param name="e">Аргументы события.</param>
    private void OnSaveButtonClick(object sender, RoutedEventArgs e)
    {
        if (!TryReadControls(out AppConfig? updated, out string? error))
        {
            SaveStatusText.Text = error;
            SaveStatusText.Foreground = (Brush)Application.Current.Resources[ThemeResourceKeys.SecondaryText];
            return;
        }

        if (AppConfig.TrySave(updated!, out string? saveError))
        {
            _config = updated!;

            if (Application.Current is App app)
            {
                app.ApplyConfig(updated!);
            }

            PreviewHintText.Text = Loc.F("L_PlaquePreviewHintFormat", updated!.AutoCloseSeconds);
            SaveStatusText.Text = Loc.T("L_Saved");
            SaveStatusText.Foreground = (Brush)Application.Current.Resources[ThemeResourceKeys.Accent];
            Log.Information("Настройки сохранены: {Config}", updated);
        }
        else
        {
            SaveStatusText.Text = Loc.F("L_SaveFailedFormat", saveError);
            SaveStatusText.Foreground = (Brush)Application.Current.Resources[ThemeResourceKeys.SecondaryText];
        }
    }

    /// <summary>
    /// Показывает модальное окно обновления и закрывает приложение, если запущена установка.
    /// </summary>
    /// <param name="result">Результат проверки, в котором есть новая версия.</param>
    public void ShowUpdateAvailable(UpdateCheckResult result)
    {
        if (!result.HasUpdate)
        {
            return;
        }

        if (UpdateWindow.ShowModal(this, result))
        {
            // Скрипт подмены уже запущен и ждёт выхода процесса.
            Application.Current.Shutdown();
        }
    }
    /// <summary>
    /// Обработчик кнопки «Открыть логи».
    /// </summary>
    /// <param name="sender">Источник события.</param>
    /// <param name="e">Аргументы события.</param>
    private void OnOpenLogsButtonClick(object sender, RoutedEventArgs e)
    {
        string logDirectory = App.GetLogDirectory();
        try
        {
            Directory.CreateDirectory(logDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{logDirectory}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Log.Error(ex, "Не удалось открыть папку журналов {Path}", logDirectory);
            SaveStatusText.Text = Loc.T("L_OpenLogsFailed");
        }
    }

    /// <summary>
    /// Обработчик перетаскивания окна за заголовок.
    /// </summary>
    /// <param name="sender">Источник события.</param>
    /// <param name="e">Аргументы события.</param>
    private void OnTitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    /// <summary>
    /// Обработчик кнопки закрытия окна.
    /// </summary>
    /// <param name="sender">Источник события.</param>
    /// <param name="e">Аргументы события.</param>
    private void OnCloseButtonClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Открывает окно «О программе».
    /// </summary>
    /// <param name="sender">Источник события.</param>
    /// <param name="e">Аргументы события.</param>
    private void OnAboutButtonClick(object sender, RoutedEventArgs e)
    {
        var about = new AboutWindow { Owner = this };
        about.ShowDialog();
    }

    /// <summary>
    /// Переключает интерфейс на русский язык.
    /// </summary>
    /// <param name="sender">Источник события.</param>
    /// <param name="e">Аргументы события.</param>
    private void OnLanguageRuClick(object sender, RoutedEventArgs e) => SwitchLanguage(AppLanguage.Ru);

    /// <summary>
    /// Переключает интерфейс на английский язык.
    /// </summary>
    /// <param name="sender">Источник события.</param>
    /// <param name="e">Аргументы события.</param>
    private void OnLanguageEnClick(object sender, RoutedEventArgs e) => SwitchLanguage(AppLanguage.En);

    /// <summary>
    /// Применяет новый язык интерфейса, сохраняя введённые фразы.
    /// </summary>
    /// <param name="language">Новый язык.</param>
    private void SwitchLanguage(AppLanguage language)
    {
        if (Loc.Current == language)
        {
            return;
        }

        // Фразы другого языка нельзя потерять: сначала складываем текущие.
        StashPhrasesFromEditor();

        Loc.SetLanguage(language);
        LoadPhrasesIntoEditor();
        UpdateLanguageButtons();

        // Тексты, заданные из кода, обновляем вручную.
        PreviewHintText.Text = Loc.F("L_PlaquePreviewHintFormat", (int)Math.Round(AutoCloseSlider.Value));
        SettingsPathText.Text = Loc.F("L_SettingsPathFormat", AppConfig.DefaultPath);

        if (_isTaskRegistered)
        {
            StatusText.Text = Loc.T("L_StartupOn");
            StatusHintText.Text = Loc.F("L_StartupRegisteredFormat", _taskScheduler.TaskName);
        }
        else
        {
            StatusText.Text = Loc.T("L_StartupOff");
            StatusHintText.Text = Loc.T("L_StartupNotRegistered");
        }

        SaveStatusText.Text = string.Empty;
        Log.Information("Язык интерфейса переключён на {Language}", language);
    }

    /// <summary>
    /// Обновляет подпись ползунка автозакрытия.
    /// </summary>
    /// <param name="sender">Источник события.</param>
    /// <param name="e">Аргументы события.</param>
    private void OnAutoCloseSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Обработчик может сработать до инициализации связанных элементов XAML.
        if (AutoCloseValueText is null || PreviewHintText is null)
        {
            return;
        }

        int seconds = (int)Math.Round(e.NewValue);
        AutoCloseValueText.Text = seconds.ToString(CultureInfo.InvariantCulture);
        PreviewHintText.Text = Loc.F("L_PlaquePreviewHintFormat", seconds);
    }

    /// <summary>
    /// Обновляет предпросмотр акцентного цвета при вводе.
    /// </summary>
    /// <param name="sender">Источник события.</param>
    /// <param name="e">Аргументы события.</param>
    private void OnAccentTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!TryParseColor(AccentTextBox.Text, out Color color))
        {
            return;
        }

        AccentPreviewBrush.Color = color;

        // Текст — источник истины при ручном вводе: синхронизируем колесо без рекурсии.
        if (_isSyncingAccent)
        {
            return;
        }

        _isSyncingAccent = true;
        try
        {
            AccentWheel.SelectedColor = color;
        }
        finally
        {
            _isSyncingAccent = false;
        }
    }

    /// <summary>
    /// Обработчик выбора цвета на цветовом колесе.
    /// </summary>
    /// <param name="sender">Источник события.</param>
    /// <param name="e">Аргументы события.</param>
    private void OnAccentWheelColorChanged(object? sender, EventArgs e)
    {
        if (_isSyncingAccent || AccentTextBox is null)
        {
            return;
        }

        Color color = AccentWheel.SelectedColor;

        _isSyncingAccent = true;
        try
        {
            AccentTextBox.Text = NormalizeColorText(color);
            AccentPreviewBrush.Color = color;
            AccentValueSlider.Hue = AccentWheel.Hue;
            AccentValueSlider.Saturation = AccentWheel.Saturation;
            AccentValueSlider.Value = AccentWheel.Brightness;
        }
        finally
        {
            _isSyncingAccent = false;
        }
    }

    /// <summary>
    /// Обработчик изменения яркости на ползунке.
    /// </summary>
    /// <param name="sender">Источник события.</param>
    /// <param name="e">Аргументы события.</param>
    private void OnAccentValueChanged(object? sender, EventArgs e)
    {
        if (_isSyncingAccent || AccentWheel is null)
        {
            return;
        }

        AccentWheel.SetBrightness(AccentValueSlider.Value);

        // Ползунок яркости меняет цвет — сразу обновляем текст и предпросмотр.
        Color color = AccentWheel.SelectedColor;
        _isSyncingAccent = true;
        try
        {
            AccentTextBox.Text = NormalizeColorText(color);
            AccentPreviewBrush.Color = color;
            AccentValueSlider.Hue = AccentWheel.Hue;
            AccentValueSlider.Saturation = AccentWheel.Saturation;
        }
        finally
        {
            _isSyncingAccent = false;
        }
    }

    /// <summary>
    /// Читает и проверяет значения из элементов управления.
    /// </summary>
    /// <param name="config">Новая конфигурация, если проверка успешна.</param>
    /// <param name="error">Текст ошибки, если проверка не пройдена.</param>
    /// <returns><see langword="true"/>, если данные корректны.</returns>
    private bool TryReadControls(out AppConfig? config, out string? error)
    {
        config = null;
        error = null;

        int seconds = (int)Math.Round(AutoCloseSlider.Value);

        if (!TryParseColor(AccentTextBox.Text, out Color accent))
        {
            error = Loc.T("L_ErrorColor");
            return false;
        }

        if (!TryReadPhrases(out List<string> currentPhrases, out error))
        {
            return false;
        }

        // Сохраняем список в зависимости от языка, который сейчас редактируется.
        List<string> phrasesRu = Loc.Current == AppLanguage.Ru ? currentPhrases : [.. _phrasesRu];
        List<string> phrasesEn = Loc.Current == AppLanguage.En ? currentPhrases : [.. _phrasesEn];

        config = new AppConfig
        {
            AutoCloseSeconds = seconds,
            Phrases = phrasesRu,
            PhrasesEn = phrasesEn,
            Language = Loc.CurrentCode,
            AccentColor = NormalizeColorText(accent),
            Accent = accent,
            LogRetentionDays = _config.LogRetentionDays
        };

        return true;
    }

    /// <summary>
    /// Читает фразы из поля ввода, не допуская пустых строк.
    /// </summary>
    /// <param name="phrases">Очищенный список фраз.</param>
    /// <param name="error">Текст ошибки, если данные некорректны.</param>
    /// <returns><see langword="true"/>, если список корректен.</returns>
    private bool TryReadPhrases(out List<string> phrases, out string? error)
    {
        phrases = [];
        error = null;

        string[] lines = PhrasesTextBox.Text.Split(["\r\n", "\n", "\r"], StringSplitOptions.None);

        // Пустая строка внутри списка — почти всегда опечатка: фраза потеряется молча.
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0)
            {
                // Последняя пустая строка появляется сама при завершающем переводе строки.
                if (i == lines.Length - 1)
                {
                    continue;
                }

                error = Loc.F("L_ErrorPhrasesEmptyLineFormat", i + 1);
                return false;
            }

            phrases.Add(line);
        }

        if (phrases.Count == 0)
        {
            error = Loc.T("L_ErrorPhrasesEmpty");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Разбирает строку цвета; в отличие от <see cref="AppConfig.ParseColor"/> сообщает об ошибке.
    /// </summary>
    /// <param name="value">Текст с цветом.</param>
    /// <param name="color">Разобранный цвет.</param>
    /// <returns><see langword="true"/>, если цвет разобран.</returns>
    private static bool TryParseColor(string? value, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            object? converted = ColorConverter.ConvertFromString(value.Trim());
            if (converted is Color parsed)
            {
                color = parsed;
                return true;
            }
        }
        catch (FormatException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }

        return false;
    }

    /// <summary>
    /// Возвращает строку цвета в формате <c>#RRGGBB</c> или <c>#AARRGGBB</c>.
    /// </summary>
    /// <param name="color">Цвет.</param>
    /// <returns>Строковое представление цвета.</returns>
    private static string NormalizeColorText(Color color) => color.A == 0xFF
        ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
        : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
}