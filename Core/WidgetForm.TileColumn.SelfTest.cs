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
        settings.BurnInProtectionEnabled = true;
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
                    "WidgetForm cold start must create exactly eleven metric tiles and one expand panel.");
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

            if (!form.ToggleSideSurfacesFromOperationPanel())
            {
                throw new InvalidOperationException("Operation double-click host action must enter physical side-surface hide state.");
            }

            if (form.operationForm == null ||
                form.operationForm.IsDisposed ||
                !form.operationForm.Visible ||
                !form.operationForm.AreLeftDockSurfacesHidden())
            {
                throw new InvalidOperationException(
                    "Physical side-surface hide must keep Operation visible while blocking its seven left-dock surfaces.");
            }

            for (int i = 0; i < form.metricTileForms.Count; i++)
            {
                if (form.metricTileForms[i] != null && form.metricTileForms[i].Visible)
                {
                    throw new InvalidOperationException(
                        "Physical side-surface hide left metric tile " + i + " visible and mouse-active.");
                }
            }

            if (form.metricTileExpandForm.Visible)
            {
                throw new InvalidOperationException(
                    "Physical side-surface hide must close the right-side expand panel.");
            }

            if (form.ToggleSideSurfacesFromOperationPanel() ||
                !form.operationForm.Visible ||
                form.operationForm.AreLeftDockSurfacesHidden())
            {
                throw new InvalidOperationException(
                    "A second Operation host toggle must restore side surfaces without hiding Operation.");
            }

            for (int i = 0; i < form.metricTileForms.Count; i++)
            {
                if (MetricTileForm.IsTileEnabled(settings, i) &&
                    (form.metricTileForms[i] == null || !form.metricTileForms[i].Visible))
                {
                    throw new InvalidOperationException(
                        "A second Operation host toggle did not restore enabled metric tile " + i + ".");
                }
            }

            form.UpdateInteractionTimer();
            if (!form.interactionTimer.Enabled)
            {
                throw new InvalidOperationException(
                    "Shared interaction timer must run for burn-in, click-through polling and radial idle collapse.");
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

            Rectangle anchor = new Rectangle(originalTiles[0].Location, originalTiles[0].Size);
            originalExpand.ShowForTile(MetricTileId.Cpu, anchor, new MetricTileFeed());
            if (!originalExpand.Visible)
            {
                throw new InvalidOperationException("Metric tile expand panel did not open for burn-in transition self-test.");
            }

            form.UpdateMetricTileBurnInPresentation(BurnInVisualLevel.LevelOne);
            if (!originalExpand.Visible)
            {
                throw new InvalidOperationException("Burn-in level one must not force-close the metric tile expand panel.");
            }

            form.UpdateMetricTileBurnInPresentation(BurnInVisualLevel.LevelTwo);
            if (originalExpand.Visible || form.hoveredMetricTileIndex != -1)
            {
                throw new InvalidOperationException(
                    "Entering burn-in level two must force-close the metric tile expand panel and clear its owner.");
            }

            form.Close();
        }
    }
}
