using System.Drawing;

// Typographic render variant (WidgetSettings.OperationRenderVariant == Typographic): OLED-safe,
// no-blue restyle. Same button set/layout/icon glyphs as Classic (DrawOperationButtonsOled), recolored
// to a neutral warm palette with a soft green "active" state instead of blue.
internal sealed partial class OperationForm
{
    private void DrawOperationWindowTypographic(Graphics g)
    {
        ConfigureGraphics(g);
        DrawOperationButtonsOled(g, GetTypographicButtonPalette());
    }
}
