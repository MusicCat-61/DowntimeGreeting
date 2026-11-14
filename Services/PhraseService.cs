using DowntimeGreeting.Localization;
using DowntimeGreeting.Models;

namespace DowntimeGreeting.Services;

/// <summary>
/// Выбирает шутливую фразу для приветственной плашки с учётом языка интерфейса.
/// </summary>
public sealed class PhraseService
{
    private readonly IReadOnlyList<string> _phrasesRu;
    private readonly IReadOnlyList<string> _phrasesEn;

    /// <summary>
    /// Инициализирует новый экземпляр <see cref="PhraseService"/>.
    /// </summary>
    /// <param name="phrasesRu">Фразы на русском; если пусто, берутся значения по умолчанию.</param>
    /// <param name="phrasesEn">Фразы на английском; если пусто, берутся значения по умолчанию.</param>
    /// <exception cref="ArgumentNullException">Любой из списков равен <see langword="null"/>.</exception>
    public PhraseService(IReadOnlyList<string> phrasesRu, IReadOnlyList<string> phrasesEn)
    {
        ArgumentNullException.ThrowIfNull(phrasesRu);
        ArgumentNullException.ThrowIfNull(phrasesEn);

        _phrasesRu = phrasesRu.Count > 0 ? phrasesRu : AppConfig.DefaultPhrases;
        _phrasesEn = phrasesEn.Count > 0 ? phrasesEn : AppConfig.DefaultPhrasesEn;
    }

    /// <summary>
    /// Возвращает список фраз для указанного языка.
    /// </summary>
    /// <param name="language">Язык интерфейса.</param>
    /// <returns>Непустой список фраз.</returns>
    public IReadOnlyList<string> GetPhrases(AppLanguage language) =>
        language == AppLanguage.Ru ? _phrasesRu : _phrasesEn;

    /// <summary>
    /// Возвращает случайную фразу на текущем языке интерфейса.
    /// </summary>
    /// <returns>Шутливая фраза для плашки.</returns>
    public string GetRandomPhrase()
    {
        IReadOnlyList<string> phrases = GetPhrases(Loc.Current);
        return phrases[Random.Shared.Next(phrases.Count)];
    }

    /// <summary>
    /// Возвращает фразу, отличную от предыдущей (если фраз больше одной).
    /// </summary>
    /// <param name="previousPhrase">Предыдущая показанная фраза.</param>
    /// <returns>Новая фраза на текущем языке интерфейса.</returns>
    public string GetNextPhrase(string? previousPhrase)
    {
        IReadOnlyList<string> phrases = GetPhrases(Loc.Current);
        if (phrases.Count == 1)
        {
            return phrases[0];
        }

        string candidate = phrases[Random.Shared.Next(phrases.Count)];
        int attempts = 0;
        while (string.Equals(candidate, previousPhrase, StringComparison.Ordinal) && attempts < phrases.Count)
        {
            candidate = phrases[Random.Shared.Next(phrases.Count)];
            attempts++;
        }

        return candidate;
    }
}