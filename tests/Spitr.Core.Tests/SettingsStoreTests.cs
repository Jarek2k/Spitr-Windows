using Spitr.Core.Overlay;
using Spitr.Core.Settings;
using Spitr.Core.Transcription;

namespace Spitr.Core.Tests;

public class SettingsStoreTests
{
    [Fact]
    public void DefaultsAreSane()
    {
        using var dir = new TempDir();
        var s = new SettingsStore(dir.Path);
        Assert.Equal("de-DE", s.LocaleIdentifier);
        Assert.Equal(HoldKey.RightCtrl, s.HoldKey);
        Assert.Equal(WhisperModelCatalog.DefaultModel, s.WhisperModel);
        Assert.Equal("", s.InputDeviceId);
        Assert.False(s.HasCompletedOnboarding);
        Assert.Equal(WaveformStyle.SignalReactive, s.WaveformStyle);
        Assert.Equal("", s.VocabularyText);
        Assert.False(s.IsPaused);
    }

    [Fact]
    public void ValuesPersistAcrossInstances()
    {
        using var dir = new TempDir();
        var first = new SettingsStore(dir.Path);
        first.LocaleIdentifier = "en-US";
        first.HoldKey = HoldKey.CapsLock;
        first.WhisperModel = "small";
        first.InputDeviceId = "mic-123";
        first.HasCompletedOnboarding = true;
        first.WaveformStyle = WaveformStyle.Kitt;
        first.VocabularyText = "Claude";

        var second = new SettingsStore(dir.Path);
        Assert.Equal("en-US", second.LocaleIdentifier);
        Assert.Equal(HoldKey.CapsLock, second.HoldKey);
        Assert.Equal("small", second.WhisperModel);
        Assert.Equal("mic-123", second.InputDeviceId);
        Assert.True(second.HasCompletedOnboarding);
        Assert.Equal(WaveformStyle.Kitt, second.WaveformStyle);
        Assert.Equal("Claude", second.VocabularyText);
    }

    [Fact]
    public void IsPausedIsNotPersisted()
    {
        using var dir = new TempDir();
        var first = new SettingsStore(dir.Path);
        first.IsPaused = true;
        // Flüchtiger Sitzungszustand darf nicht in einen frischen Start durchsickern.
        var second = new SettingsStore(dir.Path);
        Assert.False(second.IsPaused);
    }

    [Fact]
    public void VocabularyTrimsAndDropsEmptyLines()
    {
        using var dir = new TempDir();
        var s = new SettingsStore(dir.Path);
        s.VocabularyText = "Claude\n  Xcode  \n\n\t\nSwiftUI ";
        Assert.Equal(new[] { "Claude", "Xcode", "SwiftUI" }, s.Vocabulary);
    }

    [Fact]
    public void EmptyVocabularyIsEmptyArray()
    {
        using var dir = new TempDir();
        var s = new SettingsStore(dir.Path);
        s.VocabularyText = "   \n\n  ";
        Assert.Empty(s.Vocabulary);
    }
}
