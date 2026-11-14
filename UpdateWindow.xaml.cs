using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using DowntimeGreeting.Common;
using DowntimeGreeting.Localization;
using DowntimeGreeting.Services;
using DowntimeGreeting.Updates;
using Serilog;

namespace DowntimeGreeting;

/// <summary>
/// Модальное окно обновления: показывает новую версию и заметки к релизу, скачивает сборку
/// с прогрессом и запускает установку.
/// </summary>
/// <remarks>
/// Окно модальное: пока пользователь не выбрал действие, главное окно недоступно. Это защищает
/// от случайного закрытия приложения в момент, когда скачивание уже идёт, и делает предложение
/// обновления явным. Оформление своё — стандартные заголовки Windows здесь не используются,
/// чтобы окно не выбивалось из стиля приложения.
/// </remarks>
public partial class UpdateWindow : Window
{
    private readonly UpdateCheckResult _result;
    private readonly bool _previewMode;

    private PreparedUpdate? _prepared;
    private bool _isBusy;
    private bool _installing;

    /// <summary>
    /// Инициализирует новый экземпляр <see cref="UpdateWindow"/>.
    /// </summary>
    /// <param name="result">Результат проверки, в котором есть новая версия.</param>
    /// <param name="previewMode">
    /// Режим генерации превью: окно не показывается модально, тень отключена,
    /// сетевые операции не выполняются.
    /// </param>
    /// <exception cref="ArgumentNullException">Результат проверки равен <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">В результате нет доступного обновления.</exception>
    public UpdateWindow(UpdateCheckResult result, bool previewMode = false)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (!result.HasUpdate || result.Latest is null)
        {
            throw new ArgumentException("В результате проверки нет доступного обновления.", nameof(result));
        }

        _result = result;
        _previewMode = previewMode;

        InitializeComponent();

        TitleText.Text = Loc.F("L_UpdAvailableTitleFormat", result.Latest.ToString());
        VersionText.Text = Loc.F(
            "L_UpdVersionLineFormat",
            UpdateService.CurrentVersionText,
            result.Latest.ToString());

        RenderNotes(result.Notes);

        StatusText.Text = Loc.T("L_UpdIntro");

        if (previewMode)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = PreviewRenderer.OffscreenCoordinate;
            Top = PreviewRenderer.OffscreenCoordinate;
            WindowBorder.Effect = null;
            StatusText.Text = Loc.F("L_UpdReadyToDownloadFormat", FormatSize(result.AssetSize));
            return;
        }

        Loc.LanguageChanged += OnLanguageChanged;
        Closed += OnClosed;

        // Ранее скачанное обновление сразу предлагается к установке.
        _prepared = UpdateService.FindPreparedUpdate();
        if (_prepared is not null)
        {
            ShowReadyToInstall(_prepared);
        }
    }

    /// <summary>
    /// Показывает окно модально и возвращает признак того, что приложение нужно закрыть для установки.
    /// </summary>
    /// <param name="owner">Окно-владелец.</param>
    /// <param name="result">Результат проверки, в котором есть новая версия.</param>
    /// <returns><see langword="true"/>, если запущена установка обновления.</returns>
    public static bool ShowModal(Window? owner, UpdateCheckResult result)
    {
        var window = new UpdateWindow(result);

        if (owner is not null && owner.IsVisible)
        {
            window.Owner = owner;
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        window.ShowDialog();
        return window._installing;
    }

    /// <summary>
    /// Обработчик основной кнопки: скачать обновление либо установить скачанное.
    /// </summary>
    /// <param name="sender">Источник события.</param>
    /// <param name="e">Аргументы события.</param>
    private async void OnActionButtonClick(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        if (_prepared is not null)
        {
            InstallPrepared();
            return;
        }

        _isBusy = true;
        ActionButton.IsEnabled = false;
        LaterButton.IsEnabled = false;
        ReleaseButton.IsEnabled = false;
        ProgressBar.Visibility = Visibility.Visible;
        ProgressBar.Value = 0;
        StatusText.Text = Loc.F("L_UpdDownloadingFormat", 0);

        try
        {
            var progress = new Progress<double>(value =>
            {
                int percent = (int)Math.Round(value * 100);
                ProgressBar.Value = percent;
                StatusText.Text = Loc.F("L_UpdDownloadingFormat", percent);
            });

            PreparedUpdate prepared = await UpdateService.DownloadAndPrepareAsync(_result, progress);
            ShowReadyToInstall(prepared);
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException
            or IOException or UnauthorizedAccessException or HttpRequestException)
        {
            Log.Error(ex, "Не удалось скачать обновление");
            StatusText.Text = ex.Message;
            ProgressBar.Visibility = Visibility.Collapsed;
        }
        finally
        {
            _isBusy = false;
            ActionButton.IsEnabled = true;
            LaterButton.IsEnabled = true;
            ReleaseButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Переводит окно в состояние «скачано, можно устанавливать».
    /// </summary>
    /// <param name="prepared">Скачанный и проверенный файл.</param>
    private void ShowReadyToInstall(PreparedUpdate prepared)
    {
        _prepared = prepared;
        ProgressBar.Visibility = Visibility.Collapsed;
        StatusText.Text = Loc.F("L_UpdReadyFormat", prepared.Version);
        ActionButton.Content = Loc.T("L_UpdInstall");
    }

    /// <summary>
    /// Запускает установку и закрывает окно с признаком необходимости выйти из приложения.
    /// </summary>
    private void InstallPrepared()
    {
        if (_prepared is null)
        {
            return;
        }

        ActionButton.IsEnabled = false;
        LaterButton.IsEnabled = false;
        ReleaseButton.IsEnabled = false;
        StatusText.Text = Loc.T("L_UpdRestarting");

        if (UpdateService.TryLaunchApplyScript(_prepared, out string error))
        {
            _installing = true;
            Log.Information("Установка обновления {Version} запущена", _prepared.Version);
            Close();
            return;
        }

        StatusText.Text = error;
        ActionButton.IsEnabled = true;
        LaterButton.IsEnabled = true;
        ReleaseButton.IsEnabled = true;
    }

    /// <summary>
    /// Открывает страницу релиза в браузере.
    /// </summary>
    /// <param name="sender">Источник события.</param>
    /// <param name="e">Аргументы события.</param>
    private void OnReleaseButtonClick(object sender, RoutedEventArgs e) =>
        UpdateService.OpenReleasePage(_result.ReleaseUrl);

    /// <summary>
    /// Закрывает окно без установки.
    /// </summary>
    /// <param name="sender">Источник события.</param>
    /// <param name="e">Аргументы события.</param>
    private void OnCloseButtonClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Закрывает окно по клавише <c>Esc</c>, если загрузка не идёт.
    /// </summary>
    /// <param name="sender">Источник события.</param>
    /// <param name="e">Аргументы события.</param>
    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        // Во время загрузки окно не закрывается: иначе непонятно, что стало с обновлением.
        if (!_isBusy)
        {
            Close();
        }

        e.Handled = true;
    }

    /// <summary>
    /// Обработчик перетаскивания окна за заголовок.
    /// </summary>
    /// <param name="sender">Источник события.</param>
    /// <param name="e">Аргументы события.</param>
    private void OnHeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    /// <summary>
    /// Обновляет подписи при смене языка интерфейса.
    /// </summary>
    private void OnLanguageChanged()
    {
        TitleText.Text = Loc.F("L_UpdAvailableTitleFormat", _result.Latest?.ToString() ?? "?");
        VersionText.Text = Loc.F(
            "L_UpdVersionLineFormat",
            UpdateService.CurrentVersionText,
            _result.Latest?.ToString() ?? "?");

        if (_prepared is not null)
        {
            StatusText.Text = Loc.F("L_UpdReadyFormat", _prepared.Version);
            ActionButton.Content = Loc.T("L_UpdInstall");
        }
        else
        {
            StatusText.Text = Loc.T("L_UpdIntro");
            ActionButton.Content = Loc.T("L_UpdInstallNow");
        }
    }

    /// <summary>
    /// Отписывается от события смены языка.
    /// </summary>
    /// <param name="sender">Источник события.</param>
    /// <param name="e">Аргументы события.</param>
    private void OnClosed(object? sender, EventArgs e) => Loc.LanguageChanged -= OnLanguageChanged;

    /// <summary>
    /// Строит оформленные блоки заметок к релизу.
    /// </summary>
    /// <param name="body">Тело релиза на GitHub.</param>
    private void RenderNotes(string? body)
    {
        IReadOnlyList<NoteBlock> blocks = ReleaseNotesFormatter.Parse(
            ReleaseNotesFormatter.SelectSection(body, Loc.CurrentCode));

        if (blocks.Count == 0)
        {
            NotesHost.Visibility = Visibility.Collapsed;
            return;
        }

        foreach (NoteBlock block in blocks)
        {
            NotesPanel.Children.Add(CreateBlock(block));
        }

        NotesHost.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Создаёт элемент для одного блока заметок.
    /// </summary>
    /// <param name="block">Блок заметок.</param>
    /// <returns>Элемент для панели заметок.</returns>
    private FrameworkElement CreateBlock(NoteBlock block)
    {
        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 22,
            FontSize = block.Kind is NoteBlockKind.Heading or NoteBlockKind.Subheading ? 15 : 14.5
        };

        switch (block.Kind)
        {
            case NoteBlockKind.Heading:
                text.FontWeight = FontWeights.SemiBold;
                text.Foreground = (Brush)FindResource(ThemeResourceKeys.PrimaryText);
                text.Margin = new Thickness(0, 10, 0, 4);
                break;

            case NoteBlockKind.Subheading:
                text.FontWeight = FontWeights.SemiBold;
                text.Foreground = (Brush)FindResource(ThemeResourceKeys.PrimaryText);
                text.Margin = new Thickness(0, 8, 0, 2);
                break;

            case NoteBlockKind.Bullet:
                text.Foreground = (Brush)FindResource(ThemeResourceKeys.SecondaryText);
                break;

            default:
                text.Foreground = (Brush)FindResource(ThemeResourceKeys.SecondaryText);
                text.Margin = new Thickness(0, 6, 0, 0);
                break;
        }

        foreach (NoteRun run in block.Runs)
        {
            text.Inlines.Add(CreateRun(run));
        }

        if (block.Kind != NoteBlockKind.Bullet)
        {
            return text;
        }

        // Маркер выносится отдельно, чтобы перенесённые строки оставались выровненными.
        var row = new DockPanel { Margin = new Thickness(0, 3, 0, 0) };
        var marker = new TextBlock
        {
            Text = "•",
            FontSize = 14.5,
            Margin = new Thickness(2, 0, 8, 0),
            Foreground = (Brush)FindResource(ThemeResourceKeys.Accent)
        };

        DockPanel.SetDock(marker, Dock.Left);
        row.Children.Add(marker);
        row.Children.Add(text);
        return row;
    }

    /// <summary>
    /// Создаёт фрагмент текста с оформлением.
    /// </summary>
    /// <param name="run">Фрагмент заметок.</param>
    /// <returns>Фрагмент для вставки в <see cref="TextBlock.Inlines"/>.</returns>
    private Run CreateRun(NoteRun run)
    {
        var element = new Run(run.Text);

        if (run.Bold)
        {
            element.FontWeight = FontWeights.SemiBold;
            element.Foreground = (Brush)FindResource(ThemeResourceKeys.PrimaryText);
        }

        if (run.Code)
        {
            element.FontFamily = new FontFamily("Consolas, Cascadia Mono, Segoe UI");
            element.Foreground = (Brush)FindResource(ThemeResourceKeys.Accent);
        }

        return element;
    }

    /// <summary>
    /// Форматирует размер файла для подсказки.
    /// </summary>
    /// <param name="bytes">Размер в байтах.</param>
    /// <returns>Строка вида «59,1 МБ» либо пустая строка, если размер неизвестен.</returns>
    private static string FormatSize(long bytes)
    {
        if (bytes <= 0)
        {
            return string.Empty;
        }

        double megabytes = bytes / (1024d * 1024d);
        return Loc.F("L_UpdSizeFormat", megabytes);
    }
}