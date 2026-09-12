using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

// Startup entrance for the right tile column and the operation panel. Every visible surface used to
// appear in the same frame: correct, but it read as a pop rather than a start.
//
// The motion is deliberately cheap. It only moves the UpdateLayeredWindow destination and scales the
// per-window alpha, so no surface redraws its content while the intro runs.
internal sealed partial class WidgetForm
{
    // 每枚磁贴错开 32ms、单枚 280ms，十一枚合计 600ms；操作面板在磁贴过半时起步，640ms 收尾。
    // 再长就从「入场」变成「等待」了。
    private const int IntroStaggerMs = 32;
    private const int IntroSurfaceDurationMs = 280;
    private const int IntroOperationDelayMs = 360;
    private const int IntroFrameIntervalMs = 16;
    // 磁贴贴着屏幕右缘，所以从更靠外的位置滑入；操作面板在左下角，从下方浮起。
    private const int IntroTileTravelPixels = 26;
    private const int IntroOperationTravelPixels = 14;

    private bool introCompleted;
    private bool startupCompleted;

    // 纯函数，便于自检：给定经过时间与该表面的起始延迟，返回 0..1 的缓出进度。
    // 缓出三次曲线——起步快、收尾稳，是这种级联入场最不晕的一条。
    internal static float ResolveIntroProgress(double elapsedMs, int delayMs, int durationMs)
    {
        if (durationMs <= 0)
        {
            return 1.0f;
        }

        double linear = (elapsedMs - delayMs) / durationMs;
        if (linear <= 0.0)
        {
            return 0.0f;
        }

        if (linear >= 1.0)
        {
            return 1.0f;
        }

        double inverted = 1.0 - linear;
        return (float)(1.0 - inverted * inverted * inverted);
    }

    internal static int ResolveIntroTileDelayMs(int tileIndex)
    {
        return Math.Max(0, tileIndex) * IntroStaggerMs;
    }

    internal static int ResolveIntroTotalMs(int tileCount)
    {
        int tiles = ResolveIntroTileDelayMs(Math.Max(1, tileCount) - 1) + IntroSurfaceDurationMs;
        int operation = IntroOperationDelayMs + IntroSurfaceDurationMs;
        return Math.Max(tiles, operation);
    }

    private bool IntroAnimationAllowed
    {
        get { return this.CurrentSettings != null && this.CurrentSettings.StartupIntroAnimationEnabled; }
    }

    // Called once, right after the tile column and the operation panel first become visible.
    private void StartStartupIntro()
    {
        if (this.introCompleted)
        {
            return;
        }

        this.introCompleted = true;
        if (!IntroAnimationAllowed)
        {
            CompleteStartupAfterIntro();
            return;
        }

        // 帧由这里同步驱动，不走 WinForms 定时器。分层窗口靠 UpdateLayeredWindow 直接更新、
        // 不经过 WM_PAINT，所以入场根本不需要消息循环；而启动期这条线程被各看板的维护定时器
        // 和主采样定时器占满，实测定时器方案的首帧要等 645ms，动画完全没机会播。
        // 同步驱动期间 UI 线程确实被占住 640ms——但那正是「入场」本身，之后启动工作立刻继续。
        // UiHangWatchdog 的阈值是 10 秒，这点时长远不触发。
        int total = ResolveIntroTotalMs(this.metricTileForms.Count);
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        ApplyIntroFrame(0.0);
        double elapsed = 0.0;
        while (elapsed < total && !this.formClosing && !this.IsDisposed)
        {
            Thread.Sleep(IntroFrameIntervalMs);
            elapsed = watch.Elapsed.TotalMilliseconds;
            ApplyIntroFrame(elapsed);
        }

        FinishStartupIntro();
        Program.LogInfo("Startup intro played. Tiles=" + this.metricTileForms.Count +
            ", ScheduledMs=" + total + ", ActualMs=" + (int)watch.Elapsed.TotalMilliseconds);
    }

    private void ApplyIntroFrame(double elapsedMs)
    {
        float travelScale = ResolveIntroTravelScale();
        for (int i = 0; i < this.metricTileForms.Count; i++)
        {
            MetricTileForm tile = this.metricTileForms[i];
            if (tile == null || tile.IsDisposed || !tile.Visible)
            {
                continue;
            }

            float progress = ResolveIntroProgress(elapsedMs, ResolveIntroTileDelayMs(i), IntroSurfaceDurationMs);
            int offsetX = (int)Math.Round(IntroTileTravelPixels * (1.0f - progress) * travelScale);
            tile.ApplyIntroFrame(new Point(offsetX, 0), progress);
        }

        if (this.operationForm != null && !this.operationForm.IsDisposed && this.operationForm.Visible)
        {
            float progress = ResolveIntroProgress(elapsedMs, IntroOperationDelayMs, IntroSurfaceDurationMs);
            int offsetY = (int)Math.Round(IntroOperationTravelPixels * (1.0f - progress) * travelScale);
            this.operationForm.ApplyIntroFrame(new Point(0, offsetY), progress);
        }
    }

    // 位移量按当前缩放走，否则高 DPI 下这段位移会显得过短。
    private float ResolveIntroTravelScale()
    {
        MetricTileForm probe = this.metricTileForms.Count > 0 ? this.metricTileForms[0] : null;
        return probe == null ? 1.0f : Math.Max(0.5f, probe.IntroTravelScale);
    }

    private void FinishStartupIntro()
    {
        for (int i = 0; i < this.metricTileForms.Count; i++)
        {
            MetricTileForm tile = this.metricTileForms[i];
            if (tile != null && !tile.IsDisposed)
            {
                tile.ClearIntroFrame();
            }
        }

        if (this.operationForm != null && !this.operationForm.IsDisposed)
        {
            this.operationForm.ClearIntroFrame();
        }

        CompleteStartupAfterIntro();
    }

    internal static void RunStartupIntroSelfTest()
    {
        if (ResolveIntroProgress(0, 0, IntroSurfaceDurationMs) != 0.0f ||
            ResolveIntroProgress(IntroSurfaceDurationMs, 0, IntroSurfaceDurationMs) != 1.0f ||
            ResolveIntroProgress(-50, 0, IntroSurfaceDurationMs) != 0.0f ||
            ResolveIntroProgress(10000, 0, IntroSurfaceDurationMs) != 1.0f)
        {
            throw new InvalidOperationException("Startup intro progress must clamp to an exact 0 and 1 at its ends.");
        }

        // 缓出：必须单调不减，且中点已经过半——否则观感会是「先慢后猛」。
        float previous = -1.0f;
        for (int ms = 0; ms <= IntroSurfaceDurationMs; ms += 8)
        {
            float value = ResolveIntroProgress(ms, 0, IntroSurfaceDurationMs);
            if (value < previous)
            {
                throw new InvalidOperationException("Startup intro easing must be monotonic.");
            }

            previous = value;
        }

        if (!(ResolveIntroProgress(IntroSurfaceDurationMs / 2, 0, IntroSurfaceDurationMs) > 0.5f))
        {
            throw new InvalidOperationException("Startup intro easing must be ease-out, not linear or ease-in.");
        }

        if (ResolveIntroTileDelayMs(0) != 0 || ResolveIntroTileDelayMs(10) != 10 * IntroStaggerMs)
        {
            throw new InvalidOperationException("Startup intro must stagger by tile index.");
        }

        // 总时长取「最后一枚磁贴」与「操作面板」两条线里晚的那条——操作面板起步晚、收尾也晚，
        // 只按磁贴算会把它的最后一帧截掉。
        int total = ResolveIntroTotalMs(11);
        if (total != Math.Max(10 * IntroStaggerMs + IntroSurfaceDurationMs, IntroOperationDelayMs + IntroSurfaceDurationMs))
        {
            throw new InvalidOperationException("Startup intro total must cover whichever surface finishes last, got " + total);
        }

        if (ResolveIntroProgress(total, ResolveIntroTileDelayMs(10), IntroSurfaceDurationMs) != 1.0f ||
            ResolveIntroProgress(total, IntroOperationDelayMs, IntroSurfaceDurationMs) != 1.0f)
        {
            throw new InvalidOperationException("Startup intro must finish every surface by its total duration.");
        }

        // 入场是同步驱动的，这段时长直接加在启动耗时上，所以给它一个明确的上限。
        if (total > 900)
        {
            throw new InvalidOperationException("Startup intro must stay under 900ms of synchronous startup cost, got " + total);
        }

        Console.WriteLine("Startup intro: PASS ease-out clamp, monotonic, per-tile stagger, exact completion at " + total + "ms");
    }
}
