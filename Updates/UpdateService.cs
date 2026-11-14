using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using DowntimeGreeting.Common;
using DowntimeGreeting.Localization;
using Serilog;

namespace DowntimeGreeting.Updates;

/// <summary>Статус проверки обновлений.</summary>
public enum UpdateStatus
{
    /// <summary>Проверка ещё не выполнялась.</summary>
    Idle,

    /// <summary>Установлена последняя версия.</summary>
    UpToDate,

    /// <summary>Доступна новая версия.</summary>
    UpdateAvailable,

    /// <summary>Релизов ещё нет.</summary>
    NoReleases,

    /// <summary>Не удалось проверить (нет сети, ошибка API).</summary>
    Failed
}

/// <summary>Результат проверки обновлений.</summary>
/// <param name="Status">Статус проверки.</param>
/// <param name="Latest">Найденная последняя версия, если есть.</param>
/// <param name="ReleaseUrl">Ссылка на страницу релиза.</param>
/// <param name="Error">Текст ошибки для статуса <see cref="UpdateStatus.Failed"/>.</param>
public sealed record UpdateCheckResult(
    UpdateStatus Status,
    ReleaseVersion? Latest = null,
    string? ReleaseUrl = null,
    string? Error = null);

/// <summary>
/// Проверяет наличие новых версий через GitHub Releases API.
/// </summary>
/// <remarks>
/// Запрос выполняется только по явной команде пользователя (кнопка «Проверить обновления»):
/// приложение не обращается к сети при запуске и не имеет фоновых проверок.
/// Скачивание и установка не выполняются — пользователь переходит на страницу релиза.
/// </remarks>
public sealed class UpdateService
{
    /// <summary>Адрес GitHub API для последнего релиза.</summary>
    private static readonly Uri LatestReleaseUri = new(
        $"https://api.github.com/repos/{AppInfo.RepositoryOwner}/{AppInfo.RepositoryName}/releases/latest");

    /// <summary>Максимальное время ожидания ответа GitHub.</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _httpClient;

    /// <summary>
    /// Инициализирует новый экземпляр <see cref="UpdateService"/>.
    /// </summary>
    public UpdateService()
    {
        _httpClient = new HttpClient { Timeout = RequestTimeout };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd($"{AppInfo.Name}/1.0");
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    /// <summary>Текущая версия приложения.</summary>
    public static ReleaseVersion CurrentVersion
    {
        get
        {
            var assembly = typeof(UpdateService).Assembly;
            string? informational = assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                .FirstOrDefault()?.InformationalVersion;

            if (ReleaseVersion.TryParse(informational, out ReleaseVersion parsed))
            {
                return parsed;
            }

            Version? version = assembly.GetName().Version;
            return version is null
                ? new ReleaseVersion()
                : ReleaseVersion.TryParse(version.ToString(3), out ReleaseVersion fallback)
                    ? fallback
                    : new ReleaseVersion();
        }
    }

    /// <summary>
    /// Проверяет наличие новой версии на GitHub.
    /// </summary>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Результат проверки; исключения не выбрасываются.</returns>
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using HttpResponseMessage response = await _httpClient
                .GetAsync(LatestReleaseUri, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                Log.Information("Релизов в репозитории пока нет");
                return new UpdateCheckResult(UpdateStatus.NoReleases);
            }

            if (!response.IsSuccessStatusCode)
            {
                Log.Warning("GitHub API вернул код {Code}", (int)response.StatusCode);
                return new UpdateCheckResult(
                    UpdateStatus.Failed,
                    Error: Loc.F("L_UpdHttpErrorFormat", (int)response.StatusCode));
            }

            ReleaseDto? release = await response.Content
                .ReadFromJsonAsync<ReleaseDto>(cancellationToken)
                .ConfigureAwait(false);

            if (release is null || string.IsNullOrWhiteSpace(release.TagName))
            {
                return new UpdateCheckResult(UpdateStatus.Failed, Error: Loc.T("L_UpdBadResponse"));
            }

            string tag = release.TagName;
            string releaseUrl = string.IsNullOrWhiteSpace(release.HtmlUrl) ? AppInfo.ReleasesUrl : release.HtmlUrl;

            if (!ReleaseVersion.TryParse(tag, out ReleaseVersion latest))
            {
                Log.Warning("Не удалось разобрать тег релиза {Tag}", tag);
                return new UpdateCheckResult(UpdateStatus.Failed, Error: Loc.F("L_UpdBadTagFormat", tag));
            }

            ReleaseVersion current = CurrentVersion;
            Log.Information("Проверка обновлений: текущая {Current}, последняя {Latest}", current, latest);

            return latest > current
                ? new UpdateCheckResult(UpdateStatus.UpdateAvailable, latest, releaseUrl)
                : new UpdateCheckResult(UpdateStatus.UpToDate, latest, releaseUrl);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Log.Warning("Проверка обновлений прервана по таймауту");
            return new UpdateCheckResult(UpdateStatus.Failed, Error: Loc.T("L_UpdTimeout"));
        }
        catch (HttpRequestException ex)
        {
            Log.Warning(ex, "Не удалось проверить обновления: сеть недоступна");
            return new UpdateCheckResult(UpdateStatus.Failed, Error: Loc.T("L_UpdNoNetwork"));
        }
        catch (Exception ex) when (ex is TaskCanceledException or InvalidOperationException or System.Text.Json.JsonException)
        {
            Log.Warning(ex, "Ошибка при проверке обновлений");
            return new UpdateCheckResult(UpdateStatus.Failed, Error: Loc.T("L_UpdFailed"));
        }
    }

    /// <summary>
    /// Открывает страницу релиза в браузере.
    /// </summary>
    /// <param name="url">Адрес страницы; при пустом значении — общая страница релизов.</param>
    public static void OpenReleasePage(string? url)
    {
        string target = string.IsNullOrWhiteSpace(url) ? AppInfo.ReleasesUrl : url;
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error(ex, "Не удалось открыть ссылку {Url}", target);
        }
    }

    /// <summary>Ответ GitHub API с нужными полями релиза.</summary>
    private sealed class ReleaseDto
    {
        /// <summary>Тег релиза, например <c>v1.2.3</c>.</summary>
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        /// <summary>Ссылка на страницу релиза.</summary>
        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }
    }
}