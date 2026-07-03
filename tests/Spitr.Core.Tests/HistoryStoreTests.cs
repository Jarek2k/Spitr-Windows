using Spitr.Core.Settings;

namespace Spitr.Core.Tests;

public class HistoryStoreTests
{
    [Fact]
    public void RecordsNewestFirst()
    {
        using var dir = new TempDir();
        var h = new HistoryStore(dir.Path);
        h.Record("erste");
        h.Record("zweite");
        Assert.Equal(2, h.Entries.Count);
        Assert.Equal("zweite", h.Entries[0].Text);
        Assert.Equal("erste", h.Entries[^1].Text);
    }

    [Fact]
    public void IgnoresEmptyAndWhitespace()
    {
        using var dir = new TempDir();
        var h = new HistoryStore(dir.Path);
        h.Record("   ");
        h.Record("\n\t");
        h.Record("");
        Assert.Empty(h.Entries);
    }

    [Fact]
    public void TrimsStoredText()
    {
        using var dir = new TempDir();
        var h = new HistoryStore(dir.Path);
        h.Record("  hallo welt  ");
        Assert.Equal("hallo welt", h.Entries[0].Text);
    }

    [Fact]
    public void CappedAtHundred()
    {
        using var dir = new TempDir();
        var h = new HistoryStore(dir.Path);
        for (var i = 0; i < 105; i++) h.Record($"eintrag {i}");
        Assert.Equal(100, h.Entries.Count);
        // Neueste bleiben, älteste fliegen raus.
        Assert.Equal("eintrag 104", h.Entries[0].Text);
        Assert.DoesNotContain(h.Entries, e => e.Text == "eintrag 0");
    }

    [Fact]
    public void DisabledDoesNotRecordButKeepsExisting()
    {
        using var dir = new TempDir();
        var h = new HistoryStore(dir.Path);
        h.Record("behalten");
        h.Enabled = false;
        h.Record("verworfen");
        Assert.Single(h.Entries);
        Assert.Equal("behalten", h.Entries[0].Text);
    }

    [Fact]
    public void DeleteRemovesOnlyTheEntry()
    {
        using var dir = new TempDir();
        var h = new HistoryStore(dir.Path);
        h.Record("a");
        h.Record("b");
        var target = h.Entries.First(e => e.Text == "a");
        h.Delete(target.Id);
        Assert.Equal(new[] { "b" }, h.Entries.Select(e => e.Text));
    }

    [Fact]
    public void UpdateChangesTextKeepingIdentity()
    {
        using var dir = new TempDir();
        var h = new HistoryStore(dir.Path);
        h.Record("Klode");
        var original = h.Entries[0];
        h.Update(original.Id, "  Claude  ");
        var updated = h.Entries[0];
        Assert.Equal("Claude", updated.Text);   // getrimmt
        Assert.Equal(original.Id, updated.Id);  // derselbe Eintrag
        Assert.Equal(original.Date, updated.Date);
    }

    [Fact]
    public void UpdateIgnoresEmptyText()
    {
        using var dir = new TempDir();
        var h = new HistoryStore(dir.Path);
        h.Record("bleibt");
        h.Update(h.Entries[0].Id, "   ");
        Assert.Equal("bleibt", h.Entries[0].Text);
    }

    [Fact]
    public void ClearEmptiesEverything()
    {
        using var dir = new TempDir();
        var h = new HistoryStore(dir.Path);
        h.Record("a");
        h.Record("b");
        h.Clear();
        Assert.Empty(h.Entries);
    }

    [Fact]
    public void EntriesAndFlagPersist()
    {
        using var dir = new TempDir();
        var first = new HistoryStore(dir.Path);
        first.Record("bleibt");
        first.Enabled = false;

        var second = new HistoryStore(dir.Path);
        Assert.Equal(new[] { "bleibt" }, second.Entries.Select(e => e.Text));
        Assert.False(second.Enabled);
    }

    [Fact]
    public void EnabledByDefault()
    {
        using var dir = new TempDir();
        Assert.True(new HistoryStore(dir.Path).Enabled);
    }
}
