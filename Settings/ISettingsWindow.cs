internal interface ISettingsWindow
{
    bool OwnerFormClosing { get; set; }
    bool TryConsumeUnsavedPreview(out WidgetSettings settings);
}
