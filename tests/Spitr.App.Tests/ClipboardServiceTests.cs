using System.Runtime.InteropServices;
using System.Text;
using Spitr.App.Win32;

namespace Spitr.App.Tests;

// Die Zwischenablage ist eine prozessweite (sogar systemweite) Ressource —
// alle Tests, die sie anfassen, laufen in dieser einen Collection strikt
// nacheinander, sonst zerschießen sie sich gegenseitig den Zustand.
[CollectionDefinition("Clipboard", DisableParallelization = true)]
public sealed class ClipboardCollectionDefinition;

/// <summary>
/// Laufzeit-Tests gegen die echte Windows-Zwischenablage. Sie laufen nur auf
/// dem Windows-CI-Runner (interaktive Session); auf macOS werden sie zwar
/// kompiliert, können aber nicht ausgeführt werden.
/// </summary>
[Collection("Clipboard")]
public sealed class ClipboardServiceTests
{
    private const uint CF_UNICODETEXT = 13;

    [Fact]
    public void SetSnapshotRestore_RoundTripsText()
    {
        var clipboard = new ClipboardService();
        Assert.True(clipboard.TrySetText("Spitr Roundtrip Alpha", concealFromHistory: false));

        var snapshot = clipboard.TrySnapshot();
        Assert.NotNull(snapshot);
        Assert.Equal("Spitr Roundtrip Alpha", UnicodeTextOf(snapshot));

        // Anderen Text setzen, dann den Snapshot zurückspielen.
        Assert.True(clipboard.TrySetText("etwas anderes", concealFromHistory: false));
        var replaced = clipboard.TrySnapshot();
        Assert.NotNull(replaced);
        Assert.Equal("etwas anderes", UnicodeTextOf(replaced));

        Assert.True(clipboard.TryRestore(snapshot));
        var restored = clipboard.TrySnapshot();
        Assert.NotNull(restored);
        Assert.Equal("Spitr Roundtrip Alpha", UnicodeTextOf(restored));
    }

    [Fact]
    public void SetText_Concealed_MarksAllThreeExclusionFormats()
    {
        var clipboard = new ClipboardService();
        Assert.True(clipboard.TrySetText("geheimes Diktat", concealFromHistory: true));

        AssertFormatOnClipboard("ExcludeClipboardContentFromMonitorProcessing");
        AssertFormatOnClipboard("CanIncludeInClipboardHistory");
        AssertFormatOnClipboard("CanUploadToCloudClipboard");

        // Aufräumen: unmarkierten, harmlosen Inhalt hinterlassen.
        Assert.True(clipboard.TrySetText("aufgeräumt", concealFromHistory: false));
    }

    [Fact]
    public void Snapshot_OfEmptyClipboard_RestoresToEmpty()
    {
        // Zwischenablage direkt per Win32 leeren.
        Assert.True(Native.OpenClipboard(IntPtr.Zero));
        try
        {
            Assert.True(Native.EmptyClipboard());
        }
        finally
        {
            Assert.True(Native.CloseClipboard());
        }

        var clipboard = new ClipboardService();
        var snapshot = clipboard.TrySnapshot();
        Assert.NotNull(snapshot);
        Assert.Empty(snapshot.Items);

        // Zwischendurch etwas setzen, dann den leeren Snapshot zurückspielen.
        Assert.True(clipboard.TrySetText("Zwischenstand", concealFromHistory: false));
        Assert.True(clipboard.TryRestore(snapshot));

        var after = clipboard.TrySnapshot();
        Assert.NotNull(after);
        Assert.Empty(after.Items);
    }

    /// <summary>Dekodiert den CF_UNICODETEXT-Eintrag eines Snapshots (ohne Null-Terminator).</summary>
    private static string UnicodeTextOf(ClipboardService.Snapshot snapshot)
    {
        var entry = Assert.Single(snapshot.Items, item => item.Format == CF_UNICODETEXT);
        return Encoding.Unicode.GetString(entry.Bytes).TrimEnd('\0');
    }

    private static void AssertFormatOnClipboard(string formatName)
    {
        var format = Native.RegisterClipboardFormatW(formatName);
        Assert.NotEqual(0u, format);
        Assert.True(Native.IsClipboardFormatAvailable(format), $"Format '{formatName}' fehlt auf der Zwischenablage");
    }

    /// <summary>
    /// Handgeschriebene P/Invokes nur für die Verifikation — das Testprojekt
    /// hat keinen Zugriff auf die (internen) CsWin32-Stubs der App.
    /// </summary>
    private static class Native
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        internal static extern uint RegisterClipboardFormatW(string lpszFormat);

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsClipboardFormatAvailable(uint format);

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EmptyClipboard();

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseClipboard();
    }
}

/// <summary>
/// Nur ein Rauchtest: die Wirkung von SendInput ist headless nicht
/// beobachtbar — hier zählt, dass der Aufruf durchläuft, ohne zu werfen.
/// In derselben Collection, weil der synthetische Strg+V den aktuellen
/// Zwischenablage-Inhalt in ein zufällig fokussiertes Fenster einfügen könnte.
/// </summary>
[Collection("Clipboard")]
public sealed class InputSenderTests
{
    [Fact]
    public void SendCtrlV_DoesNotThrow()
    {
        InputSender.SendCtrlV();
    }
}
