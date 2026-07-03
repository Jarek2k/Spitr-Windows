using Spitr.Core.Settings;

namespace Spitr.Core.Tests;

public class DictionaryStoreTests
{
    [Fact]
    public void DisabledByDefault()
    {
        using var dir = new TempDir();
        Assert.False(new DictionaryStore(dir.Path).Enabled);
    }

    [Fact]
    public void AddAppendsEmptyRule()
    {
        using var dir = new TempDir();
        var d = new DictionaryStore(dir.Path);
        d.Add();
        Assert.Single(d.Rules);
        Assert.Equal("", d.Rules[0].Pattern);
        Assert.Equal("", d.Rules[0].Replacement);
    }

    [Fact]
    public void UpdateChangesRuleById()
    {
        using var dir = new TempDir();
        var d = new DictionaryStore(dir.Path);
        d.Add();
        var id = d.Rules[0].Id;
        d.Update(new ReplacementRule(id, "klode", "Claude"));
        Assert.Equal("klode", d.Rules[0].Pattern);
        Assert.Equal("Claude", d.Rules[0].Replacement);
        Assert.Equal(id, d.Rules[0].Id);
    }

    [Fact]
    public void AddPopulatedAppendsTrimmedRule()
    {
        using var dir = new TempDir();
        var d = new DictionaryStore(dir.Path);
        d.Add("  Klode ", " Claude ");
        Assert.Single(d.Rules);
        Assert.Equal("Klode", d.Rules[0].Pattern);
        Assert.Equal("Claude", d.Rules[0].Replacement);
    }

    [Fact]
    public void AddPopulatedUpdatesExistingPatternCaseInsensitively()
    {
        using var dir = new TempDir();
        var d = new DictionaryStore(dir.Path);
        d.Add("klode", "Claude");
        d.Add("KLODE", "Cloud");
        Assert.Single(d.Rules);                 // Upsert, kein Duplikat
        Assert.Equal("Cloud", d.Rules[0].Replacement);
    }

    [Fact]
    public void AddPopulatedIgnoresEmptyPattern()
    {
        using var dir = new TempDir();
        var d = new DictionaryStore(dir.Path);
        d.Add("   ", "x");
        Assert.Empty(d.Rules);
    }

    [Fact]
    public void DeleteRemovesRule()
    {
        using var dir = new TempDir();
        var d = new DictionaryStore(dir.Path);
        d.Add();
        d.Add();
        d.Delete(d.Rules[0].Id);
        Assert.Single(d.Rules);
    }

    [Fact]
    public void ActiveRulesRespectEnabledFlag()
    {
        using var dir = new TempDir();
        var d = new DictionaryStore(dir.Path);
        d.Add();
        d.Update(new ReplacementRule(d.Rules[0].Id, "a", "b"));
        Assert.Empty(d.ActiveRules);            // per Default deaktiviert
        d.Enabled = true;
        Assert.Single(d.ActiveRules);
    }

    [Fact]
    public void RulesAndFlagPersist()
    {
        using var dir = new TempDir();
        var first = new DictionaryStore(dir.Path);
        first.Add();
        first.Update(new ReplacementRule(first.Rules[0].Id, "x", "y"));
        first.Enabled = true;

        var second = new DictionaryStore(dir.Path);
        Assert.Single(second.Rules);
        Assert.Equal("x", second.Rules[0].Pattern);
        Assert.Equal("y", second.Rules[0].Replacement);
        Assert.True(second.Enabled);
    }
}
