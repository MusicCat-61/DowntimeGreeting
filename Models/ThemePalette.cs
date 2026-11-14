using System.Windows.Media;

namespace DowntimeGreeting.Models;

/// <summary>
/// Набор цветов, соответствующий текущей теме Windows (светлая или тёмная).
/// </summary>
public sealed class ThemePalette
{
    /// <summary>Признак светлой темы Windows.</summary>
    public required bool IsLight { get; init; }

    /// <summary>Полупрозрачный фон плашки (стекло).</summary>
    public required Color CardBackground { get; init; }

    /// <summary>Почти непрозрачный фон главного окна управления.</summary>
    public required Color WindowBackground { get; init; }

    /// <summary>Фон полей ввода (полупрозрачная заливка).</summary>
    public required Color InputBackground { get; init; }

    /// <summary>Цвет тонкой рамки вокруг плашки.</summary>
    public required Color CardBorder { get; init; }

    /// <summary>Основной цвет текста.</summary>
    public required Color PrimaryText { get; init; }

    /// <summary>Второстепенный (серый) цвет текста.</summary>
    public required Color SecondaryText { get; init; }

    /// <summary>Акцентный цвет.</summary>
    public required Color Accent { get; init; }

    /// <summary>Цвет текста поверх акцентного фона: белый на тёмном акценте, чёрный на светлом.</summary>
    public required Color AccentForeground { get; init; }

    /// <summary>Цвет «блика» — градиентная подсветка стекла.</summary>
    public required Color Sheen { get; init; }

    /// <summary>Цвет мягкой тени под плашкой.</summary>
    public required Color Shadow { get; init; }

    /// <summary>Цвет ползунка полосы прокрутки.</summary>
    public required Color ScrollThumb { get; init; }

    /// <inheritdoc />
    public override string ToString() => $"Theme(IsLight={IsLight})";
}