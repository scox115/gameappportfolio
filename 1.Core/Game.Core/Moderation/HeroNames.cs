using System.Globalization;
using System.Text;

namespace Game.Core.Moderation;

/// <summary>
/// The rules a hero name must follow: its length, the characters it may use, and no slurs,
/// profanity or names that pass for staff. A word list can't catch everything a person can spell,
/// so players can also report a name, and an admin can rename the hero
/// (see docs/adr/0024-moderation-and-reports.md).
/// </summary>
public static class HeroNames
{
    public const int MinLength = 3;
    public const int MaxLength = 50;

    // Long enough that they don't turn up inside ordinary words, so they're blocked anywhere in a
    // name, however it is spaced or spelt with numbers ("F_u.c-k", "5h1t").
    private static readonly string[] BlockedAnywhere =
    [
        "fuck", "shit", "cunt", "nigger", "nigga", "faggot", "retard", "whore", "slut", "bitch",
        "nazi", "hitler", "kike", "penis", "vagina", "porn", "dildo", "jizz", "bollock", "asshole",
        "tranny", "molest",
    ];

    // Words that are also parts of harmless names (Cassandra, Peacock, Dickens, Therapist, Swanky,
    // Atwater, Torpedo), so they're only blocked as a whole word of the name.
    private static readonly string[] BlockedWords =
    [
        "ass", "arse", "fag", "fags", "cum", "tit", "tits", "dick", "cock", "pussy", "spic",
        "chink", "coon", "gook", "wop", "hoe", "homo", "kys", "rape", "rapist", "kkk", "sex",
        "wank", "twat", "pedo", "heil",
    ];

    // Names that would pass for the people running the game: a word starting with one of the first
    // list ("AdminBob", "Moderator_1"), or a whole word from the second.
    private static readonly string[] StaffPrefixes = ["admin", "moderator", "gamemaster"];
    private static readonly string[] StaffWords = ["mod", "gm", "staff", "support", "system", "official", "dev", "developer"];

    /// <summary>Why <paramref name="name"/> can't be used, or null when it is fine. Trim it first.</summary>
    /// <param name="allowStaffNames">True for the game's own admins, who may use a name that looks like staff.</param>
    public static string? Problem(string name, bool allowStaffNames = false)
    {
        if (name.Length is < MinLength or > MaxLength)
            return $"Hero names are {MinLength} to {MaxLength} characters long.";

        foreach (var c in name)
        {
            if (!char.IsLetterOrDigit(c) && c is not (' ' or '_' or '-' or '.' or '\''))
                return "Hero names can use letters, numbers, spaces and _ - . ' only.";
        }

        if (!name.Any(char.IsLetter)) return "Hero names need at least one letter.";
        if (name.Contains("  ")) return "Hero names can't have two spaces in a row.";
        if (name != name.Trim()) return "Hero names can't start or end with a space.";

        var joined = Fold(name);
        var collapsed = CollapseRepeats(joined);
        var words = Words(name).Select(Fold).ToList();

        if (!allowStaffNames && words.Any(w => StaffWords.Contains(w) || StaffPrefixes.Any(w.StartsWith)))
            return "That name could be mistaken for the game's staff. Choose another.";

        if (BlockedAnywhere.Any(w => joined.Contains(w) || collapsed.Contains(w)) || words.Any(BlockedWords.Contains))
            return "That name isn't allowed. Choose another.";

        return null;
    }

    // Lower case letters only, with accents dropped and number look-alikes read as letters, so
    // "Ñ1ce_0ne" and "nice one" compare the same.
    private static string Fold(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            var letter = char.ToLowerInvariant(c) switch
            {
                '0' => 'o',
                '1' => 'i',
                '3' => 'e',
                '4' => 'a',
                '5' => 's',
                '7' => 't',
                '8' => 'b',
                '9' => 'g',
                var other => other,
            };
            if (char.IsLetter(letter)) builder.Append(letter);
        }
        return builder.ToString();
    }

    // "fuuuck" reads as "fuck".
    private static string CollapseRepeats(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (builder.Length == 0 || builder[^1] != c) builder.Append(c);
        }
        return builder.ToString();
    }

    // The words of a name, split at spaces and punctuation and where a capital follows a small
    // letter, so "Big_Ass", "big ass" and "BigAss" each give "ass".
    private static IEnumerable<string> Words(string name)
    {
        var word = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            var boundary = c is ' ' or '_' or '-' or '.' or '\''
                || (i > 0 && char.IsUpper(c) && char.IsLower(name[i - 1]));
            if (boundary && word.Length > 0)
            {
                yield return word.ToString();
                word.Clear();
            }
            if (c is not (' ' or '_' or '-' or '.' or '\'')) word.Append(c);
        }
        if (word.Length > 0) yield return word.ToString();
    }
}
