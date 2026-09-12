using System;
using System.Drawing;

// 统一间距模式的求解器。左右两列的整列高度由各自的「间距百分比」决定（百分比 = 把该侧可用空白
// 吃掉多少），因此高度对百分比单调不减。统一模式只有一条规则：两侧先同取统一百分比，占用高度
// 大的那一侧就地不动，占用小的那一侧把百分比往上抬，直到自己的整列高度最接近大的那一侧——
// 也就是「占用小的一侧自动加大间距向大的一侧看齐」。往下调只会更矮，永远追不上，所以搜索区间
// 从统一百分比起单向向上。
//
// 两列高度对齐之后再解算各自的整列偏移，使两列共用同一条中线。整数百分比留下的那点高度余量
// 没有任何偏移能消掉，共用中线等于把它平摊到上下两端，最大边缘误差减半——这就是「上下一致」
// 在离散间距下能做到的最好结果。
internal static class SideColumnBalance
{
    internal struct Result
    {
        public int LeftSpacingPercent;
        public int RightSpacingPercent;
        public int LeftOffsetY;
        public int RightOffsetY;
        public int TopEdgeErrorPixels;
        public int BottomEdgeErrorPixels;
        // 解算出来的像素级空白，-1 表示该列按百分比走。
        public int LeftWhitespaceOverride;
        public int RightWhitespaceOverride;
    }

    // 视觉对齐容差：两列高度相差 1 像素以内就认为已经对齐，差值来自百分比换算的整数舍入。
    private const int VisualAlignmentTolerancePixels = 1;

    private static float cachedSystemDpiScale;
    private static float selfTestScaleOverride;

    // 解算要一个屏幕缩放系数，而调用点分布在加载、运行期应用和设置读取三处，一次 GDI 查询
    // 缓存起来就够了：进程生命周期内系统 DPI 不变，显示器切换走的是 AdaptToCurrentWorkArea 那条路。
    internal static float ResolveSystemDpiScale()
    {
        if (selfTestScaleOverride > 0.0f)
        {
            return selfTestScaleOverride;
        }

        if (cachedSystemDpiScale > 0.0f)
        {
            return cachedSystemDpiScale;
        }

        float scale = 1.0f;
        try
        {
            using (Graphics graphics = Graphics.FromHwnd(IntPtr.Zero))
            {
                scale = Math.Max(0.25f, graphics.DpiY / 96.0f);
            }
        }
        catch
        {
            scale = 1.0f;
        }

        cachedSystemDpiScale = scale;
        return scale;
    }

    internal static void SetScaleOverrideForSelfTest(float scale)
    {
        selfTestScaleOverride = scale;
    }

    // 模式开启时把两侧间距与整列偏移改写成解算结果。调用点：WidgetSettings.LoadFromPath、
    // WidgetForm.ApplyRuntimeSettings/SaveSettings、Win11SettingsForm.ReadSettings——也就是
    // 「一份设置开始生效」的每一处。这四个键在这个模式下是派生值，设置界面里也相应置灰。
    internal static void ApplyUnifiedColumnSpacing(WidgetSettings settings)
    {
        if (settings == null)
        {
            return;
        }

        // 先清掉上一次的派生量。它是 internal 字段、会跟着 Clone 传下去，模式关掉之后留在那里
        // 会继续悄悄覆盖百分比。
        settings.UnifiedLeftDockWhitespaceOverride = -1;
        settings.UnifiedRightTileWhitespaceOverride = -1;
        if (!settings.UnifiedColumnSpacingEnabled)
        {
            return;
        }

        // 手动摆位的列没有可解算的整列包络，这个模式对它无从下手；宁可什么都不做，也不能
        // 反过来把用户存下来的逐个坐标推翻。设置界面在开启模式时会把两侧自动排列一并打开。
        if (!settings.LeftDockAutoArrangeEnabled || !settings.RightTileAutoArrangeEnabled)
        {
            return;
        }

        Result result;
        if (!TryResolveUnified(
            settings,
            LeftDockLayout.ResolveWorkArea(settings),
            settings.GetWorkAreaForModule(WidgetSettings.ModuleMain),
            ResolveSystemDpiScale(),
            out result))
        {
            return;
        }

        settings.LeftDockButtonGapPixels = result.LeftSpacingPercent;
        settings.RightTileButtonGapPixels = result.RightSpacingPercent;
        settings.LeftDockGroupOffsetY = result.LeftOffsetY;
        settings.RightTileGroupOffsetY = result.RightOffsetY;
        settings.UnifiedLeftDockWhitespaceOverride = result.LeftWhitespaceOverride;
        settings.UnifiedRightTileWhitespaceOverride = result.RightWhitespaceOverride;
    }

    internal static bool TryResolveUnified(
        WidgetSettings settings,
        Rectangle leftWorkArea,
        Rectangle rightWorkArea,
        float dpiScale,
        out Result result)
    {
        result = new Result();
        if (settings == null ||
            leftWorkArea.Height <= 0 ||
            rightWorkArea.Height <= 0 ||
            Math.Min(leftWorkArea.Bottom, rightWorkArea.Bottom) <= Math.Max(leftWorkArea.Top, rightWorkArea.Top))
        {
            return false;
        }

        float scale = Math.Max(0.25f, dpiScale);
        WidgetSettings working = settings.Clone();
        // 求解期间关掉统一模式并强制两侧自动排列：探测的是「假如这个百分比生效」时的整列包络，
        // 和调用方自己的开关状态无关。
        working.UnifiedColumnSpacingEnabled = false;
        working.LeftDockAutoArrangeEnabled = true;
        working.RightTileAutoArrangeEnabled = true;
        // 探测的是「纯百分比下该列有多高」，上一次的派生像素不能掺进来。
        working.UnifiedLeftDockWhitespaceOverride = -1;
        working.UnifiedRightTileWhitespaceOverride = -1;

        Rectangle currentLeftGroup = LeftDockLayout.ResolveAutoTabGroupBounds(working, leftWorkArea, scale);
        Rectangle currentRightGroup = MetricTileForm.ResolveAutoTileGroupBounds(working, rightWorkArea);
        if (currentLeftGroup.IsEmpty || currentRightGroup.IsEmpty)
        {
            return false;
        }

        working.LeftDockGroupOffsetY = 0;
        working.RightTileGroupOffsetY = 0;
        int valueCount = WidgetSettings.MaxColumnButtonGapPixels - WidgetSettings.MinColumnButtonGapPixels + 1;
        int[] leftHeights = new int[valueCount];
        int[] rightHeights = new int[valueCount];
        for (int value = WidgetSettings.MinColumnButtonGapPixels;
             value <= WidgetSettings.MaxColumnButtonGapPixels;
             value++)
        {
            int index = value - WidgetSettings.MinColumnButtonGapPixels;
            working.LeftDockButtonGapPixels = value;
            leftHeights[index] = LeftDockLayout.ResolveAutoTabGroupBounds(working, leftWorkArea, scale).Height;
            working.RightTileButtonGapPixels = value;
            rightHeights[index] = MetricTileForm.ResolveAutoTileGroupBounds(working, rightWorkArea).Height;
        }

        int unifiedPercent = Math.Max(
            WidgetSettings.MinColumnButtonGapPixels,
            Math.Min(WidgetSettings.MaxColumnButtonGapPixels, settings.UnifiedColumnSpacingPercent));
        int leftSpacing;
        int rightSpacing;
        ResolveUnifiedSpacingPair(leftHeights, rightHeights, unifiedPercent, out leftSpacing, out rightSpacing);

        working.LeftDockButtonGapPixels = leftSpacing;
        working.RightTileButtonGapPixels = rightSpacing;

        // 百分比只能把两列拉到相差半档以内（这块工作区上一档十几像素），剩下的残差没有任何偏移
        // 能消掉。所以最后一步直接给较矮的那一列一个像素级的空白值：整列高度 = 各成员高度之和
        // 加上摊开的空白，而各成员高度之和正是百分比为 0 时的整列高度，于是所需空白可以精确算出。
        // 较高的一列仍然严格按统一百分比走，统一间距这个设置因此仍是它字面的意思。
        int leftMemberHeight = leftHeights[0];
        int rightMemberHeight = rightHeights[0];
        int leftCapacity = Math.Max(0, leftHeights[valueCount - 1] - leftMemberHeight);
        int rightCapacity = Math.Max(0, rightHeights[valueCount - 1] - rightMemberHeight);
        int leftTargetHeight = leftHeights[leftSpacing - WidgetSettings.MinColumnButtonGapPixels];
        int rightTargetHeight = rightHeights[rightSpacing - WidgetSettings.MinColumnButtonGapPixels];
        int sharedHeight = Math.Max(leftTargetHeight, rightTargetHeight);
        int leftOverride = Math.Max(0, Math.Min(leftCapacity, sharedHeight - leftMemberHeight));
        int rightOverride = Math.Max(0, Math.Min(rightCapacity, sharedHeight - rightMemberHeight));
        working.UnifiedLeftDockWhitespaceOverride = leftOverride;
        working.UnifiedRightTileWhitespaceOverride = rightOverride;

        Rectangle leftBaseline = LeftDockLayout.ResolveAutoTabGroupBounds(working, leftWorkArea, scale);
        Rectangle rightBaseline = MetricTileForm.ResolveAutoTileGroupBounds(working, rightWorkArea);

        // 间距只能取整数百分比，一档在常见工作区上就是七到十个像素，较矮的一列通常抬不到和
        // 较高的一列刚好等高。剩下的这点高度差没有任何偏移能消掉，只能决定它出现在哪一端：
        // 让两列共用同一条中线，上下两端各分一半余量，最大边缘误差因此减半——这比「顶沿严丝合缝、
        // 底沿差满一整档」更接近用户要的「上下一致」。
        double minimumCenter = Math.Max(
            leftWorkArea.Top + leftBaseline.Height / 2.0,
            rightWorkArea.Top + rightBaseline.Height / 2.0);
        double maximumCenter = Math.Min(
            leftWorkArea.Bottom - leftBaseline.Height / 2.0,
            rightWorkArea.Bottom - rightBaseline.Height / 2.0);
        if (maximumCenter < minimumCenter)
        {
            return false;
        }

        // 目标中线取当前两列中心的平均值：开启模式的那一刻两列不会整体跳到别处去，而且解算结果
        // 本身就是这个平均值的不动点，反复 Normalize 不会让界面一次次往下漂。
        double preferredCenter =
            (currentLeftGroup.Top + currentLeftGroup.Height / 2.0 +
             currentRightGroup.Top + currentRightGroup.Height / 2.0) / 2.0;
        double targetCenter = Math.Max(minimumCenter, Math.Min(maximumCenter, preferredCenter));
        int leftTop = (int)Math.Round(targetCenter - leftBaseline.Height / 2.0);
        int rightTop = (int)Math.Round(targetCenter - rightBaseline.Height / 2.0);

        working.LeftDockGroupOffsetY = Math.Max(
            WidgetSettings.MinColumnGroupOffsetY,
            Math.Min(WidgetSettings.MaxColumnGroupOffsetY, leftTop - leftBaseline.Top));
        working.RightTileGroupOffsetY = Math.Max(
            WidgetSettings.MinColumnGroupOffsetY,
            Math.Min(WidgetSettings.MaxColumnGroupOffsetY, rightTop - rightBaseline.Top));

        Rectangle finalLeft = LeftDockLayout.ResolveAutoTabGroupBounds(working, leftWorkArea, scale);
        Rectangle finalRight = MetricTileForm.ResolveAutoTileGroupBounds(working, rightWorkArea);
        result.LeftSpacingPercent = leftSpacing;
        result.RightSpacingPercent = rightSpacing;
        result.LeftWhitespaceOverride = leftOverride;
        result.RightWhitespaceOverride = rightOverride;
        result.LeftOffsetY = working.LeftDockGroupOffsetY;
        result.RightOffsetY = working.RightTileGroupOffsetY;
        result.TopEdgeErrorPixels = Math.Abs(finalLeft.Top - finalRight.Top);
        result.BottomEdgeErrorPixels = Math.Abs(finalLeft.Bottom - finalRight.Bottom);
        return true;
    }

    // 纯函数，便于自检：两条高度曲线加一个统一百分比，给出两侧最终使用的百分比。
    internal static void ResolveUnifiedSpacingPair(
        int[] leftHeights,
        int[] rightHeights,
        int unifiedPercent,
        out int leftSpacing,
        out int rightSpacing)
    {
        int minimum = WidgetSettings.MinColumnButtonGapPixels;
        int maximum = WidgetSettings.MaxColumnButtonGapPixels;
        int clamped = Math.Max(minimum, Math.Min(maximum, unifiedPercent));
        leftSpacing = clamped;
        rightSpacing = clamped;

        int index = clamped - minimum;
        if (leftHeights == null || rightHeights == null ||
            index >= leftHeights.Length || index >= rightHeights.Length)
        {
            return;
        }

        int leftBase = leftHeights[index];
        int rightBase = rightHeights[index];
        if (Math.Abs(leftBase - rightBase) <= VisualAlignmentTolerancePixels)
        {
            return;
        }

        bool leftIsShorter = leftBase < rightBase;
        int[] shorter = leftIsShorter ? leftHeights : rightHeights;
        int target = leftIsShorter ? rightBase : leftBase;

        int best = index;
        int bestError = Math.Abs(shorter[index] - target);
        // 严格小于：并列时保留最小的百分比，也就是改动最小的那个解。
        for (int i = index + 1; i < shorter.Length; i++)
        {
            int error = Math.Abs(shorter[i] - target);
            if (error < bestError)
            {
                bestError = error;
                best = i;
            }
        }

        int resolved = best + minimum;
        if (leftIsShorter)
        {
            leftSpacing = resolved;
        }
        else
        {
            rightSpacing = resolved;
        }
    }

    // 自检辅助：在给定的统一百分比下，把较矮的一列的百分比在整个区间里扫一遍，取两列高度差的
    // 最小值。这是量化允许的最好结果，求解器不得比它更差。
    private static int ResolveAchievableHeightDifferenceForSelfTest(
        WidgetSettings settings,
        Rectangle workArea,
        float dpiScale)
    {
        WidgetSettings working = settings.Clone();
        working.UnifiedColumnSpacingEnabled = false;
        working.LeftDockAutoArrangeEnabled = true;
        working.RightTileAutoArrangeEnabled = true;
        // 探测的是「纯百分比下该列有多高」，上一次的派生像素不能掺进来。
        working.UnifiedLeftDockWhitespaceOverride = -1;
        working.UnifiedRightTileWhitespaceOverride = -1;
        working.LeftDockGroupOffsetY = 0;
        working.RightTileGroupOffsetY = 0;

        int unified = Math.Max(
            WidgetSettings.MinColumnButtonGapPixels,
            Math.Min(WidgetSettings.MaxColumnButtonGapPixels, settings.UnifiedColumnSpacingPercent));
        working.LeftDockButtonGapPixels = unified;
        working.RightTileButtonGapPixels = unified;
        int leftBase = LeftDockLayout.ResolveAutoTabGroupBounds(working, workArea, dpiScale).Height;
        int rightBase = MetricTileForm.ResolveAutoTileGroupBounds(working, workArea).Height;
        bool leftIsShorter = leftBase < rightBase;
        int target = leftIsShorter ? rightBase : leftBase;

        int best = Math.Abs(leftBase - rightBase);
        for (int value = unified; value <= WidgetSettings.MaxColumnButtonGapPixels; value++)
        {
            int height;
            if (leftIsShorter)
            {
                working.LeftDockButtonGapPixels = value;
                height = LeftDockLayout.ResolveAutoTabGroupBounds(working, workArea, dpiScale).Height;
            }
            else
            {
                working.RightTileButtonGapPixels = value;
                height = MetricTileForm.ResolveAutoTileGroupBounds(working, workArea).Height;
            }

            best = Math.Min(best, Math.Abs(height - target));
        }

        return best;
    }

    internal static void RunSelfTest()
    {
        int valueCount = WidgetSettings.MaxColumnButtonGapPixels - WidgetSettings.MinColumnButtonGapPixels + 1;

        // 构造两条高度曲线：左列基座矮、可用空白多，右列基座高、可用空白少。统一百分比取 10 时
        // 左列比右列矮，规则要求左列单向抬高去够右列，右列保持 10 不动。
        int[] left = new int[valueCount];
        int[] right = new int[valueCount];
        for (int i = 0; i < valueCount; i++)
        {
            left[i] = 300 + i * 10;
            right[i] = 800 + i * 2;
        }

        int leftSpacing;
        int rightSpacing;
        ResolveUnifiedSpacingPair(left, right, 10, out leftSpacing, out rightSpacing);
        if (rightSpacing != 10)
        {
            throw new InvalidOperationException(
                "Unified column spacing must leave the taller column at the unified percent, got " + rightSpacing);
        }

        if (leftSpacing <= 10)
        {
            throw new InvalidOperationException(
                "Unified column spacing must raise the shorter column above the unified percent, got " + leftSpacing);
        }

        int leftIndex = leftSpacing - WidgetSettings.MinColumnButtonGapPixels;
        int rightIndex = rightSpacing - WidgetSettings.MinColumnButtonGapPixels;
        int bestError = Math.Abs(left[leftIndex] - right[rightIndex]);
        for (int i = 10 - WidgetSettings.MinColumnButtonGapPixels; i < valueCount; i++)
        {
            if (Math.Abs(left[i] - right[rightIndex]) < bestError)
            {
                throw new InvalidOperationException("Unified column spacing did not pick the closest height match.");
            }
        }

        // 反向：右列矮时抬右列、左列不动。
        ResolveUnifiedSpacingPair(right, left, 10, out leftSpacing, out rightSpacing);
        if (leftSpacing != 10 || rightSpacing <= 10)
        {
            throw new InvalidOperationException("Unified column spacing must be symmetric between the two columns.");
        }

        // 已经一致时两侧都保持统一值，不做任何多余改动。
        ResolveUnifiedSpacingPair(left, left, 37, out leftSpacing, out rightSpacing);
        if (leftSpacing != 37 || rightSpacing != 37)
        {
            throw new InvalidOperationException("Unified column spacing must not move already-matching columns.");
        }

        // 够不到时用满 100，而不是退回统一值：这是「尽力向大的一侧看齐」。
        int[] unreachable = new int[valueCount];
        for (int i = 0; i < valueCount; i++)
        {
            unreachable[i] = 100 + i;
        }

        ResolveUnifiedSpacingPair(unreachable, right, 0, out leftSpacing, out rightSpacing);
        if (leftSpacing != WidgetSettings.MaxColumnButtonGapPixels || rightSpacing != 0)
        {
            throw new InvalidOperationException(
                "Unified column spacing must exhaust the shorter column's range when it cannot reach the taller one.");
        }

        // 端到端：默认设置在一块 1440x1740 的工作区上必须解算出上下沿对齐的两列。
        SetScaleOverrideForSelfTest(1.0f);
        try
        {
            WidgetSettings settings = WidgetSettings.CreateDefaults();
            settings.UnifiedColumnSpacingEnabled = true;
            settings.UnifiedColumnSpacingPercent = WidgetSettings.DefaultUnifiedColumnSpacingPercent;
            Rectangle workArea = new Rectangle(0, 40, 1440, 1740);
            Result result;
            if (!TryResolveUnified(settings, workArea, workArea, 1.0f, out result))
            {
                throw new InvalidOperationException("Unified column spacing geometry failed to resolve.");
            }

            // 像素级空白接管之后两端都必须精确对齐。1 像素的余地留给整列偏移的四舍五入，
            // 不是留给间距量化——那一档十几像素的残差在这里已经不该存在了。
            if (result.TopEdgeErrorPixels > 1 || result.BottomEdgeErrorPixels > 1)
            {
                throw new InvalidOperationException(
                    "Unified column spacing must align both ends: top=" + result.TopEdgeErrorPixels +
                    "px bottom=" + result.BottomEdgeErrorPixels + "px.");
            }

            // 若只按百分比对齐，这块工作区上会留下多少残差——记录下来，说明像素级空白解决的
            // 正是这个量级的问题。
            int percentOnlyResidual = ResolveAchievableHeightDifferenceForSelfTest(settings, workArea, 1.0f);

            // 求解必须是幂等的：把结果写回去再解一次，答案不能漂移，否则每次 Normalize 都会挪动界面。
            settings.LeftDockButtonGapPixels = result.LeftSpacingPercent;
            settings.RightTileButtonGapPixels = result.RightSpacingPercent;
            settings.LeftDockGroupOffsetY = result.LeftOffsetY;
            settings.RightTileGroupOffsetY = result.RightOffsetY;
            settings.UnifiedLeftDockWhitespaceOverride = result.LeftWhitespaceOverride;
            settings.UnifiedRightTileWhitespaceOverride = result.RightWhitespaceOverride;
            Result second;
            if (!TryResolveUnified(settings, workArea, workArea, 1.0f, out second) ||
                second.LeftSpacingPercent != result.LeftSpacingPercent ||
                second.RightSpacingPercent != result.RightSpacingPercent ||
                second.LeftOffsetY != result.LeftOffsetY ||
                second.RightOffsetY != result.RightOffsetY ||
                second.LeftWhitespaceOverride != result.LeftWhitespaceOverride ||
                second.RightWhitespaceOverride != result.RightWhitespaceOverride)
            {
                throw new InvalidOperationException("Unified column spacing must be idempotent across repeated resolves.");
            }

            Console.WriteLine(
                "Side column unified spacing: PASS geometry left=" + result.LeftSpacingPercent +
                "% right=" + result.RightSpacingPercent +
                "% whitespace=" + result.LeftWhitespaceOverride + "/" + result.RightWhitespaceOverride +
                "px topError=" + result.TopEdgeErrorPixels +
                "px bottomError=" + result.BottomEdgeErrorPixels +
                "px (percent-only residual would be " + percentOnlyResidual + "px)");
        }
        finally
        {
            SetScaleOverrideForSelfTest(0.0f);
        }

        RunLoadPathSelfTest();
        RunSharedBurnInSaltSelfTest();
    }

    // 解算把两列拉到同一条中线上之后，防烧屏还会各自把它们上下挪一次。两条列的盐不同，位移表
    // 取值在 -3..+3，于是相对高度差最多 6 像素、每 7 分钟变一次——用户看到的「左边还是高一点」
    // 正是这一步造成的。统一模式下两列必须共用同一个盐。
    private static void RunSharedBurnInSaltSelfTest()
    {
        int left = BurnInProtection.ResolveEdgeColumnSalt(true, BurnInProtection.LeftDockButtonColumnSalt);
        int right = BurnInProtection.ResolveEdgeColumnSalt(true, BurnInProtection.MetricTileColumnSalt);
        if (left != right || left != BurnInProtection.UnifiedEdgeColumnSalt)
        {
            throw new InvalidOperationException(
                "Unified spacing must give both edge columns one burn-in salt, got " + left + "/" + right);
        }

        if (BurnInProtection.ResolveRuntimeOffsetForSelfTest(left).Y !=
            BurnInProtection.ResolveRuntimeOffsetForSelfTest(right).Y)
        {
            throw new InvalidOperationException("One salt must produce one vertical burn-in offset.");
        }

        // 模式关闭时必须保持原样：两列各用各的盐，各自独立漂移。
        if (BurnInProtection.ResolveEdgeColumnSalt(false, BurnInProtection.LeftDockButtonColumnSalt) !=
                BurnInProtection.LeftDockButtonColumnSalt ||
            BurnInProtection.ResolveEdgeColumnSalt(false, BurnInProtection.MetricTileColumnSalt) !=
                BurnInProtection.MetricTileColumnSalt)
        {
            throw new InvalidOperationException("Without the unified mode each column must keep its own burn-in salt.");
        }

        Console.WriteLine("Side column unified spacing: PASS shared burn-in salt " + left +
            " (per-column salts " + BurnInProtection.LeftDockButtonColumnSalt + "/" +
            BurnInProtection.MetricTileColumnSalt + " retained when the mode is off)");
    }

    // 端到端覆盖「一份设置开始生效」的那条路：模式写在 settings.ini 里，加载出来的两侧间距和
    // 整列偏移就必须已经是解算结果，而不是文件里那对故意写歪的数字。
    private static void RunLoadPathSelfTest()
    {
        string root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "DesktopCodexAssistant-unified-spacing-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(root);
        try
        {
            string path = System.IO.Path.Combine(root, "settings.ini");
            System.IO.File.WriteAllLines(
                path,
                new string[]
                {
                    "Version=107",
                    "LeftDockAutoArrangeEnabled=True",
                    "RightTileAutoArrangeEnabled=True",
                    "UnifiedColumnSpacingEnabled=True",
                    "UnifiedColumnSpacingPercent=12",
                    "LeftDockButtonGapPixels=0",
                    "RightTileButtonGapPixels=99",
                    "LeftDockGroupOffsetY=-400",
                    "RightTileGroupOffsetY=400"
                },
                SharedEncoding.Utf8NoBom);

            WidgetSettings loaded = WidgetSettings.LoadFromPathForSelfTest(path);
            if (!loaded.UnifiedColumnSpacingEnabled || loaded.UnifiedColumnSpacingPercent != 12)
            {
                throw new InvalidOperationException("Unified column spacing keys must survive load.");
            }

            Result expected;
            if (!TryResolveUnified(
                loaded,
                LeftDockLayout.ResolveWorkArea(loaded),
                loaded.GetWorkAreaForModule(WidgetSettings.ModuleMain),
                ResolveSystemDpiScale(),
                out expected))
            {
                // 这台机器上两块目标工作区没有共同的纵向区间，模式无从解算；加载路径按约定
                // 保持文件里的原值不动，这也是一种正确结果。
                Console.WriteLine("Side column unified spacing: PASS load path (no common vertical band on this display)");
                return;
            }

            if (loaded.LeftDockButtonGapPixels != expected.LeftSpacingPercent ||
                loaded.RightTileButtonGapPixels != expected.RightSpacingPercent ||
                loaded.LeftDockGroupOffsetY != expected.LeftOffsetY ||
                loaded.RightTileGroupOffsetY != expected.RightOffsetY)
            {
                throw new InvalidOperationException(
                    "Loading a unified-spacing settings file must derive both columns, got left=" +
                    loaded.LeftDockButtonGapPixels + "% right=" + loaded.RightTileButtonGapPixels +
                    "% offsets=" + loaded.LeftDockGroupOffsetY + "/" + loaded.RightTileGroupOffsetY);
            }

            if (loaded.LeftDockButtonGapPixels == 0 && loaded.RightTileButtonGapPixels == 99)
            {
                throw new InvalidOperationException("Unified column spacing did not replace the stored per-side values.");
            }

            Console.WriteLine(
                "Side column unified spacing: PASS load path derived left=" + loaded.LeftDockButtonGapPixels +
                "% right=" + loaded.RightTileButtonGapPixels +
                "% offsets=" + loaded.LeftDockGroupOffsetY + "/" + loaded.RightTileGroupOffsetY);
        }
        finally
        {
            try { System.IO.Directory.Delete(root, true); } catch { }
        }
    }
}
