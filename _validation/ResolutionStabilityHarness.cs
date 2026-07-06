using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

internal static class ResolutionStabilityHarness
{
    private static readonly BindingFlags AllStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private static readonly BindingFlags AllInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    private static Type settingsType;
    private static MethodInfo cloneMethod;
    private static MethodInfo adaptMethod;
    private static MethodInfo operationWidthMethod;
    private static MethodInfo operationHeightMethod;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: ResolutionStabilityHarness <app-exe> <output-dir>");
            return 2;
        }

        string appPath = Path.GetFullPath(args[0]);
        string outputDir = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(outputDir);

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Assembly appAssembly = Assembly.LoadFrom(appPath);
        settingsType = appAssembly.GetType("WidgetSettings", true);
        Type settingsFormType = appAssembly.GetType("Win11SettingsForm", true);
        cloneMethod = settingsType.GetMethod("Clone", AllInstance);
        adaptMethod = settingsType.GetMethod("AdaptToWorkArea", AllInstance);
        operationWidthMethod = settingsType.GetMethod("GetOperationWindowWidth", AllStatic);
        operationHeightMethod = settingsType.GetMethod("GetOperationWindowHeight", AllStatic);

        object baseline = settingsType.GetMethod("CreateDefaults", AllStatic).Invoke(null, null);
        int geometryFailures = RunGeometryChecks(baseline, outputDir);
        int settingsWarnings = RunSettingsSizingChecks(outputDir);
        int wheelFailures = RunSettingsFormChecks(settingsFormType, baseline, outputDir);

        Console.WriteLine("OutputDirectory=" + outputDir);
        Console.WriteLine("GeometryFailures=" + geometryFailures);
        Console.WriteLine("SettingsMinimumWarnings=" + settingsWarnings);
        Console.WriteLine("SettingsWheelFailures=" + wheelFailures);

        return geometryFailures == 0 && wheelFailures == 0 ? 0 : 1;
    }

    private static int RunGeometryChecks(object baseline, string outputDir)
    {
        Target[] targets = new Target[]
        {
            new Target("4K-ish", new Rectangle(0, 0, 3840, 2080)),
            new Target("UX3407N-default", new Rectangle(0, 60, 2880, 1740)),
            new Target("QHD-16x10", new Rectangle(0, 40, 2560, 1540)),
            new Target("FHD", new Rectangle(0, 0, 1920, 1040)),
            new Target("HD-plus", new Rectangle(0, 0, 1600, 860)),
            new Target("HD-1366", new Rectangle(0, 0, 1366, 728)),
            new Target("HD-1280", new Rectangle(0, 0, 1280, 680)),
            new Target("XGA", new Rectangle(0, 0, 1024, 728))
        };

        string[] windows = new string[]
        {
            "Main",
            "CodexRadar",
            "PowerThermal",
            "NetworkMonitor",
            "ConnectionCheck",
            "Operation"
        };

        StringBuilder csv = new StringBuilder();
        csv.AppendLine("target,work_area,window,rect,in_work_area");
        int failures = 0;

        for (int i = 0; i < targets.Length; i++)
        {
            object settings = cloneMethod.Invoke(baseline, null);
            adaptMethod.Invoke(settings, new object[] { targets[i].WorkArea });

            for (int j = 0; j < windows.Length; j++)
            {
                Rectangle rect = GetWindowRect(settings, windows[j], targets[i].WorkArea);
                bool inWorkArea = IsInside(rect, targets[i].WorkArea);
                if (!inWorkArea)
                {
                    failures++;
                }

                csv.AppendLine(
                    Csv(targets[i].Name) + "," +
                    Csv(FormatSize(targets[i].WorkArea)) + "," +
                    Csv(windows[j]) + "," +
                    Csv(FormatRect(rect)) + "," +
                    inWorkArea.ToString());
            }
        }

        File.WriteAllText(Path.Combine(outputDir, "window-geometry.csv"), csv.ToString(), new UTF8Encoding(false));
        return failures;
    }

    private static int RunSettingsSizingChecks(string outputDir)
    {
        Target[] targets = new Target[]
        {
            new Target("4K-ish", new Rectangle(0, 0, 3840, 2080)),
            new Target("UX3407N-default", new Rectangle(0, 60, 2880, 1740)),
            new Target("QHD-16x10", new Rectangle(0, 40, 2560, 1540)),
            new Target("FHD", new Rectangle(0, 0, 1920, 1040)),
            new Target("HD-plus", new Rectangle(0, 0, 1600, 860)),
            new Target("HD-1366", new Rectangle(0, 0, 1366, 728)),
            new Target("HD-1280", new Rectangle(0, 0, 1280, 680)),
            new Target("XGA", new Rectangle(0, 0, 1024, 728))
        };

        StringBuilder csv = new StringBuilder();
        csv.AppendLine("target,work_area,fit_client,fit_client_fits,minimum,minimum_fits");
        int warnings = 0;

        for (int i = 0; i < targets.Length; i++)
        {
            int width = targets[i].WorkArea.Width;
            int height = targets[i].WorkArea.Height;
            Size fit = new Size(
                Math.Min(1888, Math.Max(760, width - 80)),
                Math.Min(1312, Math.Max(560, height - 80)));
            Size minimum = new Size(
                Math.Min(1440, Math.Max(1216, width - 128)),
                Math.Min(992, Math.Max(896, height - 128)));
            bool fitFits = fit.Width <= width && fit.Height <= height;
            bool minFits = minimum.Width <= width && minimum.Height <= height;
            if (!minFits)
            {
                warnings++;
            }

            csv.AppendLine(
                Csv(targets[i].Name) + "," +
                Csv(FormatSize(targets[i].WorkArea)) + "," +
                Csv(fit.Width + "x" + fit.Height) + "," +
                fitFits.ToString() + "," +
                Csv(minimum.Width + "x" + minimum.Height) + "," +
                minFits.ToString());
        }

        File.WriteAllText(Path.Combine(outputDir, "settings-sizing.csv"), csv.ToString(), new UTF8Encoding(false));
        return warnings;
    }

    private static int RunSettingsFormChecks(Type settingsFormType, object baseline, string outputDir)
    {
        using (Form form = (Form)Activator.CreateInstance(
            settingsFormType,
            AllInstance,
            null,
            new object[] { null, baseline },
            null))
        {
            settingsFormType.GetProperty("OwnerFormClosing", AllInstance).SetValue(form, true, null);
            FieldInfo savedField = settingsFormType.GetField("saved", AllInstance);
            if (savedField != null)
            {
                savedField.SetValue(form, true);
            }

            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-32000, -32000);
            form.Show();
            Pump(10, 50);

            MethodInfo verifySelfTest = settingsFormType.GetMethod("VerifySelfTest", AllInstance);
            verifySelfTest.Invoke(form, null);

            MethodInfo selectPage = settingsFormType.GetMethod("SelectPage", AllInstance);
            FieldInfo pagesField = settingsFormType.GetField("pages", AllInstance);
            IList pages = (IList)pagesField.GetValue(form);
            StringBuilder csv = new StringBuilder();
            csv.AppendLine("page_index,title,wheel_handled,screenshot");
            int wheelFailures = 0;

            for (int i = 0; i < pages.Count; i++)
            {
                selectPage.Invoke(form, new object[] { i });
                Pump(4, 30);

                object page = pages[i];
                FieldInfo titleField = page.GetType().GetField("Title", AllInstance);
                FieldInfo scrollPanelField = page.GetType().GetField("ScrollPanel", AllInstance);
                object scrollPanel = scrollPanelField.GetValue(page);
                MethodInfo wheelMethod = scrollPanel.GetType().GetMethod("ScrollByMouseWheelDelta", AllInstance);
                bool wheelHandled = (bool)wheelMethod.Invoke(scrollPanel, new object[] { -120 });
                if (!wheelHandled)
                {
                    wheelFailures++;
                }

                string screenshot = string.Empty;
                if (i == 0 || i == pages.Count / 2 || i == pages.Count - 1)
                {
                    screenshot = Path.Combine(outputDir, "settings-page-" + i.ToString("00") + ".png");
                    SaveScreenshot(form, screenshot);
                }

                string title = titleField == null ? string.Empty : Convert.ToString(titleField.GetValue(page));
                csv.AppendLine(
                    i.ToString() + "," +
                    Csv(title) + "," +
                    wheelHandled.ToString() + "," +
                    Csv(screenshot));
            }

            File.WriteAllText(Path.Combine(outputDir, "settings-pages.csv"), csv.ToString(), new UTF8Encoding(false));
            form.Close();
            return wheelFailures;
        }
    }

    private static Rectangle GetWindowRect(object settings, string name, Rectangle workArea)
    {
        if (name == "Operation")
        {
            int button = GetInt(settings, "OperationButtonSize");
            int width = (int)operationWidthMethod.Invoke(null, new object[] { button, 1.0f });
            int height = (int)operationHeightMethod.Invoke(null, new object[] { button, 1.0f });
            int left = workArea.Left + Math.Max(0, GetInt(settings, "OperationLeftOffset"));
            int top = workArea.Bottom - height - Math.Max(0, GetInt(settings, "OperationBottomOffset"));
            left = Math.Max(workArea.Left, Math.Min(left, workArea.Right - width));
            top = Math.Max(workArea.Top, Math.Min(top, workArea.Bottom - height));
            return new Rectangle(left, top, width, height);
        }

        string prefix = name == "Main" ? string.Empty : name;
        int w = GetInt(settings, prefix + "Width");
        int h = GetInt(settings, prefix + "Height");
        int x = GetInt(settings, prefix + "LeftX");
        int bottom = GetInt(settings, prefix + "BottomY");
        return new Rectangle(x, bottom - h + 1, w, h);
    }

    private static int GetInt(object settings, string propertyName)
    {
        return (int)settingsType.GetProperty(propertyName, AllInstance).GetValue(settings, null);
    }

    private static bool IsInside(Rectangle rect, Rectangle workArea)
    {
        return rect.Width > 0 &&
            rect.Height > 0 &&
            rect.Left >= workArea.Left &&
            rect.Top >= workArea.Top &&
            rect.Right <= workArea.Right &&
            rect.Bottom <= workArea.Bottom;
    }

    private static void SaveScreenshot(Form form, string path)
    {
        using (Bitmap bitmap = new Bitmap(form.Width, form.Height))
        {
            form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
            bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
    }

    private static void Pump(int iterations, int sleepMs)
    {
        for (int i = 0; i < iterations; i++)
        {
            Application.DoEvents();
            System.Threading.Thread.Sleep(sleepMs);
        }
    }

    private static string FormatRect(Rectangle rect)
    {
        return rect.Left + "," + rect.Top + " " + rect.Width + "x" + rect.Height;
    }

    private static string FormatSize(Rectangle rect)
    {
        return rect.Width + "x" + rect.Height;
    }

    private static string Csv(string value)
    {
        if (value == null)
        {
            value = string.Empty;
        }

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private struct Target
    {
        public readonly string Name;
        public readonly Rectangle WorkArea;

        public Target(string name, Rectangle workArea)
        {
            this.Name = name;
            this.WorkArea = workArea;
        }
    }
}
