using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using DowntimeGreeting.Common;
using DowntimeGreeting.Models;
using Microsoft.Win32;
using Serilog;

namespace DowntimeGreeting.Services;

/// <summary>
/// Определяет текущую тему оформления Windows и создаёт соответствующий набор цветов и кистей.
/// </summary>
/// <remarks>
/// Тема читается из реестра: <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize</c>,
/// параметр <c>AppsUseLightTheme</c> (1 — светлая, 0 — тёмная). Если параметр отсутствует
/// или прочитать его не удалось, используется тёмная тема.
/// Для отладки тему можно принудительно задать переменной окружения <c>BOOTGREETING_THEME</c>
/// со значением <c>light</c> или <c>dark</c>.
/// </remarks>
public static class ThemeService
{
    /// <summary>Путь к разделу реестра с персональными настройками темы.</summary>
    private const string PersonalizeRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>Имя параметра: 1 — приложения используют светлую тему.</summary>
    private const string AppsUseLightThemeValueName = "AppsUseLightTheme";

    /// <summary>Переменная окружения для принудительного выбора темы (диагностика).</summary>
    private const string ThemeOverrideVariable = "BOOTGREETING_THEME";

    /// <summary>Фон плашки в тёмной теме.</summary>
    private static readonly Color DarkCardBackground = Color.FromArgb(0xCC, 0x1E, 0x1E, 0x1E);

    /// <summary>Рамка плашки в тёмной теме.</summary>
    private static readonly Color DarkCardBorder = Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF);

    /// <summary>Основной текст в тёмной теме.</summary>
    private static readonly Color DarkPrimaryText = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);

    /// <summary>Второстепенный текст в тёмной теме.</summary>
    private static readonly Color DarkSecondaryText = Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF);

    /// <summary>Блик стекла в тёмной теме.</summary>
    private static readonly Color DarkSheen = Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF);

    /// <summary>Тень в тёмной теме.</summary>
    private static readonly Color DarkShadow = Color.FromArgb(0xFF, 0x00, 0x00, 0x00);

    /// <summary>Фон плашки в светлой теме.</summary>
    private static readonly Color LightCardBackground = Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF);

    /// <summary>Рамка плашки в светлой теме.</summary>
    private static readonly Color LightCardBorder = Color.FromArgb(0x30, 0x00, 0x00, 0x00);

    /// <summary>Основной текст в светлой теме.</summary>
    private static readonly Color LightPrimaryText = Color.FromArgb(0xFF, 0x16, 0x16, 0x16);

    /// <summary>Второстепенный текст в светлой теме.</summary>
    private static readonly Color LightSecondaryText = Color.FromArgb(0xA0, 0x00, 0x00, 0x00);

    /// <summary>Блик стекла в светлой теме.</summary>
    private static readonly Color LightSheen = Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF);

    /// <summary>Тень в светлой теме.</summary>
    private static readonly Color LightShadow = Color.FromArgb(0xFF, 0x20, 0x20, 0x20);

    /// <summary>Фон главного окна в тёмной теме.</summary>
    private static readonly Color DarkWindowBackground = Color.FromArgb(0xF5, 0x1E, 0x1E, 0x1E);

    /// <summary>Фон главного окна в светлой теме.</summary>
    private static readonly Color LightWindowBackground = Color.FromArgb(0xF5, 0xFA, 0xFA, 0xFA);

    /// <summary>Заливка полей ввода в тёмной теме.</summary>
    private static readonly Color DarkInputBackground = Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF);

    /// <summary>Заливка полей ввода в светлой теме.</summary>
    private static readonly Color LightInputBackground = Color.FromArgb(0x10, 0x00, 0x00, 0x00);

    /// <summary>Ползунок прокрутки в тёмной теме.</summary>
    private static readonly Color DarkScrollThumb = Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF);

    /// <summary>Ползунок прокрутки в светлой теме.</summary>
    private static readonly Color LightScrollThumb = Color.FromArgb(0x38, 0x00, 0x00, 0x00);

    /// <summary>Цвет текста на светлом акценте (чёрный).</summary>
    private static readonly Color LightAccentForeground = Color.FromRgb(0x12, 0x14, 0x18);

    /// <summary>Цвет текста на тёмном акценте (белый).</summary>
    private static readonly Color DarkAccentForeground = Color.FromRgb(0xFF, 0xFF, 0xFF);

    /// <summary>Порог относительной яркости, выше которого текст на акценте делается чёрным.</summary>
    private const double ContrastThreshold = 0.55;

    /// <summary>
    /// Определяет, использует ли Windows светлую тему для приложений.
    /// </summary>
    /// <returns><see langword="true"/> для светлой темы, <see langword="false"/> для тёмной.</returns>
    public static bool IsLightTheme()
    {
        string? forcedTheme = Environment.GetEnvironmentVariable(ThemeOverrideVariable);
        if (!string.IsNullOrWhiteSpace(forcedTheme))
        {
            bool forcedLight = forcedTheme.Trim().Equals("light", StringComparison.OrdinalIgnoreCase);
            Log.Information("Тема принудительно задана переменной {Variable}={Value}", ThemeOverrideVariable, forcedTheme);
            return forcedLight;
        }

        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(PersonalizeRegistryPath);
            object? value = key?.GetValue(AppsUseLightThemeValueName);
            if (value is int lightThemeFlag)
            {
                return lightThemeFlag != 0;
            }

            if (value is not null)
            {
                Log.Warning("Неожиданный тип значения {ValueName}: {Type}", AppsUseLightThemeValueName, value.GetType().Name);
            }
            else
            {
                Log.Information("Параметр {ValueName} не найден, используется тёмная тема", AppsUseLightThemeValueName);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Log.Warning(ex, "Не удалось прочитать тему из реестра, используется тёмная тема");
        }

        return false;
    }

    /// <summary>
    /// Создаёт набор цветов для текущей темы Windows.
    /// </summary>
    /// <param name="accentColor">Акцентный цвет из конфигурации.</param>
    /// <returns>Палитра текущей темы.</returns>
    public static ThemePalette CreatePalette(Color accentColor)
    {
        bool isLight = IsLightTheme();

        return new ThemePalette
        {
            IsLight = isLight,
            CardBackground = isLight ? LightCardBackground : DarkCardBackground,
            WindowBackground = isLight ? LightWindowBackground : DarkWindowBackground,
            InputBackground = isLight ? LightInputBackground : DarkInputBackground,
            CardBorder = isLight ? LightCardBorder : DarkCardBorder,
            PrimaryText = isLight ? LightPrimaryText : DarkPrimaryText,
            SecondaryText = isLight ? LightSecondaryText : DarkSecondaryText,
            Accent = accentColor,
            AccentForeground = GetContrastingForeground(accentColor),
            Sheen = isLight ? LightSheen : DarkSheen,
            Shadow = isLight ? LightShadow : DarkShadow,
            ScrollThumb = isLight ? LightScrollThumb : DarkScrollThumb
        };
    }

    /// <summary>
    /// Подбирает цвет текста поверх акцентного фона: чёрный на светлом, белый на тёмном.
    /// </summary>
    /// <param name="accent">Акцентный цвет фона.</param>
    /// <returns>Цвет текста с достаточным контрастом.</returns>
    /// <remarks>
    /// Используется относительная яркость по WCAG (0.2126·R + 0.7152·G + 0.0722·B).
    /// Порог 0.55 выбран так, чтобы средние и светлые оттенки (жёлтый, белый, салатовый)
    /// получали чёрный текст, а тёмные и насыщенные (синий, фиолетовый) — белый.
    /// </remarks>
    public static Color GetContrastingForeground(Color accent)
    {
        double luminance = ((0.2126 * accent.R) + (0.7152 * accent.G) + (0.0722 * accent.B)) / 255d;
        return luminance > ContrastThreshold ? LightAccentForeground : DarkAccentForeground;
    }

    /// <summary>
    /// Заполняет ресурсы приложения кистями и эффектами, соответствующими палитре.
    /// Кисти используются в XAML через <c>DynamicResource</c>, поэтому должны быть заморожены до публикации.
    /// </summary>
    /// <param name="resources">Словарь ресурсов приложения.</param>
    /// <param name="palette">Палитра темы.</param>
    public static void ApplyPalette(ResourceDictionary resources, ThemePalette palette)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(palette);

        resources[ThemeResourceKeys.CardBackground] = CreateBrush(palette.CardBackground);
        resources[ThemeResourceKeys.WindowBackground] = CreateBrush(palette.WindowBackground);
        resources[ThemeResourceKeys.InputBackground] = CreateBrush(palette.InputBackground);
        resources[ThemeResourceKeys.CardBorder] = CreateBrush(palette.CardBorder);
        resources[ThemeResourceKeys.PrimaryText] = CreateBrush(palette.PrimaryText);
        resources[ThemeResourceKeys.SecondaryText] = CreateBrush(palette.SecondaryText);
        resources[ThemeResourceKeys.Accent] = CreateBrush(palette.Accent);
        resources[ThemeResourceKeys.AccentForeground] = CreateBrush(palette.AccentForeground);
        resources[ThemeResourceKeys.GlassSheen] = CreateSheenBrush(palette.Sheen);
        resources[ThemeResourceKeys.ScrollThumb] = CreateBrush(palette.ScrollThumb);
        resources[ThemeResourceKeys.CardShadow] = CreateShadowEffect(palette);

        Log.Information("Применена палитра {Palette}", palette);
    }

    /// <summary>
    /// Создаёт замороженную кисть сплошного цвета.
    /// </summary>
    /// <param name="color">Цвет кисти.</param>
    /// <returns>Замороженная кисть.</returns>
    private static SolidColorBrush CreateBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Создаёт градиентную кисть «блика» стекла (сверху вниз, от светлого к прозрачному).
    /// </summary>
    /// <param name="sheenColor">Цвет блика.</param>
    /// <returns>Замороженная градиентная кисть.</returns>
    private static LinearGradientBrush CreateSheenBrush(Color sheenColor)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0.5, 0),
            EndPoint = new Point(0.5, 1)
        };

        brush.GradientStops.Add(new GradientStop(sheenColor, 0.0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, sheenColor.R, sheenColor.G, sheenColor.B), 1.0));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Создаёт мягкую тень под плашкой.
    /// </summary>
    /// <param name="palette">Палитра темы.</param>
    /// <returns>Замороженный эффект тени.</returns>
    private static DropShadowEffect CreateShadowEffect(ThemePalette palette)
    {
        var effect = new DropShadowEffect
        {
            Color = palette.Shadow,
            BlurRadius = 22,
            ShadowDepth = 4,
            Direction = 270,
            Opacity = palette.IsLight ? 0.18 : 0.55
        };

        effect.Freeze();
        return effect;
    }
}