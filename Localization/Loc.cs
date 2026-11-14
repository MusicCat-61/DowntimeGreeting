using System.Globalization;
using System.Windows;

namespace DowntimeGreeting.Localization;

/// <summary>Язык интерфейса приложения.</summary>
public enum AppLanguage
{
    /// <summary>Английский.</summary>
    En,

    /// <summary>Русский.</summary>
    Ru
}

/// <summary>
/// Переключение языковых словарей во время работы приложения.
/// </summary>
/// <remarks>
/// Тексты хранятся в <c>Localization/Strings.ru.xaml</c> и <c>Localization/Strings.en.xaml</c>
/// и подключаются как merged dictionary приложения. В XAML используется
/// <c>{DynamicResource L_Ключ}</c>, в коде — <see cref="T"/> и <see cref="F"/>.
/// </remarks>
public static class Loc
{
    /// <summary>Ключ настройки языка в конфигурации.</summary>
    public const string ConfigKey = "Language";

    private static readonly ResourceDictionary EnglishDictionary = Load("Strings.en.xaml");
    private static readonly ResourceDictionary RussianDictionary = Load("Strings.ru.xaml");

    private static ResourceDictionary? _active;

    /// <summary>Текущий язык интерфейса.</summary>
    public static AppLanguage Current { get; private set; } = AppLanguage.Ru;

    /// <summary>Код текущего языка: <c>ru</c> или <c>en</c>.</summary>
    public static string CurrentCode => Current == AppLanguage.Ru ? "ru" : "en";

    /// <summary>Событие смены языка: подписчики обновляют тексты, заданные из кода.</summary>
    public static event Action? LanguageChanged;

    /// <summary>
    /// Инициализирует локализацию: применяет сохранённый язык либо язык системы.
    /// </summary>
    /// <param name="savedCode">Код языка из конфигурации (<c>ru</c>, <c>en</c>) или <see langword="null"/>.</param>
    public static void Initialize(string? savedCode)
    {
        AppLanguage language = string.IsNullOrWhiteSpace(savedCode)
            ? DetectSystemLanguage()
            : Parse(savedCode);

        SetLanguage(language, notify: false);
    }

    /// <summary>
    /// Разбирает код языка; всё, что не <c>ru</c>, считается английским.
    /// </summary>
    /// <param name="value">Код языка.</param>
    /// <returns>Распознанный язык.</returns>
    public static AppLanguage Parse(string? value) =>
        string.Equals(value, "ru", StringComparison.OrdinalIgnoreCase) ? AppLanguage.Ru : AppLanguage.En;

    /// <summary>
    /// Определяет язык системы по текущей культуре интерфейса.
    /// </summary>
    /// <returns>Русский, если система русскоязычная, иначе английский.</returns>
    public static AppLanguage DetectSystemLanguage()
    {
        return IsRussian(CultureInfo.CurrentUICulture)
            || IsRussian(CultureInfo.InstalledUICulture)
            || IsRussian(CultureInfo.CurrentCulture)
                ? AppLanguage.Ru
                : AppLanguage.En;
    }

    /// <summary>
    /// Применяет язык: подменяет словарь ресурсов и культуру текущего потока.
    /// </summary>
    /// <param name="language">Новый язык.</param>
    /// <param name="notify">Уведомить подписчиков <see cref="LanguageChanged"/>.</param>
    public static void SetLanguage(AppLanguage language, bool notify = true)
    {
        Application? application = Application.Current;
        if (application is not null)
        {
            ResourceDictionary next = language == AppLanguage.Ru ? RussianDictionary : EnglishDictionary;
            if (!ReferenceEquals(next, _active))
            {
                if (_active is not null)
                {
                    application.Resources.MergedDictionaries.Remove(_active);
                }

                application.Resources.MergedDictionaries.Insert(0, next);
                _active = next;
            }
        }

        Current = language;

        CultureInfo culture = CultureInfo.GetCultureInfo(language == AppLanguage.Ru ? "ru-RU" : "en-US");
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;

        if (notify)
        {
            LanguageChanged?.Invoke();
        }
    }

    /// <summary>
    /// Возвращает локализованную строку по ключу.
    /// </summary>
    /// <param name="key">Ключ ресурса (например, <c>L_SaveSettings</c>).</param>
    /// <returns>Текст на текущем языке; если ключа нет — сам ключ.</returns>
    public static string T(string key) =>
        Application.Current?.TryFindResource(key) as string ?? key;

    /// <summary>
    /// Возвращает локализованную строку с подстановкой аргументов.
    /// </summary>
    /// <param name="key">Ключ ресурса с плейсхолдерами <c>{0}</c>, <c>{1}</c>…</param>
    /// <param name="arguments">Аргументы подстановки.</param>
    /// <returns>Отформатированный текст.</returns>
    public static string F(string key, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, T(key), arguments);

    private static bool IsRussian(CultureInfo culture) =>
        culture.TwoLetterISOLanguageName.Equals("ru", StringComparison.OrdinalIgnoreCase);

    private static ResourceDictionary Load(string fileName)
    {
        string assembly = typeof(Loc).Assembly.GetName().Name!;
        return new ResourceDictionary
        {
            Source = new Uri(
                $"pack://application:,,,/{assembly};component/Localization/{fileName}",
                UriKind.Absolute)
        };
    }
}