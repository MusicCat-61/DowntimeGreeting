using System.Globalization;
using System.Text.RegularExpressions;

namespace DowntimeGreeting.Updates;

/// <summary>
/// Версия приложения в формате <c>Major.Minor.Patch[.Build]</c> с разбором из тега релиза.
/// </summary>
public readonly partial struct ReleaseVersion : IComparable<ReleaseVersion>, IEquatable<ReleaseVersion>
{
    /// <summary>Разбор тега версии: допускает префикс <c>v</c>, суффиксы и меньше четырёх чисел.</summary>
    [GeneratedRegex(@"^[vV]?(\d{1,3})(?:\.(\d{1,3}))?(?:\.(\d{1,3}))?(?:\.(\d{1,3}))?")]
    private static partial Regex VersionPattern();

    private ReleaseVersion(int major, int minor, int patch, int build)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Build = build;
    }

    /// <summary>Старшая часть версии.</summary>
    public int Major { get; }

    /// <summary>Минорная часть версии.</summary>
    public int Minor { get; }

    /// <summary>Номер патча.</summary>
    public int Patch { get; }

    /// <summary>Номер сборки (может быть 0).</summary>
    public int Build { get; }

    /// <summary>
    /// Пытается разобрать версию из строки тега или информационной версии сборки.
    /// </summary>
    /// <param name="value">Строка вида <c>v1.2.3</c>, <c>1.2.3.4</c> или <c>1.2.3-beta</c>.</param>
    /// <param name="version">Разобранная версия.</param>
    /// <returns><see langword="true"/>, если строка распознана.</returns>
    public static bool TryParse(string? value, out ReleaseVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        Match match = VersionPattern().Match(value.Trim());
        if (!match.Success)
        {
            return false;
        }

        version = new ReleaseVersion(
            ParseGroup(match, 1),
            ParseGroup(match, 2),
            ParseGroup(match, 3),
            ParseGroup(match, 4));

        return true;
    }

    /// <summary>
    /// Сравнивает версии по старшинству.
    /// </summary>
    /// <param name="other">Другая версия.</param>
    /// <returns>Отрицательное число, если текущая версия старше; положительное, если младше.</returns>
    public int CompareTo(ReleaseVersion other)
    {
        int result = Major.CompareTo(other.Major);
        if (result != 0)
        {
            return result;
        }

        result = Minor.CompareTo(other.Minor);
        if (result != 0)
        {
            return result;
        }

        result = Patch.CompareTo(other.Patch);
        return result != 0 ? result : Build.CompareTo(other.Build);
    }

    /// <inheritdoc />
    public bool Equals(ReleaseVersion other) => CompareTo(other) == 0;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ReleaseVersion other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, Build);

    /// <inheritdoc />
    public override string ToString() => Build > 0
        ? string.Format(CultureInfo.InvariantCulture, "{0}.{1}.{2}.{3}", Major, Minor, Patch, Build)
        : string.Format(CultureInfo.InvariantCulture, "{0}.{1}.{2}", Major, Minor, Patch);

    /// <summary>Сравнивает две версии.</summary>
    /// <param name="left">Левая версия.</param>
    /// <param name="right">Правая версия.</param>
    /// <returns><see langword="true"/>, если левая версия старше.</returns>
    public static bool operator >(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) > 0;

    /// <summary>Сравнивает две версии.</summary>
    /// <param name="left">Левая версия.</param>
    /// <param name="right">Правая версия.</param>
    /// <returns><see langword="true"/>, если левая версия младше.</returns>
    public static bool operator <(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) < 0;

    private static int ParseGroup(Match match, int group)
    {
        Group captured = match.Groups[group];
        return captured.Success && int.TryParse(captured.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : 0;
    }
}