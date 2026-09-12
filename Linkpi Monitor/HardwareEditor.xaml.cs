using System.Windows;
using System.Windows.Controls;
using System.Text.Json;

namespace Linkpi_Monitor;

public partial class HardwareEditor : UserControl
{
    private string _baseline = string.Empty;

    public HardwareEditor()
    {
        InitializeComponent();
    }

    public Func<HardwareConfiguration, Task>? SaveAsync { get; set; }
    public bool IsSaving { get; private set; }
    public bool HasUnsavedChanges => EditorRoot.DataContext is HardwareConfiguration configuration &&
        JsonSerializer.Serialize(configuration) != _baseline;

    public bool ConfirmDiscardChanges(Window owner)
    {
        if (IsSaving)
        {
            MessageBox.Show(owner, "Wait for the hardware configuration save to finish before navigating away.",
                "Save in progress", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        return !HasUnsavedChanges || MessageBox.Show(owner,
            "Discard the unsaved hardware configuration changes?", "Unsaved changes",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private void UserControl_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not HardwareConfiguration configuration) return;
        var editable = configuration.CreateEditableCopy();
        EditorRoot.DataContext = editable;
        _baseline = JsonSerializer.Serialize(editable);
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (SaveAsync is null || EditorRoot.DataContext is not HardwareConfiguration configuration)
        {
            return;
        }

        var owner = Window.GetWindow(this);
        var validationErrors = ConfigurationValidator.Validate(configuration);
        if (validationErrors.Count > 0)
        {
            MessageBox.Show(owner, ConfigurationValidator.Format(validationErrors), "Check configuration",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (MessageBox.Show(
                owner,
                "Apply the displayed physical audio and video-output configuration to the selected LinkPi?",
                "Save hardware configuration",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        SaveButton.IsEnabled = false;
        IsSaving = true;
        try
        {
            await SaveAsync(configuration);
            _baseline = JsonSerializer.Serialize(EditorRoot.DataContext);
        }
        catch (Exception exception)
        {
            MessageBox.Show(owner, $"Could not save the hardware configuration.\n\n{exception.Message}",
                "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsSaving = false;
            SaveButton.IsEnabled = true;
        }
    }
}
