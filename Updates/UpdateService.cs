using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DowntimeGreeting.Common;
using DowntimeGreeting.Localization;
using Serilog;

namespace DowntimeGreeting.Updates;

/// <summary>Статус проверки обновлений.</summary>
public enum UpdateStatus
{
    /// <summary>Проверка ещё не выполнялась.</summary>
    Unknown,

    /// <summary>Установлена последняя версия.</summary>
    UpToDate,

    /// <summary>Доступна новая версия с файлом для скачивания.</summary>
    UpdateAvailable,

    /// <summary>Новая версия есть, но в релизе нет exe.</summary>
    NoDownload,

    /// <summary>Релизов ещё нет.</summary>
    NoReleases,

    /// <summary>Не удалось проверить (нет сети, ошибка API).</summary>
    Failed
}

/// <summary>Результат проверки обновлений.</summary>
public sealed record UpdateCheckResult
{
    /// <summary>Статус проверки.</summary>
    public UpdateStatus Status { get; init; } = UpdateStatus.Unknown;

    /// <summary>Текущая версия приложения.</summary>
    public ReleaseVersion Current { get; init; } = UpdateService.CurrentVersion;

    /// <summary>Найденная последняя версия, если есть.</summary>
    public ReleaseVersion? Latest { get; init; }

    /// <summary>Тег релиза, например <c>v1.1.0</c>.</summary>
    public string? Tag { get; init; }

    /// <summary>Ссылка на скачивание exe.</summary>
    public string? DownloadUrl { get; init; }

    /// <summary>Ссылка на файл контрольной суммы (может отсутствовать).</summary>
    public string? ChecksumUrl { get; init; }

    /// <summary>Ссылка на страницу релиза.</summary>
    public string? ReleaseUrl { get; init; }

    /// <summary>Размер exe в байтах, если известен.</summary>
    public long AssetSize { get; init; }

    /// <summary>Текст ошибки для статусов <see cref="UpdateStatus.Failed"/> и <see cref="UpdateStatus.NoDownload"/>.</summary>
    public string? Error { get; init; }

    /// <summary>Заметки к релизу (тело релиза на GitHub).</summary>
    public string? Notes { get; init; }

    /// <summary>Признак доступного обновления.</summary>
    public bool HasUpdate => Status == UpdateStatus.UpdateAvailable;
}

/// <summary>Скачанный и проверенный файл обновления, ожидающий подмены exe.</summary>
/// <param name="Version">Версия скачанной сборки.</param>
/// <param name="StagedFile">Путь к скачанному файлу.</param>
/// <param name="Size">Размер файла в байтах.</param>
public sealed record PreparedUpdate(string Version, string StagedFile, long Size);

/// <summary>Итог подмены исполняемого файла.</summary>
/// <param name="Succeeded">Подмена выполнена.</param>
/// <param name="Detail">Пояснение из отчёта скрипта.</param>
public sealed record UpdateApplyReport(bool Succeeded, string Detail);

/// <summary>
/// Проверка, загрузка и установка обновлений из GitHub Releases.
/// </summary>
/// <remarks>
/// Проверка выполняется при запуске приложения в фоновом режиме, но не чаще заданного интервала:
/// время последней проверки хранится в <c>%LOCALAPPDATA%\DowntimeGreeting\update-state.json</c>,
/// поэтому частые перезапуски не создают лишних запросов к GitHub. Сетевые запросы никогда
/// не блокируют показ плашки: режим <c>--greet</c> вообще не обращается к сети.
/// Обновление ставится подменой одного exe: приложение — единый self-contained файл.
/// </remarks>
public static class UpdateService
{
    /// <summary>Репозиторий с релизами.</summary>
    public const string Repository = AppInfo.RepositoryOwner + "/" + AppInfo.RepositoryName;

    /// <summary>Имя файла релиза: приложение публикуется одним exe.</summary>
    public const string AssetFileName = "DowntimeGreeting.exe";

    /// <summary>Имя файла контрольной суммы в релизе (необязателен).</summary>
    public const string ChecksumFileName = "DowntimeGreeting.exe.sha256";

    /// <summary>Имя, под которым скачанное обновление ждёт подмены.</summary>
    public const string StagedFileName = "DowntimeGreeting.update.exe";

    /// <summary>Настоящая сборка занимает десятки мегабайт; меньше — страница ошибки или заглушка.</summary>
    private const long MinimumAssetBytes = 1024 * 1024;

    private static readonly Uri LatestReleaseUri = new(
        $"https://api.github.com/repos/{Repository}/releases/latest");

    private static readonly SemaphoreSlim CheckGate = new(1, 1);
    private static readonly SemaphoreSlim DownloadGate = new(1, 1);

    private static readonly object ClientGate = new();

    private static HttpClient? _releaseClient;
    private static HttpClient? _downloadClient;
    private static ReleaseVersion? _currentVersion;

    /// <summary>Текущая версия приложения.</summary>
    public static ReleaseVersion CurrentVersion => _currentVersion ??= ReadCurrentVersion();

    /// <summary>Результат последней проверки в этом сеансе.</summary>
    public static UpdateCheckResult? LastResult { get; private set; }

    /// <summary>Момент последней проверки (UTC), включая сохранённый с прошлых запусков.</summary>
    public static DateTimeOffset? LastCheckUtc { get; private set; }

    /// <summary>Событие завершения проверки: подписчики обновляют интерфейс.</summary>
    public static event EventHandler<UpdateCheckResult>? Checked;

    /// <summary>Путь к запущенному исполняемому файлу.</summary>
    public static string ExecutablePath =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, AssetFileName);

    /// <summary>Каталог, в котором лежит исполняемый файл.</summary>
    public static string ExecutableDirectory =>
        Path.GetDirectoryName(ExecutablePath) ?? AppContext.BaseDirectory;

    /// <summary>Полный путь, под которым ждёт скачанное обновление.</summary>
    public static string StagedFilePath => Path.Combine(ExecutableDirectory, StagedFileName);

    private static HttpClient ReleaseClient
    {
        get
        {
            lock (ClientGate)
            {
                return _releaseClient ??= CreateClient(allowRedirects: false);
            }
        }
    }

    private static HttpClient DownloadClient
    {
        get
        {
            lock (ClientGate)
            {
                return _downloadClient ??= CreateClient(allowRedirects: true);
            }
        }
    }

    /// <summary>
    /// Загружает сохранённое время последней проверки, чтобы не обращаться к сети при каждом запуске.
    /// </summary>
    public static void LoadState()
    {
        try
        {
            string path = AppPaths.GetUpdateStatePath();
            if (!File.Exists(path))
            {
                return;
            }

            UpdateState? state = JsonSerializer.Deserialize<UpdateState>(File.ReadAllText(path));
            if (state?.LastCheckUtc is { } saved && saved <= DateTimeOffset.UtcNow.AddDays(1))
            {
                LastCheckUtc = saved;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warning(ex, "Не удалось прочитать состояние проверки обновлений");
        }
    }

    /// <summary>
    /// Проверяет, пора ли выполнять очередную проверку.
    /// </summary>
    /// <param name="normalInterval">Интервал между успешными проверками.</param>
    /// <param name="retryAfterFailure">Интервал повторной попытки после ошибки.</param>
    /// <returns><see langword="true"/>, если проверку нужно выполнить.</returns>
    public static bool IsCheckDue(TimeSpan normalInterval, TimeSpan retryAfterFailure)
    {
        if (LastCheckUtc is not { } checkedAt)
        {
            return true;
        }

        TimeSpan interval = LastResult?.Status == UpdateStatus.Failed ? retryAfterFailure : normalInterval;
        return DateTimeOffset.UtcNow - checkedAt >= interval;
    }

    /// <summary>
    /// Проверяет наличие новой версии на GitHub.
    /// </summary>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Результат проверки; сетевые ошибки не выбрасываются наружу.</returns>
    public static async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        await CheckGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            UpdateCheckResult result = await CheckCoreAsync(cancellationToken).ConfigureAwait(false);

            LastResult = result;
            LastCheckUtc = DateTimeOffset.UtcNow;
            SaveState(LastCheckUtc.Value);

            Log.Information("Обновления: {State}", Describe(result));
            Checked?.Invoke(null, result);
            return result;
        }
        finally
        {
            CheckGate.Release();
        }
    }

    private static async Task<UpdateCheckResult> CheckCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUri);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");

            using HttpResponseMessage response = await ReleaseClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new UpdateCheckResult { Status = UpdateStatus.NoReleases, Current = CurrentVersion };
            }

            if (!response.IsSuccessStatusCode)
            {
                return Failed(Loc.F("L_UpdHttpErrorFormat", (int)response.StatusCode));
            }

            string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ParseRelease(json, CurrentVersion);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Не удалось проверить обновления");
            return Failed(ex is HttpRequestException
                ? Loc.T("L_UpdNoNetwork")
                : Loc.T("L_UpdFailed"));
        }
    }

    /// <summary>
    /// Разбирает ответ GitHub API о последнем релизе.
    /// </summary>
    /// <param name="json">Тело ответа.</param>
    /// <param name="current">Текущая версия приложения.</param>
    /// <returns>Результат разбора.</returns>
    public static UpdateCheckResult ParseRelease(string json, ReleaseVersion current)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement release = document.RootElement;

        // Черновики и предрелизы не предлагаются: они не являются стабильной версией.
        if (release.TryGetProperty("draft", out JsonElement draft) && draft.ValueKind == JsonValueKind.True)
        {
            return new UpdateCheckResult { Status = UpdateStatus.NoReleases, Current = current };
        }

        if (release.TryGetProperty("prerelease", out JsonElement prerelease)
            && prerelease.ValueKind == JsonValueKind.True)
        {
            return new UpdateCheckResult { Status = UpdateStatus.NoReleases, Current = current };
        }

        string tag = (release.TryGetProperty("tag_name", out JsonElement tagValue) ? tagValue.GetString() : null)
            ?? string.Empty;

        if (!ReleaseVersion.TryParse(tag, out ReleaseVersion latest))
        {
            return Failed(Loc.F("L_UpdBadTagFormat", tag.Length == 0 ? "?" : tag));
        }

        string releaseUrl = $"https://github.com/{Repository}/releases/tag/{Uri.EscapeDataString(tag)}";
        string? notes = release.TryGetProperty("body", out JsonElement bodyValue) ? bodyValue.GetString() : null;

        if (latest <= current)
        {
            return new UpdateCheckResult
            {
                Status = UpdateStatus.UpToDate,
                Current = current,
                Latest = latest,
                Tag = tag,
                ReleaseUrl = releaseUrl,
                Notes = notes
            };
        }

        string? downloadUrl = null;
        string? checksumUrl = null;
        long assetSize = 0;

        if (release.TryGetProperty("assets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement asset in assets.EnumerateArray())
            {
                if (!asset.TryGetProperty("name", out JsonElement nameValue)
                    || !asset.TryGetProperty("browser_download_url", out JsonElement urlValue))
                {
                    continue;
                }

                string? name = nameValue.GetString();
                string? url = urlValue.GetString();
                if (!IsRepositoryAssetUrl(url))
                {
                    continue;
                }

                if (string.Equals(name, AssetFileName, StringComparison.OrdinalIgnoreCase))
                {
                    downloadUrl = url;
                    if (asset.TryGetProperty("size", out JsonElement sizeValue))
                    {
                        sizeValue.TryGetInt64(out assetSize);
                    }
                }
                else if (string.Equals(name, ChecksumFileName, StringComparison.OrdinalIgnoreCase))
                {
                    checksumUrl = url;
                }
            }
        }

        if (downloadUrl is null)
        {
            return new UpdateCheckResult
            {
                Status = UpdateStatus.NoDownload,
                Current = current,
                Latest = latest,
                Tag = tag,
                ReleaseUrl = releaseUrl,
                Notes = notes,
                Error = Loc.F("L_UpdNoAssetFormat", AssetFileName)
            };
        }

        return new UpdateCheckResult
        {
            Status = UpdateStatus.UpdateAvailable,
            Current = current,
            Latest = latest,
            Tag = tag,
            ReleaseUrl = releaseUrl,
            DownloadUrl = downloadUrl,
            ChecksumUrl = checksumUrl,
            AssetSize = assetSize,
            Notes = notes
        };
    }

    /// <summary>
    /// Проверяет, что ссылка ведёт на файл этого репозитория (защита от подмены ответа API).
    /// </summary>
    /// <param name="url">Адрес файла.</param>
    /// <returns><see langword="true"/>, если адрес допустим.</returns>
    private static bool IsRepositoryAssetUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? address)
        && address.Scheme == Uri.UriSchemeHttps
        && address.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        && address.AbsolutePath.StartsWith(
            $"/{Repository}/releases/download/",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Скачивает новую сборку рядом с текущей и проверяет её.
    /// </summary>
    /// <param name="result">Результат проверки со ссылкой на файл.</param>
    /// <param name="progress">Приёмник прогресса (0…1).</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Скачанный и проверенный файл.</returns>
    /// <exception cref="InvalidOperationException">Обновление недоступно для скачивания.</exception>
    /// <exception cref="InvalidDataException">Файл не прошёл проверку.</exception>
    /// <exception cref="IOException">Не удалось записать файл.</exception>
    public static async Task<PreparedUpdate> DownloadAndPrepareAsync(
        UpdateCheckResult result,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!result.HasUpdate || string.IsNullOrWhiteSpace(result.DownloadUrl) || result.Latest is null)
        {
            throw new InvalidOperationException(Loc.T("L_UpdNotDownloadable"));
        }

        await DownloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!CanWriteToInstallFolder())
            {
                throw new IOException(Loc.F("L_UpdFolderNotWritable", ExecutableDirectory));
            }

            string? expectedHash = string.IsNullOrWhiteSpace(result.ChecksumUrl)
                ? null
                : await DownloadChecksumAsync(result.ChecksumUrl, cancellationToken).ConfigureAwait(false);

            string staged = StagedFilePath;
            string partial = staged + "." + result.Latest + ".part";
            DeleteStalePartials(staged, partial);

            await DownloadFileAsync(result.DownloadUrl, partial, result.AssetSize, progress, cancellationToken)
                .ConfigureAwait(false);

            if (expectedHash is not null)
            {
                string actualHash = await ComputeSha256Async(partial, cancellationToken).ConfigureAwait(false);
                if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(partial);
                    throw new InvalidDataException(Loc.T("L_UpdChecksumMismatch"));
                }
            }

            progress?.Report(0.95);
            long size = ValidateDownloadedExecutable(partial, result.AssetSize, CurrentVersion);
            File.Move(partial, staged, overwrite: true);
            progress?.Report(1);

            Log.Information(
                "Обновление {Version} скачано и проверено ({Size} байт): {Path}",
                result.Latest,
                size,
                staged);

            return new PreparedUpdate(result.Latest.Value.ToString(), staged, size);
        }
        finally
        {
            DownloadGate.Release();
        }
    }

    /// <summary>
    /// Отбрасывает обрезанный файл, страницу ошибки, сохранённую как «.exe», и сборку не новее текущей.
    /// </summary>
    /// <param name="path">Путь к скачанному файлу.</param>
    /// <param name="expectedSize">Ожидаемый размер (0, если неизвестен).</param>
    /// <param name="current">Текущая версия.</param>
    /// <returns>Размер проверенного файла.</returns>
    /// <exception cref="InvalidDataException">Файл не является подходящей сборкой.</exception>
    private static long ValidateDownloadedExecutable(string path, long expectedSize, ReleaseVersion current)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < MinimumAssetBytes)
        {
            throw new InvalidDataException(Loc.F("L_UpdTooSmallFormat", info.Length));
        }

        if (expectedSize > 0 && info.Length != expectedSize)
        {
            throw new InvalidDataException(Loc.F("L_UpdSizeMismatchFormat", info.Length, expectedSize));
        }

        // Портативный exe начинается с «MZ»; HTML или JSON-страница ошибки — нет.
        Span<byte> header = stackalloc byte[2];
        using (FileStream stream = File.OpenRead(path))
        {
            if (stream.Read(header) != 2 || header[0] != (byte)'M' || header[1] != (byte)'Z')
            {
                throw new InvalidDataException(Loc.T("L_UpdNotExecutable"));
            }
        }

        string? text = FileVersionInfo.GetVersionInfo(path).FileVersion;
        if (ReleaseVersion.TryParse(text, out ReleaseVersion version) && version < current)
        {
            throw new InvalidDataException(Loc.F("L_UpdOlderFormat", version, current));
        }

        return info.Length;
    }

    /// <summary>
    /// Находит уже скачанное обновление, если оно новее текущей версии.
    /// </summary>
    /// <returns>Скачанный файл либо <see langword="null"/>.</returns>
    public static PreparedUpdate? FindPreparedUpdate()
    {
        try
        {
            string staged = StagedFilePath;
            if (!File.Exists(staged))
            {
                return null;
            }

            long size = new FileInfo(staged).Length;
            if (size < MinimumAssetBytes || IsNotNewer(staged))
            {
                Log.Information("Удаляем устаревший скачанный файл обновления: {Path}", staged);
                TryDelete(staged);
                return null;
            }

            string? text = FileVersionInfo.GetVersionInfo(staged).FileVersion;
            string version = ReleaseVersion.TryParse(text, out ReleaseVersion parsed) ? parsed.ToString() : "?";
            return new PreparedUpdate(version, staged, size);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error(ex, "Не удалось проверить скачанное обновление");
            return null;
        }
    }

    /// <summary>
    /// Проверяет, что скачанный файл не старше установленного.
    /// </summary>
    /// <param name="staged">Путь к скачанному файлу.</param>
    /// <returns><see langword="true"/>, если файл не новее текущей сборки.</returns>
    private static bool IsNotNewer(string staged)
    {
        // Файл без читаемой версии оставляем: отказать в настоящем обновлении хуже, чем предложить дважды.
        string? text = FileVersionInfo.GetVersionInfo(staged).FileVersion;
        return ReleaseVersion.TryParse(text, out ReleaseVersion stagedVersion) && stagedVersion <= CurrentVersion;
    }

    /// <summary>
    /// Проверяет, можно ли писать в каталог приложения.
    /// </summary>
    /// <returns><see langword="true"/>, если каталог доступен для записи.</returns>
    public static bool CanWriteToInstallFolder()
    {
        try
        {
            string probe = Path.Combine(ExecutableDirectory, $".dg-write-{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Передаёт скачанный файл отдельному скрипту, который подменит exe после выхода приложения.
    /// </summary>
    /// <param name="prepared">Скачанный файл.</param>
    /// <param name="error">Текст ошибки, если запуск не удался.</param>
    /// <returns><see langword="true"/>, если скрипт запущен.</returns>
    public static bool TryLaunchApplyScript(PreparedUpdate prepared, out string error)
    {
        error = string.Empty;
        try
        {
            if (!File.Exists(prepared.StagedFile))
            {
                error = Loc.T("L_UpdStagedGone");
                return false;
            }

            if (new FileInfo(prepared.StagedFile).Length != prepared.Size)
            {
                error = Loc.T("L_UpdStagedChanged");
                return false;
            }

            if (!CanWriteToInstallFolder())
            {
                error = Loc.F("L_UpdFolderNotWritable", ExecutableDirectory);
                return false;
            }

            Directory.CreateDirectory(AppPaths.GetApplicationDirectory());

            // Скрипт — чистый ASCII и берёт все пути из переменных окружения: cmd.exe читает .cmd
            // в OEM-кодировке, и путь вида «C:\Users\Маksim\…» был бы испорчен.
            string scriptPath = AppPaths.GetApplyScriptPath();
            File.WriteAllLines(scriptPath, ApplyScriptLines, Encoding.ASCII);

            string logPath = AppPaths.GetApplyLogPath();
            TryDelete(logPath);

            var startInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"\"{scriptPath}\"\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            startInfo.Environment["DG_STAGED"] = prepared.StagedFile;
            startInfo.Environment["DG_TARGET"] = ExecutablePath;
            startInfo.Environment["DG_LOG"] = logPath;
            startInfo.Environment["DG_PID"] = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
            startInfo.Environment["DG_EXPECT"] = prepared.Size.ToString(CultureInfo.InvariantCulture);

            Process.Start(startInfo);
            Log.Information("Запущен отдельный процесс установки обновления {Version}", prepared.Version);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            error = ex.Message;
            Log.Error(ex, "Не удалось запустить установку обновления");
            return false;
        }
    }

    /// <summary>
    /// Читает и удаляет отчёт предыдущей попытки установки.
    /// </summary>
    /// <returns>Отчёт либо <see langword="null"/>, если скрипт не дошёл до записи.</returns>
    public static UpdateApplyReport? ConsumeLastApplyReport()
    {
        string logPath = AppPaths.GetApplyLogPath();
        string scriptPath = AppPaths.GetApplyScriptPath();

        try
        {
            if (!File.Exists(logPath))
            {
                return null;
            }

            string? resultLine = File.ReadLines(logPath)
                .LastOrDefault(static line => line.Contains("RESULT=", StringComparison.Ordinal));

            if (resultLine is null)
            {
                return null;
            }

            int index = resultLine.IndexOf("RESULT=", StringComparison.Ordinal) + 7;
            string result = resultLine[index..].Trim();

            var report = new UpdateApplyReport(
                result.StartsWith("OK", StringComparison.OrdinalIgnoreCase),
                result);

            Log.Information("Результат установки обновления: {Detail}", report.Detail);
            return report;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error(ex, "Не удалось прочитать отчёт об установке обновления");
            return null;
        }
        finally
        {
            TryDelete(logPath);
            TryDelete(scriptPath);
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

    /// <summary>
    /// Возвращает текущую версию в виде строки для интерфейса.
    /// </summary>
    /// <returns>Строка вида <c>1.0.0</c>.</returns>
    public static string CurrentVersionText => CurrentVersion.ToString();

    private static async Task<string> DownloadChecksumAsync(string url, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await DownloadClient
            .GetAsync(url, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        string? hash = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

        if (hash is null || hash.Length != 64 || !hash.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException(Loc.T("L_UpdBadChecksumFile"));
        }

        return hash;
    }

    /// <summary>
    /// Скачивает файл, продолжая прерванную загрузку с того же места.
    /// </summary>
    /// <param name="url">Адрес файла.</param>
    /// <param name="targetPath">Путь назначения.</param>
    /// <param name="expectedLength">Ожидаемый размер (0, если неизвестен).</param>
    /// <param name="progress">Приёмник прогресса.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Задача, представляющая асинхронную операцию.</returns>
    private static async Task DownloadFileAsync(
        string url,
        string targetPath,
        long expectedLength,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        long alreadyHave = ExistingBytes(targetPath);

        // Полный файл не требует сети: остаётся только пройти проверки выше.
        if (expectedLength > 0 && alreadyHave == expectedLength)
        {
            progress?.Report(0.9);
            return;
        }

        // Частичный файл больше самого ассета — мусор от другой сборки, а не продолжение.
        if (expectedLength > 0 && alreadyHave > expectedLength)
        {
            TryDelete(targetPath);
            alreadyHave = 0;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (alreadyHave > 0)
        {
            request.Headers.Range = new RangeHeaderValue(alreadyHave, null);
        }

        using HttpResponseMessage response = await DownloadClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        // Сервер без поддержки Range отвечает 200 и целиком — загрузка начинается заново.
        bool resumed = alreadyHave > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (!resumed)
        {
            alreadyHave = 0;
        }

        long totalLength = resumed
            ? alreadyHave + (response.Content.Headers.ContentLength ?? 0)
            : response.Content.Headers.ContentLength ?? expectedLength;

        await using Stream source = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var target = new FileStream(
            targetPath,
            resumed ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            useAsync: true);

        byte[] buffer = new byte[128 * 1024];
        long totalRead = alreadyHave;
        int read;

        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            totalRead += read;

            if (totalLength > 0)
            {
                progress?.Report(Math.Clamp(totalRead / (double)totalLength * 0.9, 0, 0.9));
            }
        }

        await target.FlushAsync(cancellationToken).ConfigureAwait(false);

        if (totalLength > 0 && totalRead != totalLength)
        {
            throw new IOException(Loc.F("L_UpdDownloadStoppedFormat", totalRead, totalLength));
        }
    }

    private static void DeleteStalePartials(string stagedPath, string keep)
    {
        try
        {
            string? folder = Path.GetDirectoryName(stagedPath);
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                return;
            }

            string pattern = Path.GetFileName(stagedPath) + ".*.part";
            foreach (string file in Directory.EnumerateFiles(folder, pattern, SearchOption.TopDirectoryOnly))
            {
                if (!string.Equals(file, keep, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Не удалось удалить недокачанные части обновления");
        }
    }

    private static long ExistingBytes(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : 0L;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0L;
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Оставшийся файл безвреден.
        }
    }

    private static UpdateCheckResult Failed(string reason) => new()
    {
        Status = UpdateStatus.Failed,
        Current = CurrentVersion,
        Error = reason
    };

    private static string Describe(UpdateCheckResult result) => result.Status switch
    {
        UpdateStatus.UpToDate => $"актуальная версия ({CurrentVersion}).",
        UpdateStatus.UpdateAvailable => $"доступна версия {result.Latest}.",
        UpdateStatus.NoDownload => result.Error ?? "в последнем релизе нет exe.",
        UpdateStatus.NoReleases => "публичных релизов пока нет.",
        UpdateStatus.Failed => $"проверка не удалась: {result.Error}",
        _ => "состояние неизвестно."
    };

    private static ReleaseVersion ReadCurrentVersion()
    {
        Assembly assembly = Assembly.GetEntryAssembly() ?? typeof(UpdateService).Assembly;

        string? informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            int plus = informational.IndexOf('+', StringComparison.Ordinal);
            string value = plus >= 0 ? informational[..plus] : informational;
            if (ReleaseVersion.TryParse(value, out ReleaseVersion parsed))
            {
                return parsed;
            }
        }

        return ReleaseVersion.TryParse(assembly.GetName().Version?.ToString(), out ReleaseVersion fallback)
            ? fallback
            : default;
    }

    private static HttpClient CreateClient(bool allowRedirects)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = allowRedirects,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        };

        var client = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"{AppInfo.Name}/{CurrentVersion}");
        return client;
    }

    private static void SaveState(DateTimeOffset checkedAt)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.GetApplicationDirectory());
            string json = JsonSerializer.Serialize(new UpdateState { LastCheckUtc = checkedAt });
            File.WriteAllText(AppPaths.GetUpdateStatePath(), json, new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Не удалось сохранить состояние проверки обновлений");
        }
    }

    /// <summary>
    /// Скрипт подмены exe: ждёт выхода приложения, заменяет файл, проверяет результат,
    /// запускает приложение снова и пишет каждый шаг в отчёт. Чистый ASCII.
    /// </summary>
    private static readonly string[] ApplyScriptLines =
    [
        "@echo off",
        "setlocal",
        "set \"STAGED=%DG_STAGED%\"",
        "set \"TARGET=%DG_TARGET%\"",
        "set \"LOG=%DG_LOG%\"",
        "set \"APPPID=%DG_PID%\"",
        "set \"EXPECT=%DG_EXPECT%\"",
        "call :note \"apply start: pid=%APPPID% expected bytes=%EXPECT%\"",
        "call :note \"staged: %STAGED%\"",
        "call :note \"target: %TARGET%\"",
        "",
        "rem Windows keeps a running executable locked, so retry the move itself.",
        "set /a tries=0",
        ":swap",
        "move /Y \"%STAGED%\" \"%TARGET%\" >nul 2>nul",
        "if not errorlevel 1 goto swapped",
        "set /a tries+=1",
        "if %tries% geq 30 goto stuck",
        "call :sleep",
        "goto swap",
        "",
        ":stuck",
        "rem Still locked after half a minute: the lock it holds proves the id is ours.",
        "call :note \"still locked after 30s, ending pid %APPPID%\"",
        "taskkill /PID %APPPID% /T /F >nul 2>nul",
        "call :sleep",
        "set /a tries=0",
        ":swapagain",
        "move /Y \"%STAGED%\" \"%TARGET%\" >nul 2>nul",
        "if not errorlevel 1 goto swapped",
        "set /a tries+=1",
        "if %tries% geq 15 goto copyfile",
        "call :sleep",
        "goto swapagain",
        "",
        ":copyfile",
        "rem A rename can also fail for reasons no retry can fix (another volume, a filter",
        "rem driver holding the file). Copying fails differently, so give it one chance.",
        "call :note \"rename failed, trying a copy\"",
        "copy /Y /B \"%STAGED%\" \"%TARGET%\" >>\"%LOG%\" 2>&1",
        "if errorlevel 1 goto failed",
        "del /F /Q \"%STAGED%\" >nul 2>nul",
        "",
        ":swapped",
        "call :verify",
        "if errorlevel 1 goto failed",
        "call :note \"RESULT=OK\"",
        "call :note \"the new build is in place, starting it\"",
        "start \"\" \"%TARGET%\"",
        "goto done",
        "",
        ":failed",
        "call :note \"RESULT=FAILED the downloaded file could not replace the running one\"",
        "call :note \"kept for another try: %STAGED%\"",
        "start \"\" \"%TARGET%\"",
        "goto done",
        "",
        ":done",
        "rem Nothing left to do here: the app deletes this script when it starts again.",
        "exit /b 0",
        "",
        ":verify",
        "if not exist \"%TARGET%\" exit /b 1",
        "for %%A in (\"%TARGET%\") do if %%~zA LSS 1024 exit /b 1",
        "if \"%EXPECT%\"==\"\" exit /b 0",
        "for %%A in (\"%TARGET%\") do if not \"%%~zA\"==\"%EXPECT%\" exit /b 1",
        "exit /b 0",
        "",
        ":note",
        ">>\"%LOG%\" echo [%DATE% %TIME%] %~1",
        "exit /b 0",
        "",
        ":sleep",
        "rem A wait that works without a console window.",
        "ping -n 2 127.0.0.1 >nul 2>nul",
        "exit /b 0"
    ];

    /// <summary>Состояние проверки обновлений, сохраняемое между запусками.</summary>
    private sealed class UpdateState
    {
        /// <summary>Момент последней проверки (UTC).</summary>
        public DateTimeOffset? LastCheckUtc { get; set; }
    }
}