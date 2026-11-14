namespace DowntimeGreeting.Common;

/// <summary>
/// Ключи ресурсов (кистей и эффектов), которые заполняются <c>ThemeService</c>
/// и используются в XAML через <c>DynamicResource</c>.
/// </summary>
public static class ThemeResourceKeys
{
    /// <summary>Фон плашки (полупрозрачное «стекло»).</summary>
    public const string CardBackground = "CardBackgroundBrush";

    /// <summary>Фон главного окна управления.</summary>
    public const string WindowBackground = "WindowBackgroundBrush";

    /// <summary>Фон полей ввода.</summary>
    public const string InputBackground = "InputBackgroundBrush";

    /// <summary>Кисть тонкой рамки плашки.</summary>
    public const string CardBorder = "CardBorderBrush";

    /// <summary>Кисть основного текста.</summary>
    public const string PrimaryText = "PrimaryTextBrush";

    /// <summary>Кисть второстепенного (серого) текста.</summary>
    public const string SecondaryText = "SecondaryTextBrush";

    /// <summary>Кисть акцентного цвета.</summary>
    public const string Accent = "AccentBrush";

    /// <summary>Кисть текста поверх акцентного цвета (белая или чёрная — по контрасту).</summary>
    public const string AccentForeground = "AccentForegroundBrush";

    /// <summary>Градиентная подсветка стекла (блик).</summary>
    public const string GlassSheen = "GlassSheenBrush";

    /// <summary>Мягкая тень под плашкой.</summary>
    public const string CardShadow = "CardShadowEffect";

    /// <summary>Ползунок полосы прокрутки.</summary>
    public const string ScrollThumb = "ScrollThumbBrush";
}