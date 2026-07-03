using Spitr.Core.Settings;
using Spitr.Core.Text;

namespace Spitr.Core.Tests;

// Edge-Cases der Wörterbuch-Ersetzung: interpunktionsberandete Begriffe,
// Regex-Metazeichen, Unicode-Wortgrenzen, String-Ränder und mehrzeiliger Text.
public class TextReplacementServiceEdgeTests
{
    private readonly TextReplacementService _service = new();

    private static ReplacementRule Rule(string pattern, string replacement) =>
        new(Guid.NewGuid(), pattern, replacement);

    [Fact]
    public void MatchesTermEndingInPunctuation()
    {
        // Regression: "\b…\b" machte das früher unmöglich.
        var rules = new[] { Rule("c++", "cpp") };
        Assert.Equal("ich nutze cpp gern", _service.Apply(rules, "ich nutze c++ gern"));
    }

    [Fact]
    public void MatchesTermStartingWithPunctuation()
    {
        var rules = new[] { Rule(".net", "dotnet") };
        Assert.Equal("läuft auf dotnet hier", _service.Apply(rules, "läuft auf .net hier"));
    }

    [Fact]
    public void RegexMetacharactersAreLiteral()
    {
        // "a.b" darf "aXb" nicht matchen.
        var rules = new[] { Rule("a.b", "DOT") };
        Assert.Equal("aXb und DOT", _service.Apply(rules, "aXb und a.b"));
    }

    [Fact]
    public void UnicodeWordBoundaries()
    {
        var rules = new[] { Rule("müller", "Müller") };
        Assert.Equal("Herr Müller kommt", _service.Apply(rules, "Herr müller kommt"));
        // …aber nicht innerhalb eines anderen Worts.
        Assert.Equal("müllerstraße", _service.Apply(rules, "müllerstraße"));
    }

    [Fact]
    public void MatchesAtStringStartAndEnd()
    {
        var rules = new[] { Rule("klode", "Claude") };
        Assert.Equal("Claude", _service.Apply(rules, "Klode"));
        Assert.Equal("Claude hilft", _service.Apply(rules, "Klode hilft"));
        Assert.Equal("frag Claude", _service.Apply(rules, "frag Klode"));
    }

    [Fact]
    public void ReplacementWithBackslashStaysLiteral()
    {
        var rules = new[] { Rule("pfad", @"C:\temp") };
        Assert.Equal(@"der C:\temp", _service.Apply(rules, "der pfad"));
    }

    [Fact]
    public void PreservesNewlines()
    {
        var rules = new[] { Rule("klode", "Claude") };
        Assert.Equal("Claude\nzweite Zeile", _service.Apply(rules, "Klode\nzweite Zeile"));
    }

    [Fact]
    public void ReplacesEveryOccurrence()
    {
        var rules = new[] { Rule("klode", "Claude") };
        Assert.Equal("Claude und Claude", _service.Apply(rules, "Klode und Klode"));
    }

    [Fact]
    public void EmptyRulesLeaveTextUntouched()
    {
        Assert.Equal("unverändert", _service.Apply(Array.Empty<ReplacementRule>(), "unverändert"));
    }

    [Fact]
    public void RuleWhosePatternIsOnlyPunctuationStillWorks()
    {
        // Gar keine Wortränder → keine Grenzen; darf nicht crashen und muss matchen.
        var rules = new[] { Rule("->", "→") };
        Assert.Equal("a → b", _service.Apply(rules, "a -> b"));
    }
}
