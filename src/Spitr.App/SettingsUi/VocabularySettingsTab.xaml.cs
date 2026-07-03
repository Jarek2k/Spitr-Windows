using System.Windows.Controls;
using Spitr.Core.Settings;

namespace Spitr.App.SettingsUi;

/// <summary>
/// Der Tab „Vokabular" — Port von VocabularySettingsView.swift. Die TextBox
/// bindet direkt an <see cref="SettingsStore.VocabularyText"/>; der Store
/// persistiert bei jeder Änderung selbst.
/// </summary>
public partial class VocabularySettingsTab : UserControl
{
    public VocabularySettingsTab(SettingsStore settings)
    {
        InitializeComponent();
        DataContext = settings;
    }
}
