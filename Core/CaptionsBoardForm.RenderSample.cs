using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

internal sealed partial class CaptionsBoardForm
{
    internal static void RenderSample(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        WidgetSettings settings = WidgetSettings.CreateDefaults();
        settings.SpecBoardWidth = 648;
        settings.SpecBoardHeight = 400;
        TranslatorControlSnapshot fixture = CreateFixtureSnapshot();
        using (CaptionsBoardForm form = new CaptionsBoardForm(null, settings, delegate { return null; }))
        {
            form.Size = form.GetDesiredSize();
            form.snapshot = fixture;
            // The article is half the board, so the sample has to carry one; without it every
            // render would only ever show the empty state.
            form.articleFixture = CreateArticleFixture(40);
            string path = Path.Combine(outputDir, "captions-board.png");
            RenderSampleSupport.SaveComposited(outputDir, Path.GetFileName(path), form.Width, form.Height, 255, form.DrawWindowContent);
            Console.WriteLine("Captions board -> " + path + " (" + form.Width + "x" + form.Height + ")");
        }
    }

    private static TranslatorControlSnapshot CreateFixtureSnapshot()
    {
        DateTime now = new DateTime(2026, 9, 11, 21, 40, 0, DateTimeKind.Local);
        TranslatorControlSnapshot snapshot = TranslatorControlSnapshot.CreateEmpty();
        snapshot.IsRunning = true;
        snapshot.GenieXRunning = true;
        snapshot.SanitizeProxyRunning = true;
        // Deliberately mixed so one render exercises both status-chip styles at once: three healthy
        // indicator chips plus one down chip carrying the accent border and its start hit target.
        snapshot.LiveCaptionsRunning = false;
        // zh-CN is the state the caption-source control exists to get the machine out of, so the
        // sample renders its warning styling rather than the already-correct en-US.
        snapshot.CaptionLanguageKnown = true;
        snapshot.CaptionLanguage = "zh-CN";
        snapshot.SettingsFileFound = true;
        snapshot.ContextAwareKnown = true;
        snapshot.ContextAware = true;
        snapshot.NumContextsKnown = true;
        snapshot.NumContexts = 64;
        snapshot.ModelNameKnown = true;
        snapshot.ModelName = "qualcomm/Qwen3-4B-Instruct-2507:W4A16";
        snapshot.ApiUrl = "http://127.0.0.1:18182/v1/chat/completions";
        snapshot.AvailableModels.Add("qualcomm/Qwen3-4B-Instruct-2507:W4A16");
        snapshot.AvailableModels.Add("qualcomm/Qwen3-8B:W4A16");
        snapshot.LastSuccessKnown = true;
        snapshot.LastSuccessLocal = now.AddMinutes(-3.0);
        snapshot.HistoryDatabaseFound = true;

        snapshot.RecentHistory.Add(new TranslatorHistoryEntry
        {
            TimestampKnown = true,
            TimestampLocal = now,
            SourceText = "Something went wrong connecting to the model.",
            TranslatedText = "[ERROR] Translation Failed: connection refused",
            TargetLanguage = "zh-CN",
            IsError = true
        });
        snapshot.RecentHistory.Add(new TranslatorHistoryEntry
        {
            TimestampKnown = true,
            TimestampLocal = now.AddMinutes(-3.0),
            SourceText = "This is a test of the live caption system running on device.",
            TranslatedText = "这是设备端实时字幕系统的测试。",
            TargetLanguage = "zh-CN",
            IsError = false
        });
        snapshot.RecentHistory.Add(new TranslatorHistoryEntry
        {
            TimestampKnown = true,
            TimestampLocal = now.AddMinutes(-6.0),
            SourceText = "Hello there, how are you today?",
            TranslatedText = "你好，你今天怎么样？",
            TargetLanguage = "zh-CN",
            IsError = false
        });
        snapshot.RecentHistory.Add(new TranslatorHistoryEntry
        {
            TimestampKnown = true,
            TimestampLocal = now.AddMinutes(-9.0),
            SourceText = "Let's talk about the quarterly roadmap for a moment.",
            TranslatedText = "我们花点时间聊聊这个季度的路线图。",
            TargetLanguage = "zh-CN",
            IsError = false
        });
        // Enough rows to overflow the visible area: the list now fills what it measures instead of
        // spreading a fixed three across it, so a fixture that stops short of the budget would show
        // the old sparse picture and hide the very thing this sample is meant to verify.
        snapshot.RecentHistory.Add(new TranslatorHistoryEntry
        {
            TimestampKnown = true,
            TimestampLocal = now.AddMinutes(-12.0),
            SourceText = "The frame rate drops as soon as ray tracing is switched on.",
            TranslatedText = "一打开光线追踪，帧率就掉下来了。",
            TargetLanguage = "zh-CN",
            IsError = false
        });
        snapshot.RecentHistory.Add(new TranslatorHistoryEntry
        {
            TimestampKnown = true,
            TimestampLocal = now.AddMinutes(-15.0),
            SourceText = "Battery life improved noticeably after the update.",
            TranslatedText = "更新之后续航明显变长了。",
            TargetLanguage = "zh-CN",
            IsError = false
        });
        snapshot.RecentHistory.Add(new TranslatorHistoryEntry
        {
            TimestampKnown = true,
            TimestampLocal = now.AddMinutes(-18.0),
            SourceText = "We will look at how attention scales with sequence length.",
            TranslatedText = "我们来看注意力如何随序列长度变化。",
            TargetLanguage = "zh-CN",
            IsError = false
        });

        return snapshot;
    }

    // Worst case for the layout self-test: with nothing running, all four status-strip start
    // targets plus the caption-source chip are registered in the same frame alongside the toolbar's
    // six, which is the densest the interactive surface ever gets.
    private static TranslatorControlSnapshot CreateAllServicesDownFixtureSnapshot()
    {
        TranslatorControlSnapshot snapshot = CreateFixtureSnapshot();
        snapshot.IsRunning = false;
        snapshot.GenieXRunning = false;
        snapshot.SanitizeProxyRunning = false;
        snapshot.LiveCaptionsRunning = false;
        return snapshot;
    }

    internal static void RunRenderSelfTest()
    {
        TranslatorControlSnapshot fixture = CreateFixtureSnapshot();
        TranslatorControlSnapshot clone = fixture.Clone();
        if (clone.RecentHistory.Count != 4 ||
            clone.AvailableModels.Count != 2 ||
            !clone.ContextAware ||
            clone.NumContexts != 64 ||
            !string.Equals(clone.ModelName, "qualcomm/Qwen3-4B-Instruct-2507:W4A16", StringComparison.Ordinal) ||
            !clone.RecentHistory[0].IsError)
        {
            throw new InvalidOperationException("Captions board snapshot clone self-test failed.");
        }

        WidgetSettings settings = WidgetSettings.CreateDefaults();
        using (CaptionsBoardForm form = new CaptionsBoardForm(null, settings, delegate { return null; }))
        {
            form.Size = form.GetDesiredSize();
            form.snapshot = fixture;
            using (Bitmap bitmap = new Bitmap(form.Width, form.Height, PixelFormat.Format32bppPArgb))
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                form.DrawWindowContent(g);
                Color corner = bitmap.GetPixel(Math.Min(bitmap.Width - 1, form.S(3)), Math.Min(bitmap.Height - 1, form.S(3)));
                Color center = bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2);
                if (corner.A == 0 || center.A == 0)
                {
                    throw new InvalidOperationException("Captions board renderer produced transparent output.");
                }
            }
        }

        Console.WriteLine("Captions board render: PASS snapshot clone, non-transparent composite");
    }
}
