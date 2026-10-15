namespace BiliIndexTranslate.Core.Selection;

public enum ReadingLayout
{
    /// <summary>A word or short phrase: large type, tight padding, controls hidden until hover.</summary>
    Compact,
    /// <summary>Sentences and paragraphs: reading type size, generous line height, max width, scroll when long.</summary>
    Paragraph,
}

public static class ReadingLayoutClassifier
{
    public const int CompactMaxTranslationChars = 14;
    public const int CompactMaxSourceChars = 48;

    public static ReadingLayout Classify(string sourceText, string? translation)
    {
        var src = sourceText.Trim();
        if (src.Length > CompactMaxSourceChars || src.Contains('\n')) return ReadingLayout.Paragraph;
        if (translation is null) return ReadingLayout.Compact; // loading a short selection: keep it small
        var t = translation.Trim();
        if (t.Contains('\n')) return ReadingLayout.Paragraph;
        return VisualLength(t) <= CompactMaxTranslationChars ? ReadingLayout.Compact : ReadingLayout.Paragraph;
    }

    /// <summary>Counts CJK ideographs as 1 and Latin letters as ~0.55 so "一个词" and "a phrase" compare fairly.</summary>
    public static int VisualLength(string text)
    {
        double len = 0;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch)) { len += 0.4; continue; }
            len += IsWide(ch) ? 1.0 : 0.55;
        }
        return (int)Math.Ceiling(len);
    }

    private static bool IsWide(char ch)
        => ch >= 0x1100 && (ch <= 0x115F || ch is >= (char)0x2E80 and <= (char)0xA4CF || ch is >= (char)0xAC00 and <= (char)0xD7A3
            || ch is >= (char)0xF900 and <= (char)0xFAFF || ch is >= (char)0xFE30 and <= (char)0xFE4F || ch is >= (char)0xFF00 and <= (char)0xFF60
            || ch is >= (char)0xFFE0 and <= (char)0xFFE6);
}
