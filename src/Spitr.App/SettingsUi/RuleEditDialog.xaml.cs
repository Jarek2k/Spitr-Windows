using System.Windows;
using System.Windows.Controls;

namespace Spitr.App.SettingsUi;

/// <summary>
/// Modal-Dialog zum Anlegen/Bearbeiten einer Wörterbuch-Regel. Liefert bei
/// DialogResult == true die getrimmten Felder <see cref="Pattern"/> und
/// <see cref="Replacement"/>; leeres Pattern ist nicht speicherbar (eine leere
/// Ersetzung schon — das löscht das erkannte Wort).
/// </summary>
public partial class RuleEditDialog : Window
{
    public RuleEditDialog(string title, string pattern, string replacement)
    {
        InitializeComponent();
        Title = title;
        PatternBox.Text = pattern;
        ReplacementBox.Text = replacement;
        UpdateSaveEnabled();
        Loaded += (_, _) => PatternBox.Focus();
    }

    /// <summary>Der zu ersetzende Begriff (getrimmt).</summary>
    public string Pattern => PatternBox.Text.Trim();

    /// <summary>Der Ersatztext (getrimmt).</summary>
    public string Replacement => ReplacementBox.Text.Trim();

    private void OnTextChanged(object sender, TextChangedEventArgs e) => UpdateSaveEnabled();

    private void UpdateSaveEnabled() => SaveButton.IsEnabled = Pattern.Length > 0;

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (Pattern.Length == 0) return;
        DialogResult = true;
    }
}
