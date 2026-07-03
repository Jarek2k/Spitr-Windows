using Spitr.Core.Settings;
using Spitr.Core.Text;

namespace Spitr.Core.Tests;

public class TextReplacementServiceTests
{
    private readonly TextReplacementService _service = new();

    private static ReplacementRule Rule(string pattern, string replacement) =>
        new(Guid.NewGuid(), pattern, replacement);

    [Fact]
    public void ReplacesWholeWordCaseInsensitively()
    {
        var rules = new[] { Rule("klode", "Claude") };
        Assert.Equal("Frag mal Claude.", _service.Apply(rules, "Frag mal Klode."));
    }

    [Fact]
    public void DoesNotReplaceInsideOtherWords()
    {
        var rules = new[] { Rule("git", "GitHub") };
        Assert.Equal("digital", _service.Apply(rules, "digital"));
    }

    [Fact]
    public void AppliesRulesInOrder()
    {
        var rules = new[]
        {
            Rule("a", "b"),
            Rule("b", "c"),
        };
        Assert.Equal("c", _service.Apply(rules, "a"));
    }

    [Fact]
    public void IgnoresEmptyPattern()
    {
        var rules = new[] { Rule("   ", "x") };
        Assert.Equal("hallo", _service.Apply(rules, "hallo"));
    }

    [Fact]
    public void TreatsReplacementLiterally()
    {
        var rules = new[] { Rule("preis", "$5") };
        Assert.Equal("der $5", _service.Apply(rules, "der Preis"));
    }

    [Fact]
    public void HandlesMultiWordPattern()
    {
        var rules = new[] { Rule("swift ui", "SwiftUI") };
        Assert.Equal("ich mag SwiftUI sehr", _service.Apply(rules, "ich mag Swift UI sehr"));
    }
}
