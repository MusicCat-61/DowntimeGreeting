using System.IO;

namespace DowntimeGreeting.Common;

/// <summary>
/// Пути приложения в профиле пользователя: настройки и журналы хранятся в
/// <c>%LOCALAPPDATA%\DowntimeGreeting</c>, чтобы приложение работало из любого каталога
/// (в том числе из <c>Program Files</c>, куда писать нельзя).
/// </summary>
public static class AppPaths
{
    /// <summary>Имя каталога приложения в <c>%LOCALAPPDATA%</c>.</summary>
    public const string ApplicationFolderName = "DowntimeGreeting";

    /// <summary>Имя подкаталога с журналами.</summary>
    public const string LogFolderName = "logs";

    /// <summary>Имя файла настроек.</summary>
    public const string SettingsFileName = "appsettings.json";

    /// <summary>
    /// Возвращает корневой каталог данных приложения.
    /// </summary>
    /// <returns>Путь вида <c>C:\Users\Пользователь\AppData\Local\DowntimeGreeting</c>.</returns>
    public static string GetApplicationDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ApplicationFolderName);

    /// <summary>
    /// Возвращает каталог журналов приложения.
    /// </summary>
    /// <returns>Путь вида <c>...\DowntimeGreeting\logs</c>.</returns>
    public static string GetLogDirectory() => Path.Combine(GetApplicationDirectory(), LogFolderName);

    /// <summary>
    /// Возвращает путь к файлу настроек пользователя.
    /// </summary>
    /// <returns>Путь вида <c>...\DowntimeGreeting\appsettings.json</c>.</returns>
    public static string GetSettingsPath() => Path.Combine(GetApplicationDirectory(), SettingsFileName);
}