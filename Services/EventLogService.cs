using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Management;
using DowntimeGreeting.Models;
using Serilog;

namespace DowntimeGreeting.Services;

/// <summary>
/// Определяет, сколько времени компьютер был выключен — то есть интервал между последним
/// выключением системы и текущим включением.
/// </summary>
/// <remarks>
/// Порядок источников данных:
/// <list type="number">
/// <item>Журнал <c>System</c>, событие 6006 — «Служба журнала событий была остановлена».</item>
/// <item>Журнал <c>System</c>, событие 1074 — инициатор выключения/перезагрузки.</item>
/// <item>Журнал <c>System</c>, событие 6008 — неожиданное завершение работы (время берётся из параметров события).</item>
/// <item>Журнал <c>System</c>, событие 6005 — предыдущий запуск службы журнала (приблизительная оценка).</item>
/// <item>WMI <c>Win32_OperatingSystem.LastBootUpTime</c> — если журнал недоступен.</item>
/// <item>Системный uptime (<see cref="Environment.TickCount64"/>) — только для определения момента включения.</item>
/// </list>
/// Сон и гибернация не считаются выключением: эти состояния не порождают событий 6006/1074/6008.
/// </remarks>
public sealed class EventLogService
{
    /// <summary>Имя канала журнала событий с системными событиями.</summary>
    private const string SystemLogName = "System";

    /// <summary>Event ID 6006 — служба журнала событий остановлена.</summary>
    private const int LogStoppedEventId = 6006;

    /// <summary>Event ID 6005 — служба журнала событий запущена.</summary>
    private const int LogStartedEventId = 6005;

    /// <summary>Event ID 1074 — инициатор завершения работы или перезагрузки.</summary>
    private const int InitiatedShutdownEventId = 1074;

    /// <summary>Event ID 6008 — неожиданное завершение работы.</summary>
    private const int UnexpectedShutdownEventId = 6008;

    /// <summary>Сколько записей журнала максимум просматривается при поиске события выключения.</summary>
    private const int MaxEventsToScan = 400;

    /// <summary>Бюджет времени на чтение журнала событий.</summary>
    private static readonly TimeSpan ScanTimeBudget = TimeSpan.FromSeconds(1.5);

    /// <summary>Допуск при сопоставлении события загрузки с моментом старта текущего сеанса.</summary>
    private static readonly TimeSpan BootMatchTolerance = TimeSpan.FromMinutes(5);

    /// <summary>Максимальное правдоподобное время простоя (защита от мусорных данных).</summary>
    private static readonly TimeSpan MaximumPlausibleDowntime = TimeSpan.FromDays(3650);

    /// <summary>
    /// Асинхронно определяет информацию о простое компьютера.
    /// </summary>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Информация о простое.</returns>
    public Task<DowntimeInfo> GetDowntimeInfoAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => GetDowntimeInfo(cancellationToken), cancellationToken);

    /// <summary>
    /// Определяет информацию о простое компьютера (синхронно, выполняется в фоновом потоке).
    /// </summary>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Информация о простое.</returns>
    public DowntimeInfo GetDowntimeInfo(CancellationToken cancellationToken = default)
    {
        DateTime bootTime = GetBootTimeFromUptime();
        Log.Debug("Момент включения по системному uptime: {BootTime:O}", bootTime);

        EventLogSnapshot? snapshot = null;
        try
        {
            snapshot = ReadSessionEvents(bootTime, cancellationToken);
        }
        catch (Exception ex)
        {
            // Любая проблема с журналом (нет прав, канал недоступен, ошибка провайдера)
            // не должна ломать приложение — переходим на WMI-фолбэк.
            Log.Warning(ex, "Не удалось прочитать журнал событий {LogName}, используется резервный источник", SystemLogName);
        }

        DowntimeSource bootSource = DowntimeSource.SystemUptime;
        if (snapshot?.CurrentBootEventTime is { } logBootTime)
        {
            bootSource = DowntimeSource.EventLogLogStarted;
            TimeSpan difference = logBootTime - bootTime;
            if (difference.Duration() > BootMatchTolerance)
            {
                Log.Debug(
                    "Событие 6005 ({LogBootTime:HH:mm:ss}) и системный uptime ({BootTime:HH:mm:ss}) расходятся на {Difference}",
                    logBootTime,
                    bootTime,
                    difference);
            }
        }
        else if (snapshot is null)
        {
            DateTime? wmiBootTime = TryGetWmiBootTime();
            if (wmiBootTime is not null)
            {
                bootTime = wmiBootTime.Value;
                bootSource = DowntimeSource.WmiLastBootUpTime;
                Log.Debug("Момент включения по WMI: {BootTime:O}", bootTime);
            }
        }

        DateTime? shutdownTime = null;
        DowntimeSource source = DowntimeSource.Unknown;
        bool isApproximate = false;

        if (snapshot is not null)
        {
            if (snapshot.LogStoppedEventTime is { } logStopped)
            {
                shutdownTime = logStopped;
                source = DowntimeSource.EventLogShutdown;
            }
            else if (snapshot.InitiatedShutdownEventTime is { } initiated)
            {
                shutdownTime = initiated;
                source = DowntimeSource.EventLogInitiatedShutdown;
            }
            else if (snapshot.UnexpectedShutdownEventTime is { } unexpected)
            {
                shutdownTime = unexpected;
                source = DowntimeSource.EventLogUnexpectedShutdown;
                isApproximate = true;
            }
            else if (snapshot.PreviousBootEventTime is { } previousBoot)
            {
                // Крайний случай: события выключения отсутствуют вовсе.
                // Предыдущий запуск службы журнала — верхняя оценка момента выключения.
                shutdownTime = previousBoot;
                source = DowntimeSource.EventLogLogStarted;
                isApproximate = true;
                Log.Warning("События выключения не найдены, используется предыдущее событие 6005 ({Time:O})", previousBoot);
            }
        }
        else
        {
            Log.Warning("Журнал событий недоступен: длительность простоя определить нельзя (boot={BootSource})", bootSource);
        }

        // Событие выключения не может быть позже текущей загрузки.
        if (shutdownTime is { } shutdown && shutdown > bootTime)
        {
            Log.Warning(
                "Момент выключения ({Shutdown:O}) позже момента загрузки ({Boot:O}) — значение отброшено",
                shutdown,
                bootTime);
            shutdownTime = null;
            source = DowntimeSource.Unknown;
            isApproximate = false;
        }

        TimeSpan? downtime = null;
        if (shutdownTime is { } shutdownTimeValue)
        {
            TimeSpan candidate = bootTime - shutdownTimeValue;
            if (candidate < TimeSpan.Zero)
            {
                Log.Warning("Отрицательный простой ({Downtime}) — значение отброшено", candidate);
            }
            else if (candidate > MaximumPlausibleDowntime)
            {
                Log.Warning("Неправдоподобно большой простой ({Downtime}) — значение отброшено", candidate);
            }
            else
            {
                downtime = candidate;
            }
        }

        var info = new DowntimeInfo
        {
            BootTime = bootTime,
            LastShutdownTime = shutdownTime,
            Downtime = downtime,
            Source = source,
            IsApproximate = isApproximate,
            IsRestart = snapshot?.InitiatedShutdownWasRestart ?? false
        };

        Log.Information(
            "Простой: {Downtime}; перезагрузка: {IsRestart}; источник: {Source}; приблизительно: {IsApproximate}; выключение: {LastShutdown}; включение: {BootTime:dd.MM.yyyy HH:mm:ss}; bootSource: {BootSource}",
            info.DowntimeText,
            info.IsRestart,
            info.Source,
            info.IsApproximate,
            info.LastShutdownText,
            info.BootTime,
            bootSource);

        return info;
    }

    /// <summary>
    /// Вычисляет момент включения компьютера по системному счётчику времени с момента загрузки.
    /// </summary>
    /// <returns>Локальное время включения.</returns>
    private static DateTime GetBootTimeFromUptime() => DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);

    /// <summary>
    /// Читает последние события журнала <c>System</c>, относящиеся к выключению и загрузке.
    /// </summary>
    /// <param name="currentBootTime">Момент включения текущего сеанса.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Набор найденных событий.</returns>
    /// <exception cref="EventLogNotFoundException">Канал журнала не найден.</exception>
    /// <exception cref="UnauthorizedAccessException">Нет прав на чтение журнала.</exception>
    private static EventLogSnapshot ReadSessionEvents(DateTime currentBootTime, CancellationToken cancellationToken)
    {
        string query = string.Format(
            CultureInfo.InvariantCulture,
            "*[System[(EventID={0} or EventID={1} or EventID={2} or EventID={3})]]",
            LogStoppedEventId,
            LogStartedEventId,
            InitiatedShutdownEventId,
            UnexpectedShutdownEventId);

        var eventQuery = new EventLogQuery(SystemLogName, PathType.LogName, query)
        {
            ReverseDirection = true
        };

        DateTime deadline = DateTime.UtcNow + ScanTimeBudget;
        var snapshot = new EventLogSnapshot();
        int scanned = 0;

        using var reader = new EventLogReader(eventQuery);
        while (scanned < MaxEventsToScan)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                Log.Debug("Чтение журнала событий отменено после {Scanned} записей", scanned);
                break;
            }

            if (DateTime.UtcNow >= deadline)
            {
                Log.Debug("Достигнут бюджет времени чтения журнала событий ({Scanned} записей)", scanned);
                break;
            }

            using EventRecord? record = reader.ReadEvent();
            if (record is null)
            {
                break;
            }

            scanned++;

            if (NormalizeTimestamp(record.TimeCreated) is not { } eventTime)
            {
                continue;
            }

            int eventId = record.Id;

            switch (eventId)
            {
                case LogStoppedEventId:
                    if (eventTime <= currentBootTime)
                    {
                        snapshot.LogStoppedEventTime ??= eventTime;
                    }

                    break;

                case InitiatedShutdownEventId:
                    if (eventTime <= currentBootTime)
                    {
                        if (snapshot.InitiatedShutdownEventTime is null)
                        {
                            snapshot.InitiatedShutdownEventTime = eventTime;
                            snapshot.InitiatedShutdownWasRestart = IsRestartEvent(record);
                        }
                    }

                    break;

                case UnexpectedShutdownEventId:
                    // Событие 6008 записывается уже при следующей загрузке, поэтому время
                    // самого сбоя берётся из параметров события.
                    if (snapshot.UnexpectedShutdownEventTime is null)
                    {
                        snapshot.UnexpectedShutdownEventTime = TryParseUnexpectedShutdownTime(record, currentBootTime);
                        if (snapshot.UnexpectedShutdownEventTime is null)
                        {
                            Log.Debug("Не удалось получить время из события 6008 (запись от {EventTime:O})", eventTime);
                        }
                    }

                    break;

                case LogStartedEventId:
                    if (eventTime <= currentBootTime)
                    {
                        snapshot.PreviousBootEventTime ??= eventTime;
                    }
                    else if (eventTime <= currentBootTime + BootMatchTolerance)
                    {
                        snapshot.CurrentBootEventTime ??= eventTime;
                    }

                    break;

                default:
                    break;
            }
        }

        Log.Debug(
            "Просмотрено записей журнала: {Scanned}; 6006={Shutdown}, 1074={Initiated}, 6008={Unexpected}, предыдущий 6005={Previous}, текущий 6005={Current}",
            scanned,
            snapshot.LogStoppedEventTime,
            snapshot.InitiatedShutdownEventTime,
            snapshot.UnexpectedShutdownEventTime,
            snapshot.PreviousBootEventTime,
            snapshot.CurrentBootEventTime);

        return snapshot;
    }

    /// <summary>
    /// Пытается извлечь время неожиданного завершения работы из параметров события 6008.
    /// </summary>
    /// <param name="record">Запись журнала событий.</param>
    /// <param name="currentBootTime">Момент включения текущего сеанса.</param>
    /// <returns>Время выключения либо <see langword="null"/>, если разобрать параметры не удалось.</returns>
    private static DateTime? TryParseUnexpectedShutdownTime(EventRecord record, DateTime currentBootTime)
    {
        try
        {
            IList<EventProperty>? properties = record.Properties;
            if (properties is null || properties.Count < 2)
            {
                return null;
            }

            string? timePart = properties[0].Value?.ToString();
            string? datePart = properties[1].Value?.ToString();
            if (string.IsNullOrWhiteSpace(timePart) || string.IsNullOrWhiteSpace(datePart))
            {
                return null;
            }

            string combined = $"{datePart} {timePart}";
            CultureInfo[] cultures = [CultureInfo.CurrentCulture, CultureInfo.InvariantCulture];

            foreach (CultureInfo culture in cultures)
            {
                if (DateTime.TryParse(combined, culture, DateTimeStyles.None, out DateTime parsed) && parsed <= currentBootTime)
                {
                    return DateTime.SpecifyKind(parsed, DateTimeKind.Local);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Не удалось разобрать параметры события 6008");
        }

        return null;
    }

    /// <summary>
    /// Проверяет, является ли событие 1074 перезапуском (а не выключением).
    /// </summary>
    /// <param name="record">Запись журнала событий.</param>
    /// <returns><see langword="true"/>, если тип завершения работы — «Перезапуск».</returns>
    private static bool IsRestartEvent(EventRecord record)
    {
        try
        {
            IList<EventProperty>? properties = record.Properties;
            if (properties is null || properties.Count < 5)
            {
                return false;
            }

            // param5 содержит тип: «Завершить работу», «Перезапуск», «Выключение питания» и т.п.
            string? shutdownType = properties[4].Value?.ToString();
            if (string.IsNullOrWhiteSpace(shutdownType))
            {
                return false;
            }

            return shutdownType.Contains("Перезапуск", StringComparison.OrdinalIgnoreCase)
                || shutdownType.Contains("Restart", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Не удалось разобрать тип события 1074");
            return false;
        }
    }

    /// <summary>
    /// Приводит отметку времени события к локальному времени.
    /// </summary>
    /// <param name="value">Отметка времени из журнала.</param>
    /// <returns>Локальное время либо <see langword="null"/>.</returns>
    private static DateTime? NormalizeTimestamp(DateTime? value)
    {
        if (value is not { } timestamp)
        {
            return null;
        }

        return timestamp.Kind switch
        {
            DateTimeKind.Utc => timestamp.ToLocalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(timestamp, DateTimeKind.Local),
            _ => timestamp
        };
    }

    /// <summary>
    /// Получает момент последней загрузки через WMI-запрос <c>Win32_OperatingSystem.LastBootUpTime</c>.
    /// </summary>
    /// <returns>Локальное время загрузки либо <see langword="null"/>, если WMI недоступен.</returns>
    private static DateTime? TryGetWmiBootTime()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT LastBootUpTime FROM Win32_OperatingSystem");
            using ManagementObjectCollection results = searcher.Get();

            foreach (ManagementBaseObject item in results)
            {
                using (item)
                {
                    if (item["LastBootUpTime"] is string raw && !string.IsNullOrWhiteSpace(raw))
                    {
                        return ManagementDateTimeConverter.ToDateTime(raw);
                    }
                }
            }
        }
        catch (ManagementException ex)
        {
            Log.Warning(ex, "WMI-запрос LastBootUpTime завершился ошибкой");
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Warning(ex, "Нет доступа к WMI при запросе LastBootUpTime");
        }
        catch (PlatformNotSupportedException ex)
        {
            Log.Warning(ex, "WMI недоступен на этой платформе");
        }

        return null;
    }

    /// <summary>
    /// Набор событий, найденных в журнале <c>System</c> при анализе текущего сеанса.
    /// </summary>
    private sealed class EventLogSnapshot
    {
        /// <summary>Время последнего события 6006 (штатное выключение).</summary>
        public DateTime? LogStoppedEventTime { get; set; }

        /// <summary>Время последнего события 1074 (инициированное выключение).</summary>
        public DateTime? InitiatedShutdownEventTime { get; set; }

        /// <summary>Событие 1074 сообщает, что это был перезапуск, а не выключение.</summary>
        public bool InitiatedShutdownWasRestart { get; set; }

        /// <summary>Время последнего события 6008 (неожиданное выключение).</summary>
        public DateTime? UnexpectedShutdownEventTime { get; set; }

        /// <summary>Время предыдущего события 6005 (запуск журнала в прошлом сеансе).</summary>
        public DateTime? PreviousBootEventTime { get; set; }

        /// <summary>Время события 6005 текущего сеанса.</summary>
        public DateTime? CurrentBootEventTime { get; set; }
    }
}