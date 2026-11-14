using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using DowntimeGreeting.Common;
using Serilog;

namespace DowntimeGreeting.Models;

/// <summary>
/// Конфигурация приложения, читаемая из <c>appsettings.json</c> при старте.
/// Если файл отсутствует или содержит некорректные значения, используются значения по умолчанию.
/// </summary>
public sealed class AppConfig
{
    /// <summary>Значение <see cref="AutoCloseSeconds"/> по умолчанию.</summary>
    public const int DefaultAutoCloseSeconds = 5;

    /// <summary>Акцентный цвет по умолчанию (синий).</summary>
    public const string DefaultAccentColor = "#4D6BFE";

    /// <summary>Срок хранения лог-файлов по умолчанию (в днях).</summary>
    public const int DefaultLogRetentionDays = 7;

    /// <summary>Минимально допустимое время автозакрытия плашки, секунды.</summary>
    public const int MinimumAutoCloseSeconds = 5;

    /// <summary>Максимально допустимое время автозакрытия плашки, секунды.</summary>
    public const int MaximumAutoCloseSeconds = 15;

    private static readonly string[] FallbackPhrasesRu =
    [
        "С возвращением!",
        "Комп скучал",
        "Давно не виделись",
        "Ты вернулся, а я уже думал, что ты меня бросил",
        "Пыль успела осесть"
    ];

    private static readonly string[] FallbackPhrasesEn =
    [
        "Welcome back!",
        "The PC missed you",
        "Long time no see",
        "You're back — I thought you'd left me",
        "Dust had time to settle"
    ];

    private static readonly Color FallbackAccent = Color.FromRgb(0x4D, 0x6B, 0xFE);

    /// <summary>Список шутливых фраз по умолчанию (русский).</summary>
    public static IReadOnlyList<string> DefaultPhrases { get; } = FallbackPhrasesRu;

    /// <summary>Список шутливых фраз по умолчанию (английский).</summary>
    public static IReadOnlyList<string> DefaultPhrasesEn { get; } = FallbackPhrasesEn;

    /// <summary>Акцентный цвет по умолчанию в виде структуры <see cref="Color"/>.</summary>
    public static Color DefaultAccent { get; } = FallbackAccent;

    /// <summary>Время в секундах, через которое плашка закрывается автоматически.</summary>
    public int AutoCloseSeconds { get; init; } = DefaultAutoCloseSeconds;

    /// <summary>Набор шутливых фраз на русском языке.</summary>
    public IReadOnlyList<string> Phrases { get; init; } = DefaultPhrases;

    /// <summary>Набор шутливых фраз на английском языке.</summary>
    public IReadOnlyList<string> PhrasesEn { get; init; } = DefaultPhrasesEn;

    /// <summary>Код языка интерфейса (<c>ru</c> или <c>en</c>); пусто — по языку системы.</summary>
    public string Language { get; init; } = string.Empty;

    /// <summary>Акцентный цвет в формате <c>#RRGGBB</c> или <c>#AARRGGBB</c>.</summary>
    public string AccentColor { get; init; } = DefaultAccentColor;

    /// <summary>Сколько дней хранить лог-файлы.</summary>
    public int LogRetentionDays { get; init; } = DefaultLogRetentionDays;

    /// <summary>Акцентный цвет в виде структуры <see cref="Color"/>.</summary>
    public Color Accent { get; init; } = FallbackAccent;

    /// <summary>
    /// Путь к файлу конфигурации по умолчанию: <c>%LOCALAPPDATA%\DowntimeGreeting\appsettings.json</c>.
    /// </summary>
    public static string DefaultPath => AppPaths.GetSettingsPath();

    /// <summary>
    /// Загружает конфигурацию из JSON-файла профиля пользователя.
    /// Если файла нет, он создаётся с настройками по умолчанию.
    /// </summary>
    /// <param name="path">Путь к файлу; если <see langword="null"/>, используется <see cref="DefaultPath"/>.</param>
    /// <returns>Загруженная конфигурация; при любой проблеме — значения по умолчанию.</returns>
    public static AppConfig Load(string? path = null)
    {
        string filePath = path ?? DefaultPath;

        if (!File.Exists(filePath))
        {
            Log.Information("Файл конфигурации {Path} не найден, создаём со значениями по умолчанию", filePath);
            var defaults = new AppConfig();
            TrySave(defaults, out string? createError, filePath);
            if (createError is not null)
            {
                Log.Warning("Не удалось создать {Path}: {Error}. Используются значения по умолчанию", filePath, createError);
            }

            return defaults;
        }

        try
        {
            string json = File.ReadAllText(filePath);
            ConfigDto? dto = JsonSerializer.Deserialize<ConfigDto>(json, SerializerOptions);
            if (dto is null)
            {
                Log.Warning("Файл конфигурации {Path} пуст, используются значения по умолчанию", filePath);
                return new AppConfig();
            }

            return FromDto(dto);
        }
        catch (JsonException ex)
        {
            Log.Warning(ex, "Не удалось разобрать {Path}, используются значения по умолчанию", filePath);
            return new AppConfig();
        }
        catch (IOException ex)
        {
            Log.Warning(ex, "Не удалось прочитать {Path}, используются значения по умолчанию", filePath);
            return new AppConfig();
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Warning(ex, "Нет доступа к {Path}, используются значения по умолчанию", filePath);
            return new AppConfig();
        }
    }

    /// <summary>
    /// Преобразует цвет из строки <c>#RRGGBB</c>/<c>#AARRGGBB</c>; при ошибке возвращает <paramref name="fallback"/>.
    /// </summary>
    /// <param name="value">Строковое представление цвета.</param>
    /// <param name="fallback">Цвет, используемый при ошибке разбора.</param>
    /// <returns>Разобранный цвет либо <paramref name="fallback"/>.</returns>
    public static Color ParseColor(string? value, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        try
        {
            object? converted = ColorConverter.ConvertFromString(value.Trim());
            return converted is Color color ? color : fallback;
        }
        catch (FormatException)
        {
            return fallback;
        }
        catch (NotSupportedException)
        {
            return fallback;
        }
    }

    /// <summary>
    /// Сохраняет конфигурацию в JSON-файл (используется главным окном при изменении настроек).
    /// </summary>
    /// <param name="config">Сохраняемая конфигурация.</param>
    /// <param name="path">Путь к файлу; если <see langword="null"/>, используется <see cref="DefaultPath"/>.</param>
    /// <param name="error">Текст ошибки, если сохранение не удалось.</param>
    /// <returns><see langword="true"/>, если файл записан.</returns>
    /// <exception cref="ArgumentNullException">Конфигурация равна <see langword="null"/>.</exception>
    public static bool TrySave(AppConfig config, out string? error, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        string filePath = path ?? DefaultPath;
        var dto = new ConfigDto
        {
            AutoCloseSeconds = config.AutoCloseSeconds,
            Phrases = [.. config.Phrases],
            PhrasesEn = [.. config.PhrasesEn],
            Language = config.Language,
            AccentColor = config.AccentColor,
            LogRetentionDays = config.LogRetentionDays
        };

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath) ?? AppContext.BaseDirectory);
            string json = JsonSerializer.Serialize(dto, SaveOptions) + Environment.NewLine;
            File.WriteAllText(filePath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            error = null;
            Log.Information("Конфигурация сохранена в {Path}", filePath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            error = ex.Message;
            Log.Error(ex, "Не удалось сохранить конфигурацию в {Path}", filePath);
            return false;
        }
    }

    private static readonly JsonSerializerOptions SaveOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static AppConfig FromDto(ConfigDto dto)
    {
        int autoClose = dto.AutoCloseSeconds ?? DefaultAutoCloseSeconds;
        autoClose = Math.Clamp(autoClose, MinimumAutoCloseSeconds, MaximumAutoCloseSeconds);

        List<string> phrases = NormalizePhrases(dto.Phrases, FallbackPhrasesRu);
        List<string> phrasesEn = NormalizePhrases(dto.PhrasesEn, FallbackPhrasesEn);

        int retention = dto.LogRetentionDays ?? DefaultLogRetentionDays;
        retention = Math.Clamp(retention, 1, 365);

        string accentHex = string.IsNullOrWhiteSpace(dto.AccentColor) ? DefaultAccentColor : dto.AccentColor.Trim();
        string language = string.IsNullOrWhiteSpace(dto.Language) ? string.Empty : dto.Language.Trim().ToLowerInvariant();
        if (language is not ("ru" or "en"))
        {
            language = string.Empty;
        }

        return new AppConfig
        {
            AutoCloseSeconds = autoClose,
            Phrases = phrases,
            PhrasesEn = phrasesEn,
            Language = language,
            AccentColor = accentHex,
            Accent = ParseColor(accentHex, FallbackAccent),
            LogRetentionDays = retention
        };
    }

    /// <summary>
    /// Очищает список фраз: убирает пустые строки и пробелы по краям.
    /// </summary>
    /// <param name="source">Исходный список из JSON.</param>
    /// <param name="fallback">Значения по умолчанию, если после очистки ничего не осталось.</param>
    /// <returns>Нормализованный список фраз.</returns>
    private static List<string> NormalizePhrases(List<string>? source, IReadOnlyList<string> fallback)
    {
        List<string> phrases = source?
            .Where(static phrase => !string.IsNullOrWhiteSpace(phrase))
            .Select(static phrase => phrase.Trim())
            .ToList()
            ?? [];

        if (phrases.Count == 0)
        {
            phrases.AddRange(fallback);
        }

        return phrases;
    }

    /// <inheritdoc />
    public override string ToString() =>
        string.Format(
            CultureInfo.InvariantCulture,
            "AutoCloseSeconds={0}, AccentColor={1}, Language={2}, Phrases={3}, PhrasesEn={4}, LogRetentionDays={5}",
            AutoCloseSeconds,
            AccentColor,
            string.IsNullOrEmpty(Language) ? "auto" : Language,
            Phrases.Count,
            PhrasesEn.Count,
            LogRetentionDays);

    /// <summary>DTO для десериализации <c>appsettings.json</c>.</summary>
    private sealed class ConfigDto
    {
        public int? AutoCloseSeconds { get; set; }

        public List<string>? Phrases { get; set; }

        public List<string>? PhrasesEn { get; set; }

        public string? Language { get; set; }

        public string? AccentColor { get; set; }

        public int? LogRetentionDays { get; set; }
    }
}