using System.Text;

namespace DowntimeGreeting.Updates;

/// <summary>Тип блока в заметках к релизу.</summary>
public enum NoteBlockKind
{
    /// <summary>Заголовок раздела.</summary>
    Heading,

    /// <summary>Подзаголовок.</summary>
    Subheading,

    /// <summary>Пункт списка.</summary>
    Bullet,

    /// <summary>Обычный абзац.</summary>
    Text
}

/// <summary>Фрагмент текста с оформлением.</summary>
/// <param name="Text">Текст фрагмента.</param>
/// <param name="Bold">Выделение полужирным.</param>
/// <param name="Code">Моноширинный фрагмент (код, имя файла).</param>
public sealed record NoteRun(string Text, bool Bold = false, bool Code = false);

/// <summary>Блок заметок к релизу.</summary>
/// <param name="Kind">Тип блока.</param>
/// <param name="Runs">Фрагменты текста с оформлением.</param>
public sealed record NoteBlock(NoteBlockKind Kind, IReadOnlyList<NoteRun> Runs)
{
    /// <summary>Весь текст блока одной строкой.</summary>
    public string Text
    {
        get
        {
            var builder = new StringBuilder();
            foreach (NoteRun run in Runs)
            {
                builder.Append(run.Text);
            }

            return builder.ToString();
        }
    }
}

/// <summary>
/// Разбирает текст заметок к релизу в блоки, чтобы показать его оформленным,
/// а не как исходный Markdown с решётками и звёздочками.
/// </summary>
/// <remarks>
/// Поддерживается минимальный набор разметки, который реально встречается в заметках:
/// заголовки (<c>#</c>, <c>##</c>), пункты списка (<c>-</c>, <c>*</c>), выделение
/// <c>**жирным**</c> и <c>`код`</c>. Разделы на разных языках (<c>## English</c>)
/// отбрасываются, если запрошен русский текст, и наоборот.
/// </remarks>
public static class ReleaseNotesFormatter
{
    /// <summary>
    /// Выбирает из заметок раздел нужного языка.
    /// </summary>
    /// <param name="body">Полный текст заметок.</param>
    /// <param name="languageCode">Код языка: <c>ru</c> или <c>en</c>.</param>
    /// <returns>Текст выбранного раздела либо весь текст, если разделов нет.</returns>
    public static string SelectSection(string? body, string languageCode)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        string[] lines = body.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        // Заметки делятся на языковые разделы заголовком первого уровня.
        var sections = new List<(string Title, List<string> Lines)>();
        List<string> preamble = [];
        List<string>? current = null;
        string currentTitle = string.Empty;

        foreach (string line in lines)
        {
            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                current = [];
                currentTitle = line[2..].Trim();
                sections.Add((currentTitle, current));
                continue;
            }

            if (current is null)
            {
                preamble.Add(line);
            }
            else
            {
                current.Add(line);
            }
        }

        if (sections.Count == 0)
        {
            return body;
        }

        bool wantsRussian = string.Equals(languageCode, "ru", StringComparison.OrdinalIgnoreCase);

        foreach ((string title, List<string> sectionLines) in sections)
        {
            bool isRussianTitle = ContainsCyrillic(title);
            bool isEnglishTitle = title.Contains("english", StringComparison.OrdinalIgnoreCase)
                || title.Contains("англ", StringComparison.OrdinalIgnoreCase);

            if (wantsRussian && (isRussianTitle || title.Contains("рус", StringComparison.OrdinalIgnoreCase)))
            {
                return string.Join('\n', sectionLines);
            }

            if (!wantsRussian && isEnglishTitle)
            {
                return string.Join('\n', sectionLines);
            }
        }

        // Подходящего раздела нет — показываем первый, вместе с вводной частью.
        string selected = string.Join('\n', sections[0].Lines);
        string intro = string.Join('\n', preamble).Trim();

        return intro.Length == 0 ? selected : intro + "\n\n" + selected;
    }

    /// <summary>
    /// Разбирает текст заметок в блоки для отображения.
    /// </summary>
    /// <param name="text">Текст заметок без языковой обёртки.</param>
    /// <returns>Список блоков; пустой, если текста нет.</returns>
    public static IReadOnlyList<NoteBlock> Parse(string? text)
    {
        var blocks = new List<NoteBlock>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return blocks;
        }

        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var paragraph = new List<string>();

        void FlushParagraph()
        {
            if (paragraph.Count == 0)
            {
                return;
            }

            string joined = string.Join(' ', paragraph).Trim();
            paragraph.Clear();

            if (joined.Length > 0)
            {
                blocks.Add(new NoteBlock(NoteBlockKind.Text, ParseRuns(joined)));
            }
        }

        foreach (string rawLine in lines)
        {
            string line = rawLine.TrimEnd();

            if (line.Trim().Length == 0)
            {
                FlushParagraph();
                continue;
            }

            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                FlushParagraph();
                blocks.Add(new NoteBlock(NoteBlockKind.Subheading, ParseRuns(line[4..].Trim())));
                continue;
            }

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                FlushParagraph();
                blocks.Add(new NoteBlock(NoteBlockKind.Subheading, ParseRuns(line[3..].Trim())));
                continue;
            }

            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                FlushParagraph();
                blocks.Add(new NoteBlock(NoteBlockKind.Heading, ParseRuns(line[2..].Trim())));
                continue;
            }

            if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
            {
                FlushParagraph();
                blocks.Add(new NoteBlock(NoteBlockKind.Bullet, ParseRuns(line[2..].Trim())));
                continue;
            }

            paragraph.Add(line.Trim());
        }

        FlushParagraph();
        return blocks;
    }

    /// <summary>
    /// Разбирает строку на фрагменты с оформлением.
    /// </summary>
    /// <param name="line">Строка заметок.</param>
    /// <returns>Фрагменты текста.</returns>
    private static IReadOnlyList<NoteRun> ParseRuns(string line)
    {
        var runs = new List<NoteRun>();
        var buffer = new StringBuilder();
        bool bold = false;
        bool code = false;
        int index = 0;

        void Flush()
        {
            if (buffer.Length == 0)
            {
                return;
            }

            runs.Add(new NoteRun(buffer.ToString(), bold, code));
            buffer.Clear();
        }

        while (index < line.Length)
        {
            char current = line[index];

            if (current == '`')
            {
                Flush();
                code = !code;
                index++;
                continue;
            }

            if (current == '*' && index + 1 < line.Length && line[index + 1] == '*' && !code)
            {
                Flush();
                bold = !bold;
                index += 2;
                continue;
            }

            buffer.Append(current);
            index++;
        }

        Flush();

        if (runs.Count == 0)
        {
            runs.Add(new NoteRun(line));
        }

        return runs;
    }

    private static bool ContainsCyrillic(string value)
    {
        foreach (char symbol in value)
        {
            if (symbol is >= 'А' and <= 'я' or 'Ё' or 'ё')
            {
                return true;
            }
        }

        return false;
    }
}