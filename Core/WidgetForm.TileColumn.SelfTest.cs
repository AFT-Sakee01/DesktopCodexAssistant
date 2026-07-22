using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

// Regression test for the hidden host / live tile-runtime separation on a real WidgetForm. The host
// window must never be shown, but hiddenForFullscreen still belongs exclusively to environmental
// visibility because it gates the shared interaction timer and the PDH control-tick rate.
internal sealed partial class WidgetForm
{
    internal static void RunTileColumnRuntimeSelfTest()
    {
        using (PdhSampler sampler = new PdhSampler())
        using (EventWaitHandle stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset))
        {
            AssertRuntimeAlive(sampler, stopEvent);
        }

        Console.WriteLine("Widget tile-column runtime: PASS hidden host with live interaction and sampling runtime");
    }

    private static void AssertRuntimeAlive(PdhSampler sampler, EventWaitHandle stopEvent)
    {
        WidgetSettings settings = WidgetSettings.CreateDefaults();
        // Hover opacity must be on for the shared interaction timer to have anything to do; this is
        // the subsystem whose death made hidden mode inescapable.
        settings.HoverOpacityEnabled = true;
        settings.Normalize();

        using (WidgetForm form = new WidgetForm(sampler, stopEvent, settings, false))
        {
            form.Show();
            // Shown can be queued behind the first message-pump turn. Invoke the idempotent
            // production startup path directly: Application.DoEvents() is invalid here because
            // the live tile/Dock timers can continuously replenish the queue and never return.
            form.StartChildWindowLifecycle();
            if (form.metricTileForms.Count != MetricTileModel.AllTileCount ||
                form.metricTileExpandForm == null ||
                form.metricTileExpandForm.IsDisposed)
            {
                throw new InvalidOperationException(
                    "WidgetForm cold start must create exactly ten metric tiles and one expand panel.");
            }

            MetricTileForm[] originalTiles = form.metricTileForms.ToArray();
            MetricTileExpandForm originalExpand = form.metricTileExpandForm;
            for (int i = 0; i < 20; i++)
            {
                form.ApplyRuntimeSettings(settings);
            }

            if (form.metricTileForms.Count != MetricTileModel.AllTileCount ||
                !object.ReferenceEquals(originalExpand, form.metricTileExpandForm))
            {
                throw new InvalidOperationException(
                    "Repeated settings application must not recreate the tile column or expand panel.");
            }

            for (int i = 0; i < originalTiles.Length; i++)
            {
                if (!object.ReferenceEquals(originalTiles[i], form.metricTileForms[i]))
                {
                    throw new InvalidOperationException(
                        "Repeated settings application recreated metric tile " + i + ".");
                }
            }

            form.UpdateVisibilityForMode();

            if (form.hiddenForFullscreen)
            {
                throw new InvalidOperationException(
                    "WidgetForm.hiddenForFullscreen must stay false when policy allows tiles; it gates timers, sampling and the shared interaction tick.");
            }

            form.UpdateHoverAnimationTimer();
            if (!form.hoverTimer.Enabled)
            {
                throw new InvalidOperationException(
                    "Shared interaction timer must run for the hidden host; without it auto-hide never clears and hidden mode cannot be exited.");
            }

            // The control tick must also stay at the interactive rate rather than the hidden-window
            // fallback, or the tile column updates several seconds late.
            int interval = form.GetCurrentWidgetTimerIntervalMs();
            int expected = WidgetSettings.GetWidgetSampleIntervalMs(
                WidgetSettings.GetEffectivePerformanceMode(settings.PerformanceMode));
            if (interval != expected)
            {
                throw new InvalidOperationException(
                    "Hidden host control tick interval was " + interval + " ms, expected the interactive " + expected + " ms.");
            }

            form.Close();
        }
    }
}
