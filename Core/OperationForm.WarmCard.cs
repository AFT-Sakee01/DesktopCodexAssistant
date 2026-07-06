using System.Drawing;

// WarmCard render variant (WidgetSettings.OperationRenderVariant == WarmCard): OLED-safe, no-blue
// restyle. Same button set/layout/icon glyphs as Classic (DrawOperationButtonsOled), recolored to
// low-luminance warm-gray fills instead of blue.
internal sealed partial class OperationForm
{
    private void DrawOperationWindowWarmCard(Graphics g)
    {
        ConfigureGraphics(g);
        DrawOperationButtonsOled(g, GetWarmCardButtonPalette());
    }
}
