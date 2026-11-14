using System.Globalization;
using DowntimeGreeting.Localization;

namespace DowntimeGreeting.Models;

/// <summary>
/// Источник данных, использованный при определении времени простоя.
/// </summary>
public enum DowntimeSource
{
    /// <summary>Источник не определён (данные недоступны).</summary>
    Unknown = 0,

    /// <summary>Событие 6006 «Служба журнала событий была остановлена» (штатное выключение).</summary>
    EventLogShutdown = 1,

    /// <summary>Событие 1074 «Инициатор завершения работы» (запасной вариант события 6006).</summary>
    EventLogInitiatedShutdown = 2,

    /// <summary>Событие 6008 «Неожиданное завершение работы».</summary>
    EventLogUnexpectedShutdown = 3,

    /// <summary>Событие 6005 «Служба журнала событий запущена» (приблизительная оценка).</summary>
    EventLogLogStarted = 4,

    /// <summary>WMI-запрос <c>Win32_OperatingSystem.LastBootUpTime</c>.</summary>
    WmiLastBootUpTime = 5,

    /// <summary>Системный uptime (<see cref="Environment.TickCount64"/>).</summary>
    SystemUptime = 6
}

/// <summary>
/// Информация о времени простоя компьютера между выключением и текущим включением.
/// </summary>
public sealed class DowntimeInfo
{
    /// <summary>Формат даты и времени последнего выключения.</summary>
    private const string ShutdownDateFormat = "dd.MM.yyyy HH:mm";

    /// <summary>Момент текущего включения (начала текущего сеанса работы).</summary>
    public required DateTime BootTime { get; init; }

    /// <summary>Момент последнего выключения системы, если его удалось определить.</summary>
    public DateTime? LastShutdownTime { get; init; }

    /// <summary>Длительность простоя; <see langword="null"/>, если определить не удалось.</summary>
    public TimeSpan? Downtime { get; init; }

    /// <summary>Источник, из которого получены данные о выключении.</summary>
    public DowntimeSource Source { get; init; } = DowntimeSource.Unknown;

    /// <summary>
    /// Признак приблизительной оценки (например, когда вместо события выключения
    /// использовалось предыдущее событие загрузки или системный uptime).
    /// </summary>
    public bool IsApproximate { get; init; }

    /// <summary>Признак того, что длительность простоя известна.</summary>
    public bool IsKnown => Downtime.HasValue;

    /// <summary>
    /// Признак перезагрузки: компьютер не был выключен надолго, а перезагрузился
    /// (событие 1074 сообщает тип «Перезапуск»).
    /// </summary>
    public bool IsRestart { get; init; }

    /// <summary>Подпись под длительностью на текущем языке интерфейса.</summary>
    public string CaptionText => Loc.T(IsRestart ? "L_GreetingRestarted" : "L_GreetingWasOff");

    /// <summary>Строка со временем выключения (или перезагрузки) на текущем языке интерфейса.</summary>
    public string ShutdownLineText => Loc.F(
        IsRestart ? "L_GreetingRestartFormat" : "L_GreetingLastShutdownFormat",
        LastShutdownText);

    /// <summary>Длительность простоя в человекочитаемом виде на текущем языке интерфейса.</summary>
    public string DowntimeText =>
        Downtime is { } downtime
            ? (IsApproximate
                ? Loc.F("L_ApproxFormat", FormatDuration(downtime, Loc.Current))
                : FormatDuration(downtime, Loc.Current))
            : Loc.T("L_UnknownDowntime");

    /// <summary>Дата и время последнего выключения в формате «12.11.2026 23:14» либо «—».</summary>
    public string LastShutdownText =>
        LastShutdownTime is { } shutdown
            ? shutdown.ToString(ShutdownDateFormat, CultureInfo.InvariantCulture)
                + (IsApproximate ? Loc.T("L_ApproxSuffix") : string.Empty)
            : Loc.T("L_UnknownShutdown");

    /// <summary>
    /// Раскладывает длительность на дни, часы и минуты.
    /// Меньше часа — только минуты, меньше минуты — «меньше минуты».
    /// </summary>
    /// <param name="value">Длительность простоя.</param>
    /// <param name="language">Язык, на котором нужно вывести длительность.</param>
    /// <returns>Строка вида «1 д 14 ч 23 мин» или «1d 14h 23m».</returns>
    public static string FormatDuration(TimeSpan value, AppLanguage language)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        int days = value.Days;
        int hours = value.Hours;
        int minutes = value.Minutes;

        if (days > 0)
        {
            return Loc.F("L_DurationDaysFormat", days, hours, minutes);
        }

        if (hours > 0)
        {
            return Loc.F("L_DurationHoursFormat", hours, minutes);
        }

        if (minutes > 0)
        {
            return Loc.F("L_DurationMinutesFormat", minutes);
        }

        return Loc.T("L_LessThanMinute");
    }

    /// <inheritdoc />
    public override string ToString() =>
        string.Format(
            CultureInfo.InvariantCulture,
            "Downtime={0}, Source={1}, Approximate={2}, BootTime={3:dd.MM.yyyy HH:mm:ss}, LastShutdown={4}",
            DowntimeText,
            Source,
            IsApproximate,
            BootTime,
            LastShutdownText);
}