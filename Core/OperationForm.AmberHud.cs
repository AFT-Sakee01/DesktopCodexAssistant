using System.Drawing;

// AmberHud render variant (WidgetSettings.OperationRenderVariant == AmberHud): OLED-safe, no-blue
// restyle. Same button set/layout/icon glyphs as Classic (DrawOperationButtonsOled), recolored to a
// single amber hue instead of blue.
internal sealed partial class OperationForm
{
    private void DrawOperationWindowAmberHud(Graphics g)
    {
        ConfigureGraphics(g);
        DrawOperationButtonsOled(g, GetAmberHudButtonPalette());
    }
}
