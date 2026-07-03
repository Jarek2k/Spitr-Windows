using Spitr.Core.Text;

namespace Spitr.Core.Tests;

// Abgeleitet aus den Code-Pfaden von TextInsertionService.smartSpaced /
// leadingSpaceNeeded im macOS-Original (dort ohne eigene Testdatei).
public class SmartSpacingTests
{
    // MARK: - Deaktiviert

    [Fact]
    public void DisabledPassesTextThroughUnchanged()
    {
        // Aus: auch doppelte Leerzeichen bleiben unangetastet.
        Assert.Equal("hallo  welt", SmartSpacing.Prepare("hallo  welt", 'a', enabled: false));
    }

    // MARK: - Führendes Leerzeichen

    [Fact]
    public void PrependsSpaceAfterWordCharacter()
    {
        Assert.Equal(" hallo", SmartSpacing.Prepare("hallo", 'a', enabled: true));
    }

    [Fact]
    public void PrependsSpaceAfterDigit()
    {
        Assert.Equal(" Euro", SmartSpacing.Prepare("Euro", '5', enabled: true));
    }

    [Fact]
    public void PrependsSpaceAfterSentencePunctuation()
    {
        // Nach einem Satzende gehört vor das neue Diktat ein Leerzeichen.
        Assert.Equal(" Neuer Satz", SmartSpacing.Prepare("Neuer Satz", '.', enabled: true));
    }

    [Fact]
    public void PrependsSpaceAfterClosingQuote()
    {
        // Schließende Anführungszeichen stehen NICHT im Öffnungs-Set des Originals.
        Assert.Equal(" und weiter", SmartSpacing.Prepare("und weiter", '”', enabled: true));
    }

    [Fact]
    public void NoSpaceWhenPrecedingCharacterUnknown()
    {
        // null = Feldanfang oder Kontext nicht lesbar → nicht raten.
        Assert.Equal("hallo", SmartSpacing.Prepare("hallo", null, enabled: true));
    }

    [Theory]
    [InlineData(' ')]
    [InlineData('\t')]
    [InlineData('\n')]
    [InlineData('\r')]
    public void NoSpaceAfterWhitespace(char preceding)
    {
        Assert.Equal("hallo", SmartSpacing.Prepare("hallo", preceding, enabled: true));
    }

    [Theory]
    [InlineData('(')]
    [InlineData('[')]
    [InlineData('{')]
    public void NoSpaceAfterOpeningBracket(char preceding)
    {
        Assert.Equal("hallo", SmartSpacing.Prepare("hallo", preceding, enabled: true));
    }

    [Theory]
    [InlineData('“')] // “
    [InlineData('„')] // „
    [InlineData('‘')] // ‘
    [InlineData('«')] // «
    public void NoSpaceAfterOpeningQuote(char preceding)
    {
        Assert.Equal("hallo", SmartSpacing.Prepare("hallo", preceding, enabled: true));
    }

    [Theory]
    [InlineData('.')]
    [InlineData(',')]
    [InlineData(';')]
    [InlineData(':')]
    [InlineData('!')]
    [InlineData('?')]
    [InlineData(')')]
    [InlineData(']')]
    [InlineData('}')]
    public void NoSpaceBeforeTextStartingWithAttachingPunctuation(char first)
    {
        // Satzzeichen, die ans vorige Wort anschließen, nie abtrennen —
        // auch wenn davor ein Wortzeichen steht.
        var text = first + " genau";
        Assert.Equal(text, SmartSpacing.Prepare(text, 'a', enabled: true));
    }

    [Fact]
    public void NoSpaceWhenTextStartsWithWhitespace()
    {
        Assert.Equal(" hallo", SmartSpacing.Prepare(" hallo", 'a', enabled: true));
    }

    [Fact]
    public void EmptyTextStaysEmpty()
    {
        Assert.Equal("", SmartSpacing.Prepare("", 'a', enabled: true));
    }

    // MARK: - Zusammenfassen von Leerzeichen/Tabs

    [Fact]
    public void CollapsesDoubleSpaces()
    {
        Assert.Equal("a b", SmartSpacing.Prepare("a  b", null, enabled: true));
    }

    [Fact]
    public void CollapsesLongerSpaceRuns()
    {
        Assert.Equal("a b c", SmartSpacing.Prepare("a    b   c", null, enabled: true));
    }

    [Fact]
    public void CollapsesTabRunsToSingleSpace()
    {
        Assert.Equal("a b", SmartSpacing.Prepare("a\t\tb", null, enabled: true));
    }

    [Fact]
    public void CollapsesMixedSpaceTabRuns()
    {
        Assert.Equal("a b", SmartSpacing.Prepare("a \t b", null, enabled: true));
    }

    [Fact]
    public void KeepsSingleTab()
    {
        // Der Lauf braucht mindestens zwei Zeichen — ein einzelner Tab bleibt.
        Assert.Equal("a\tb", SmartSpacing.Prepare("a\tb", null, enabled: true));
    }

    [Fact]
    public void DoesNotCollapseNewlines()
    {
        Assert.Equal("a\n\nb", SmartSpacing.Prepare("a\n\nb", null, enabled: true));
    }

    [Fact]
    public void CollapseHappensBeforeLeadingSpaceDecision()
    {
        // Zwei führende Leerzeichen → erst zu einem zusammengefasst, das erste
        // Zeichen ist dann Whitespace → kein zusätzliches Leerzeichen davor.
        Assert.Equal(" hallo", SmartSpacing.Prepare("  hallo", 'a', enabled: true));
    }

    [Fact]
    public void CombinesCollapseAndLeadingSpace()
    {
        Assert.Equal(" hallo welt", SmartSpacing.Prepare("hallo  welt", 'a', enabled: true));
    }
}
