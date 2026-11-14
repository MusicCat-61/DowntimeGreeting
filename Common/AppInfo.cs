namespace DowntimeGreeting.Common;

/// <summary>
/// Сведения о приложении и внешние ссылки, используемые в интерфейсе и системе обновлений.
/// </summary>
public static class AppInfo
{
    /// <summary>Название приложения.</summary>
    public const string Name = "DowntimeGreeting";

    /// <summary>Репозиторий проекта на GitHub.</summary>
    public const string RepositoryUrl = "https://github.com/MusicCat-61/DowntimeGreeting";

    /// <summary>Владелец репозитория (для GitHub API).</summary>
    public const string RepositoryOwner = "MusicCat-61";

    /// <summary>Имя репозитория (для GitHub API).</summary>
    public const string RepositoryName = "DowntimeGreeting";

    /// <summary>Telegram-канал автора.</summary>
    public const string TelegramUrl = "https://t.me/musiccat61dev";

    /// <summary>Ссылка для поддержки проекта.</summary>
    public const string DonateUrl = "https://dalink.to/musiccat61";

    /// <summary>Адрес страницы релизов (запасной вариант, если API недоступен).</summary>
    public const string ReleasesUrl = RepositoryUrl + "/releases";
}