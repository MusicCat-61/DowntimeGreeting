using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DowntimeGreeting.Interop;
using DowntimeGreeting.Localization;
using DowntimeGreeting.Models;
using DowntimeGreeting.Services;
using Serilog;

namespace DowntimeGreeting;

/// <summary>
/// Приветственная плашка: компактное «стеклянное» окно по центру основного монитора
/// с информацией о длительности простоя и обратным отсчётом до закрытия.
/// </summary>
public partial class GreetingWindow : Window
{
    /// <summary>Длительность анимации появления, миллисекунды.</summary>
    private const double FadeInDurationMs = 300;

    /// <summary>Длительность анимации исчезновения, миллисекунды.</summary>
    private const double FadeOutDurationMs = 300;

    /// <summary>Начальный масштаб плашки при появлении.</summary>
    private const double InitialScale = 0.95;

    /// <summary>Конечный масштаб плашки при исчезновении.</summary>
    private const double FinalScale = 0.97;

    private readonly DowntimeInfo _info;
    private readonly DispatcherTimer _countdownTimer;
    private readonly KeyboardHook _keyboardHook;
    private readonly bool _previewMode;

    private int _secondsLeft;
    private bool _isClosing;

    /// <summary>
    /// Инициализирует новый экземпляр <see cref="GreetingWindow"/>.
    /// </summary>
    /// <param name="config">Конфигурация приложения.</param>
    /// <param name="info">Информация о простое компьютера.</param>
    /// <param name="phrase">Шутливая фраза для отображения.</param>
    /// <param name="previewMode">
    /// Режим генерации превью: окно не центрируется, не запускает анимацию, отсчёт и хук клавиатуры,
    /// сразу отображается полностью непрозрачным — так его можно отрисовать в PNG.
    /// </param>
    /// <exception cref="ArgumentNullException">Один из аргументов равен <see langword="null"/>.</exception>
    public GreetingWindow(AppConfig config, DowntimeInfo info, string phrase, bool previewMode = false)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(info);

        _info = info;
        _secondsLeft = config.AutoCloseSeconds;
        _keyboardHook = new KeyboardHook();
        _previewMode = previewMode;

        InitializeComponent();

        PhraseText.Text = string.IsNullOrWhiteSpace(phrase) ? AppConfig.DefaultPhrases[0] : phrase;
        DowntimeText.Text = info.DowntimeText;
        CaptionText.Text = info.CaptionText;
        LastShutdownText.Text = info.ShutdownLineText;
        CountdownText.Text = FormatCountdownText(_secondsLeft);

        if (previewMode)
        {
            // Рендер превью: без анимации и центрирования, сразу в конечном состоянии.
            Opacity = 1d;
            RootScale.ScaleX = 1d;
            RootScale.ScaleY = 1d;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = PreviewRenderer.OffscreenCoordinate;
            Top = PreviewRenderer.OffscreenCoordinate;

            // Программный рендер (RenderTargetBitmap) не поддерживает эффекты: DropShadowEffect
            // даёт непрозрачную подложку. Для превью тень убираем — на экране она остаётся.
            CardBorder.Effect = null;
        }
        else
        {
            CenterOnPrimaryScreen();
        }

        _countdownTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _countdownTimer.Tick += OnCountdownTick;

        if (!previewMode)
        {
            _keyboardHook.EscapePressed += OnEscapePressed;
        }

        Loaded += OnLoaded;
        Closed += OnClosed;
        MouseLeftButtonDown += OnMouseLeftButtonDown;
    }

    /// <summary>Признак того, что плашка закрывается (анимация исчезновения уже запущена).</summary>
    public bool IsClosing => _isClosing;

    /// <summary>
    /// Позиционирует окно по центру основного монитора.
    /// </summary>
    /// <remarks>
    /// Координаты считаются вручную по <see cref="SystemParameters.PrimaryScreenWidth"/> и
    /// <see cref="SystemParameters.PrimaryScreenHeight"/>, чтобы при нескольких мониторах
    /// плашка появлялась именно на основном, а не на том, где находится курсор.
    /// </remarks>
    private void CenterOnPrimaryScreen()
    {
        double left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        double top = (SystemParameters.PrimaryScreenHeight - Height) / 2;

        Left = Math.Round(left);
        Top = Math.Round(top);

        Log.Debug("Позиция плашки: Left={Left}, Top={Top}", Left, Top);
    }

    /// <summary>
    /// Обработчик события загрузки окна: включает размытие, анимацию появления и отсчёт.
    /// </summary>
    /// <param name="sender">Источник события.</param>
    /// <param name="e">Аргументы события.</param>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_previewMode)
        {
            // Превью-режим: окно уже в конечном состоянии, анимации и отсчёт не нужны.
            Log.Debug("Плашка загружена в режиме превью");
            return;
        }

        BlurHelper.TryApplyBlur(this);

        if (!_keyboardHook.TryInstall())
        {
            Log.Debug("Закрытие по Esc недоступно — используется клик по плашке");
        }

        StartFadeIn();
        _countdownTimer.Start();

        Log.Information(
            "Плашка показана: «{Phrase}», простой «{Downtime}», источник {Source}",
            PhraseText.Text,
            _info.DowntimeText,
            _info.Source);
    }

    /// <summary>
    /// Запускает анимацию появления: прозрачность 0 → 1 и масштаб 0.95 → 1.0 за 300 мс.
    /// </summary>
    private void StartFadeIn()
    {
        var fadeIn = new DoubleAnimation(0d, 1d, TimeSpan.FromMilliseconds(FadeInDurationMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        BeginAnimation(OpacityProperty, fadeIn);

        var scaleIn = new DoubleAnimation(InitialScale, 1d, TimeSpan.FromMilliseconds(FadeInDurationMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        RootScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, scaleIn);
        RootScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, scaleIn);
    }

    /// <summary>
    /// Обработчик тика таймера обратного отсчёта.
    /// </summary>
    /// <param name="sender">Источник события.</param>
    /// <param name="e">Аргументы события.</param>
    private void OnCountdownTick(object? sender, EventArgs e)
    {
        _secondsLeft--;

        if (_secondsLeft <= 0)
        {
            BeginClose();
            return;
        }

        CountdownText.Text = FormatCountdownText(_secondsLeft);
    }

    /// <summary>
    /// Обработчик клика по плашке — плашка закрывается досрочно.
    /// </summary>
    /// <param name="sender">Источник события.</param>
    /// <param name="e">Аргументы события.</param>
    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Log.Debug("Плашка закрыта кликом");
        BeginClose();
    }

    /// <summary>
    /// Обработчик нажатия <c>Esc</c> (перехватывается глобальным хуком клавиатуры).
    /// </summary>
    /// <param name="sender">Источник события.</param>
    /// <param name="e">Аргументы события.</param>
    private void OnEscapePressed(object? sender, EventArgs e)
    {
        // Хук вызывается из оконной процедуры — откладываем закрытие до завершения обработки.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            Log.Debug("Плашка закрыта клавишей Esc");
            BeginClose();
        }));
    }

    /// <summary>
    /// Запускает анимацию исчезновения и закрывает окно.
    /// </summary>
    private void BeginClose()
    {
        if (_isClosing)
        {
            return;
        }

        _isClosing = true;
        _countdownTimer.Stop();

        var fadeOut = new DoubleAnimation(1d, 0d, TimeSpan.FromMilliseconds(FadeOutDurationMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        fadeOut.Completed += OnFadeOutCompleted;
        BeginAnimation(OpacityProperty, fadeOut);

        var scaleOut = new DoubleAnimation(1d, FinalScale, TimeSpan.FromMilliseconds(FadeOutDurationMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        RootScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, scaleOut);
        RootScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, scaleOut);
    }

    /// <summary>
    /// Закрывает окно после завершения анимации исчезновения.
    /// </summary>
    /// <param name="sender">Источник события.</param>
    /// <param name="e">Аргументы события.</param>
    private void OnFadeOutCompleted(object? sender, EventArgs e)
    {
        Log.Debug("Плашка закрыта");
        Close();
    }

    /// <summary>
    /// Освобождает ресурсы при закрытии окна.
    /// </summary>
    /// <param name="sender">Источник события.</param>
    /// <param name="e">Аргументы события.</param>
    private void OnClosed(object? sender, EventArgs e)
    {
        _countdownTimer.Stop();
        _countdownTimer.Tick -= OnCountdownTick;

        _keyboardHook.EscapePressed -= OnEscapePressed;
        _keyboardHook.Dispose();
    }

    /// <summary>
    /// Формирует текст обратного отсчёта.
    /// </summary>
    /// <param name="seconds">Сколько секунд осталось до закрытия.</param>
    /// <returns>Строка вида «Закроется через 5 с · Esc — закрыть».</returns>
    private static string FormatCountdownText(int seconds)
    {
        int safeSeconds = Math.Max(seconds, 1);
        return Loc.F("L_GreetingClosingFormat", safeSeconds);
    }
}