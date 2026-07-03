using System.Text.RegularExpressions;
using Spitr.Core.Settings;

namespace Spitr.Core.Text;

/// <summary>
/// Ganzwort-Ersetzung (\b…\b), case-insensitiv. Die Ersetzung wird wörtlich
/// eingesetzt — Regex-Metazeichen im Substitut werden nicht interpretiert.
/// </summary>
public sealed class TextReplacementService : ITextReplacing
{
    public string Apply(IReadOnlyList<ReplacementRule> rules, string text)
    {
        var result = text;
        foreach (var rule in rules)
        {
            var pattern = rule.Pattern.Trim();
            if (pattern.Length == 0) continue;

            // Nur auf Seiten mit einer Wortgrenze verankern, die auf einem
            // Wortzeichen enden/beginnen. Sonst würden Begriffe wie "c++" oder
            // ".net" — deren Rand Interpunktion ist — nie matchen (\b läge
            // zwischen zwei Nicht-Wortzeichen).
            var core = Regex.Escape(pattern);
            var lead = IsWordCharacter(pattern[0]);
            var trail = IsWordCharacter(pattern[^1]);
            var anchored = (lead ? @"\b" : "") + core + (trail ? @"\b" : "");

            // MatchEvaluator statt Ersetzungs-Template: so bleiben "$" & Co. in
            // der Ersetzung wörtlicher Text (Pendant zu escapedTemplate im Original).
            result = Regex.Replace(
                result,
                anchored,
                _ => rule.Replacement,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        return result;
    }

    private static bool IsWordCharacter(char c) =>
        char.IsLetter(c) || char.IsNumber(c) || c == '_';
}
