using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using DowntimeGreeting.Common;
using DowntimeGreeting.Localization;
using DowntimeGreeting.Services;
using DowntimeGreeting.Updates;
using Serilog;

namespace DowntimeGreeting;

/// <summary>
/// Окно «О программе»: версия, краткое описание, ссылки на репозиторий, Telegram-канал
/// и донат, а также проверка обновлений по кнопке.
/// </summary>
public partial class AboutWindow : Window
{
    private readonly bool _previewMode;

    private string? _releaseUrl;
    private bool _isChecking;

    /// <summary>
    /// Инициализирует новый экземпляр <see cref="AboutWindow"/>.
    /// </summary>
    /// <param name="previewMode">
    /// Режим генерации превью: окно не центрируется относительно владельца,
    /// не подписывается на смену языка и не выполняет сетевых запросов.
    /// </param>
    public AboutWindow(bool previewMode = false)
    {
        _previewMode = previewMode;

        InitializeComponent();

        VersionText.Text = Loc.F("L_AboutVersionFormat", UpdateService.CurrentVersionText);

        if (previewMode)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = PreviewRenderer.OffscreenCoordinate;
            Top = PreviewRenderer.OffscreenCoordinate;

            // Программный рендер не поддерживает DropShadowEffect (даёт непрозрачную подложку).
            WindowBorder.Effect = null;
            return;
        }

        Loc.LanguageChanged += OnLanguageChanged;
        Closed += OnClosed;
    }

    /// <summary>
    /// Обработчик кнопки «Проверить обновления».
    /// </summary>
    /// <param name="sender">Источник события.</param>
    /// <param name="e">Аргументы события.</param>
    private async void OnCheckUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_isChecking)
        {
            return;
        }

        _isChecking = true;
        CheckUpdateButton.IsEnabled = false;
        OpenReleaseButton.Visibility = Visibility.Collapsed;
        UpdateStatusText.Text = Loc.T("L_UpdChecking");

        try
        {
            UpdateCheckResult result = await UpdateService.CheckAsync();

            _releaseUrl = result.ReleaseUrl;

            switch (result.Status)
            {
                case UpdateStatus.UpToDate:
                    UpdateStatusText.Text = Loc.F("L_UpdUpToDateFormat", result.Latest?.ToString() ?? "?");
                    break;

                case UpdateStatus.UpdateAvailable:
                    UpdateStatusText.Text = Loc.F("L_UpdAvailableFormat", result.Latest?.ToString() ?? "?");
                    OpenReleaseButton.Visibility = Visibility.Visible;

                    // Модальное окно предлагает скачать и установить обновление сразу.
                    if (UpdateWindow.ShowModal(this, result))
                    {
                        Application.Current.Shutdown();
                    }

                    break;

                case UpdateStatus.NoReleases:
                    UpdateStatusText.Text = Loc.T("L_UpdNoReleaseYet");
                    break;

                default:
                    UpdateStatusText.Text = result.Error ?? Loc.T("L_UpdFailed");
                    break;
            }
        }
        finally
        {
            _isChecking = false;
            CheckUpdateButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Обработчик кнопки «Открыть страницу релиза».
    /// </summary>
    /// <param name="sender">Источник события.</param>
    /// <param name="e">Аргументы события.</param>
    private void OnOpenReleaseClick(object sender, RoutedEventArgs e) =>
        UpdateService.OpenReleasePage(_releaseUrl);

    /// <summary>
    /// Открывает репозиторий проекта.
    /// </summary>
    /// <param name="sender">Источник события.</param>
    /// <param name="e">Аргументы события.</param>
    private void OnProjectLinkClick(object sender, RoutedEventArgs e) => OpenUrl(AppInfo.RepositoryUrl);

    /// <summary>
    /// Открывает Telegram-канал автора.
    /// </summary>
    /// <param name="sender">Источник события.</param>
    /// <param name="e">Аргументы события.</param>
    private void OnTelegramLinkClick(object sender, RoutedEventArgs e) => OpenUrl(AppInfo.TelegramUrl);

    /// <summary>
    /// Открывает страницу поддержки проекта.
    /// </summary>
    /// <param name="sender">Источник события.</param>
    /// <param name="e">Аргументы события.</param>
    private void OnDonateLinkClick(object sender, RoutedEventArgs e) => OpenUrl(AppInfo.DonateUrl);

    /// <summary>
    /// Обработчик кнопки закрытия окна.
    /// </summary>
    /// <param name="sender">Источник события.</param>
    /// <param name="e">Аргументы события.</param>
    private void OnCloseButtonClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Закрывает окно по клавише <c>Esc</c>.
    /// </summary>
    /// <param name="sender">Источник события.</param>
    /// <param name="e">Аргументы события.</param>
    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
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
    /// Обновляет тексты, заданные из кода, при смене языка.
    /// </summary>
    private void OnLanguageChanged()
    {
        VersionText.Text = Loc.F("L_AboutVersionFormat", UpdateService.CurrentVersionText);
        UpdateStatusText.Text = Loc.T("L_UpdIdle");
        OpenReleaseButton.Visibility = Visibility.Collapsed;
        _releaseUrl = null;
    }

    /// <summary>
    /// Отписывается от события смены языка при закрытии окна.
    /// </summary>
    /// <param name="sender">Источник события.</param>
    /// <param name="e">Аргументы события.</param>
    private void OnClosed(object? sender, EventArgs e) => Loc.LanguageChanged -= OnLanguageChanged;

    /// <summary>
    /// Открывает ссылку в браузере по умолчанию.
    /// </summary>
    /// <param name="url">Адрес.</param>
    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error(ex, "Не удалось открыть ссылку {Url}", url);
        }
    }
}