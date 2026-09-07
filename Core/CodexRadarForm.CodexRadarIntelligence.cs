using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

internal sealed partial class CodexRadarForm
{
    private const string CodexRadarIntelligenceMetricsUrl =
        "https://codexradar.com/api/intelligence-efficiency-metrics";
    private const string CodexRadarInsightsUrl =
        "https://codexradar.com/api/radar-insights";
    private const string CodexRadarArithmeticRecommendationMode =
        "comprehensive_arithmetic_mean";
    private const string CodexRadarWeightedRecommendationMode =
        "comprehensive_weighted_mean";

    private static bool TryReadCodexRadarIntelligenceStatus(
        string modelKey,
        out CodexRadarSnapshot snapshot,
        out ServiceHealthState health,
        out CodexRadarModelCatalogUpdate catalogUpdate,
        CancellationToken cancellationToken)
    {
        snapshot = null;
        catalogUpdate = null;
        string metricsContent;
        if (!TryReadCodexRadarUrlText(
                AddCacheBuster(CodexRadarIntelligenceMetricsUrl),
                "application/json,text/plain,*/*",
                out metricsContent,
                out health,
                cancellationToken))
        {
            return false;
        }

        string insightsContent;
        if (!TryReadCodexRadarUrlText(
                AddCacheBuster(CodexRadarInsightsUrl),
                "application/json,text/plain,*/*",
                out insightsContent,
                out health,
                cancellationToken))
        {
            return false;
        }

        if (!TryParseCodexRadarIntelligenceStatus(
                metricsContent,
                insightsContent,
                modelKey,
                true,
                out snapshot,
                out catalogUpdate))
        {
            health = ServiceHealthState.Unavailable;
            return false;
        }

        health = GetCodexRadarSnapshotHealth(snapshot);
        return true;
    }

    private static bool TryParseCodexRadarIntelligenceStatus(
        string metricsContent,
        string insightsContent,
        string modelKey,
        bool persistCatalog,
        out CodexRadarSnapshot snapshot,
        out CodexRadarModelCatalogUpdate catalogUpdate)
    {
        snapshot = null;
        catalogUpdate = null;
        if (string.IsNullOrWhiteSpace(metricsContent) || string.IsNullOrWhiteSpace(insightsContent))
        {
            return false;
        }

        try
        {
            JavaScriptSerializer serializer = BoundedHttpTextReader.CreateJsonSerializer(
                BoundedHttpTextReader.PublicJsonMaxBytes);
            Dictionary<string, object> metricsRoot =
                serializer.DeserializeObject(metricsContent) as Dictionary<string, object>;
            Dictionary<string, object> insightsRoot =
                serializer.DeserializeObject(insightsContent) as Dictionary<string, object>;
            if (!HasCodexRadarIntelligenceSchema(metricsRoot, 3) ||
                !HasCodexRadarIntelligenceSchema(insightsRoot, 1) ||
                !IsSupportedCodexRadarRecommendationMode(insightsRoot))
            {
                return false;
            }

            List<Dictionary<string, object>> metricPoints =
                GetQuotaObjectsFromArray(metricsRoot, "points");
            List<Dictionary<string, object>> comprehensivePoints =
                GetQuotaObjectsFromArray(insightsRoot, "comprehensive_points");
            if (metricPoints.Count == 0 || comprehensivePoints.Count == 0)
            {
                return false;
            }

            Dictionary<string, Dictionary<string, object>> metricsByKey =
                new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < metricPoints.Count; i++)
            {
                Dictionary<string, object> metric = metricPoints[i];
                string key = GetCodexRadarIntelligencePointKey(metric);
                if (key.Length == 0 || metricsByKey.ContainsKey(key))
                {
                    return false;
                }

                metricsByKey[key] = metric;
            }

            DateTime sourceUpdatedLocal;
            if (!TryGetCodexRadarIntelligenceSourceTime(insightsRoot, out sourceUpdatedLocal))
            {
                return false;
            }

            List<KeyValuePair<string, Dictionary<string, object>>> combined =
                new List<KeyValuePair<string, Dictionary<string, object>>>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < comprehensivePoints.Count; i++)
            {
                Dictionary<string, object> comprehensive = comprehensivePoints[i];
                string key = GetCodexRadarIntelligencePointKey(comprehensive);
                Dictionary<string, object> metric;
                if (key.Length == 0 || seen.Contains(key) ||
                    !metricsByKey.TryGetValue(key, out metric))
                {
                    return false;
                }

                Dictionary<string, object> node = BuildCodexRadarIntelligenceModelNode(
                    comprehensive,
                    metric,
                    sourceUpdatedLocal);
                if (node == null)
                {
                    return false;
                }

                seen.Add(key);
                combined.Add(new KeyValuePair<string, Dictionary<string, object>>(key, node));
            }

            if (combined.Count != comprehensivePoints.Count)
            {
                return false;
            }

            Dictionary<string, object> modelIq = BuildCodexRadarIntelligenceModelIqRoot(combined);
            Dictionary<string, object> selected = SelectCodexModelIqRoot(modelIq, modelKey);
            CodexRadarSnapshot candidate = CodexRadarSnapshot.CreateDefault();
            candidate.FetchedAtLocal = DateTime.Now;
            candidate.FetchedAtKnown = true;
            candidate.CheckedAtLocal = sourceUpdatedLocal;
            candidate.CheckedAtKnown = true;
            candidate.CodexIqModels = ExtractCodexIqBoardModels(modelIq, modelKey);
            if (!TryApplyCodexModelIqStatus(selected, candidate))
            {
                return false;
            }

            candidate.ModelIqSourceUpdatedAtLocal = sourceUpdatedLocal;
            candidate.ModelIqSourceUpdatedAtKnown = true;
            candidate.ModelIqRefreshedAtLocal = candidate.FetchedAtLocal;
            candidate.ModelIqRefreshedAtKnown = true;
            candidate.ModelIqRefreshSucceeded = true;
            ApplyCodexModelIqNormalRange(
                candidate,
                CodexModelIqWebsiteNormalLowScore,
                CodexModelIqWebsiteNormalHighScore);
            ApplyCodexModelIqDisplayMaxFromSource(modelIq, candidate);

            if (persistCatalog)
            {
                List<CodexRadarModelInfo> discovered = ExtractCodexRadarModelCatalog(modelIq);
                catalogUpdate = CodexRadarModelCatalog.MergeAndSave(
                    discovered,
                    IsCodexRadarCompleteCatalog(modelIq, discovered));
            }

            snapshot = candidate;
            return true;
        }
        catch
        {
            snapshot = null;
            catalogUpdate = null;
            return false;
        }
    }

    private static bool HasCodexRadarIntelligenceSchema(
        Dictionary<string, object> root,
        int expectedSchema)
    {
        double schema;
        return root != null &&
            TryGetQuotaNumber(root, "schema", out schema) &&
            Math.Abs(schema - expectedSchema) < 0.001;
    }

    private static bool IsSupportedCodexRadarRecommendationMode(
        Dictionary<string, object> root)
    {
        string mode = GetQuotaString(root, "recommendation_mode").Trim();
        // The site changed its published aggregate from arithmetic to weighted mean without
        // changing schema 1. Both modes expose the same authoritative comprehensive_points
        // contract; unknown algorithms still fail closed until their semantics are reviewed.
        return string.Equals(
                mode,
                CodexRadarArithmeticRecommendationMode,
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                mode,
                CodexRadarWeightedRecommendationMode,
                StringComparison.OrdinalIgnoreCase);
    }

    private static string GetCodexRadarIntelligencePointKey(Dictionary<string, object> point)
    {
        return point == null
            ? string.Empty
            : CodexRadarModelCatalog.BuildModelKey(
                GetQuotaString(point, "model"),
                GetQuotaString(point, "effort"),
                string.Empty);
    }

    private static bool TryGetCodexRadarIntelligenceSourceTime(
        Dictionary<string, object> root,
        out DateTime sourceUpdatedLocal)
    {
        sourceUpdatedLocal = DateTime.MinValue;
        string[] keys =
        {
            "software_source_updated_at",
            "visual_source_updated_at",
            "source_updated_at"
        };
        for (int i = 0; i < keys.Length; i++)
        {
            DateTime candidate;
            if (TryGetQuotaDate(root, keys[i], out candidate) &&
                (sourceUpdatedLocal == DateTime.MinValue || candidate > sourceUpdatedLocal))
            {
                sourceUpdatedLocal = candidate;
            }
        }

        if (sourceUpdatedLocal != DateTime.MinValue)
        {
            return true;
        }

        return TryGetQuotaDate(root, "generated_at", out sourceUpdatedLocal);
    }

    private static Dictionary<string, object> BuildCodexRadarIntelligenceModelNode(
        Dictionary<string, object> comprehensive,
        Dictionary<string, object> metric,
        DateTime sourceUpdatedLocal)
    {
        double iq;
        string model = GetQuotaString(comprehensive, "model").Trim();
        string effort = GetQuotaString(comprehensive, "effort").Trim();
        if (model.Length == 0 || effort.Length == 0 ||
            !TryGetQuotaNumber(comprehensive, "iq", out iq) ||
            iq < 0.0 || iq > MaxCodexModelIqScore)
        {
            return null;
        }

        Dictionary<string, object> node = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            { "date", sourceUpdatedLocal.ToString("o", CultureInfo.InvariantCulture) },
            { "model", model },
            { "reasoning_effort", effort },
            { "score", iq },
            { "status", InferCodexModelIqStatusFromScore(iq) }
        };

        double passed;
        double total = 0.0;
        if (TryGetQuotaNumber(metric, "passed", out passed))
        {
            node["passed"] = Math.Max(0.0, passed);
        }

        if (TryGetQuotaNumber(metric, "total", out total) && total > 0.0)
        {
            node["valid_tasks"] = total;
        }

        double averagePrice;
        if (TryGetQuotaNumber(metric, "average_price_usd", out averagePrice))
        {
            node["average_cost_usd"] = Math.Max(0.0, averagePrice);
        }

        double averageMinutes;
        if (TryGetQuotaNumber(metric, "average_minutes", out averageMinutes))
        {
            node["average_task_seconds"] = Math.Max(0.0, averageMinutes * 60.0);
            if (averageMinutes > 0.0 && total > 0.0)
            {
                node["serial_task_seconds"] = averageMinutes * 60.0 * total;
            }
        }

        double averageTokens;
        if (TryGetQuotaNumber(metric, "average_total_tokens", out averageTokens) &&
            averageTokens > 0.0 && total > 0.0)
        {
            // The new endpoint exposes per-run token average while the existing projection stores
            // the aggregate token input. Convert at the adapter boundary so downstream efficiency
            // and cache code keep their established unit and do not mix averages with totals.
            node["total_tokens"] = averageTokens * total;
        }

        return node;
    }

    private static Dictionary<string, object> BuildCodexRadarIntelligenceModelIqRoot(
        IList<KeyValuePair<string, Dictionary<string, object>>> points)
    {
        Dictionary<string, object> modelIq =
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, object> comparisons =
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; points != null && i < points.Count; i++)
        {
            KeyValuePair<string, Dictionary<string, object>> point = points[i];
            if (i == 0)
            {
                modelIq["latest"] = point.Value;
                continue;
            }

            comparisons[point.Key] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                { "label", CodexRadarModelCatalog.GetDisplayLabel(string.Empty, point.Key) },
                { "latest", point.Value }
            };
        }

        modelIq["comparisons"] = comparisons;
        return modelIq;
    }

    private static string FormatCodexRadarIntelligenceProbe(
        CodexRadarProbeResponse metrics,
        CodexRadarProbeResponse insights,
        string modelKey)
    {
        StringBuilder builder = new StringBuilder();
        builder.AppendLine("Comprehensive intelligence endpoints:");
        builder.Append("  metrics ");
        AppendProbeTransportLine(builder, metrics);
        builder.Append("  insights ");
        AppendProbeTransportLine(builder, insights);
        if (metrics.TransportSucceeded && metrics.StatusCode >= 200 && metrics.StatusCode < 300 &&
            insights.TransportSucceeded && insights.StatusCode >= 200 && insights.StatusCode < 300)
        {
            CodexRadarSnapshot parsed;
            CodexRadarModelCatalogUpdate ignored;
            bool success = TryParseCodexRadarIntelligenceStatus(
                metrics.Content,
                insights.Content,
                modelKey,
                false,
                out parsed,
                out ignored);
            builder.AppendLine("  parse=" + (success ? "ok" : "failed"));
            if (success && parsed != null)
            {
                builder.AppendLine("  models=" + parsed.CodexIqModels.Count.ToString(CultureInfo.InvariantCulture));
                builder.AppendLine("  selected_iq=" + parsed.ModelIqPassRatePercent.ToString("0.##", CultureInfo.InvariantCulture));
                builder.AppendLine("  source_updated_at=" + parsed.ModelIqSourceUpdatedAtLocal.ToString("o", CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static void RunCodexRadarIntelligenceAdapterSelfTest()
    {
        string metrics =
            "{\"schema\":3,\"points\":[" +
            "{\"model\":\"gpt-6-astra\",\"effort\":\"medium\",\"passed\":77,\"total\":110,\"iq\":105," +
            "\"average_price_usd\":2.25,\"average_minutes\":10,\"average_total_tokens\":1000000}," +
            "{\"model\":\"gpt-5.6-sol\",\"effort\":\"medium\",\"passed\":72,\"total\":112,\"iq\":96.43," +
            "\"average_price_usd\":1.5,\"average_minutes\":8,\"average_total_tokens\":800000}]}";
        string insights =
            "{\"schema\":1,\"recommendation_mode\":\"comprehensive_weighted_mean\"," +
            "\"software_source_updated_at\":\"2026-09-07T11:20:00+09:00\"," +
            "\"visual_source_updated_at\":\"2026-09-07T11:10:00+09:00\"," +
            "\"comprehensive_points\":[" +
            "{\"model\":\"gpt-6-astra\",\"effort\":\"medium\",\"iq\":121.25,\"samples\":8}," +
            "{\"model\":\"gpt-5.6-sol\",\"effort\":\"medium\",\"iq\":94.5,\"samples\":86}]}";
        CodexRadarSnapshot snapshot;
        CodexRadarModelCatalogUpdate update;
        bool parsed = TryParseCodexRadarIntelligenceStatus(
                metrics,
                insights,
                "gpt_6_astra_medium",
                false,
                out snapshot,
                out update);
        if (!parsed ||
            snapshot == null || !snapshot.ModelIqKnown ||
            snapshot.ModelIqPassRatePercent != 121 ||
            snapshot.ModelIqPassed != 77 || snapshot.ModelIqValidTasks != 110 ||
            Math.Abs(snapshot.ModelIqEfficiencyTotalTokens - 110000000.0) > 1.0 ||
            Math.Abs(snapshot.ModelIqEfficiencySerialSeconds - 66000.0) > 1.0 ||
            snapshot.CodexIqModels.Count != 2 ||
            !snapshot.CodexIqModels[0].Current ||
            Math.Abs(snapshot.CodexIqModels[0].Iq - 121.25) > 0.001 ||
            !snapshot.ModelIqSourceUpdatedAtKnown ||
            snapshot.ModelIqSourceUpdatedAtLocal.ToUniversalTime() !=
                new DateTime(2026, 9, 7, 2, 20, 0, DateTimeKind.Utc))
        {
            throw new InvalidOperationException(
                "Codex Radar comprehensive-intelligence adapter self-test failed." +
                " parsed=" + parsed.ToString(CultureInfo.InvariantCulture) +
                " snapshot=" + (snapshot == null ? "null" : "present") +
                " known=" + (snapshot != null && snapshot.ModelIqKnown).ToString(CultureInfo.InvariantCulture) +
                " iq=" + (snapshot != null ? snapshot.ModelIqPassRatePercent : -1.0).ToString(CultureInfo.InvariantCulture) +
                " passed=" + (snapshot != null ? snapshot.ModelIqPassed : -1).ToString(CultureInfo.InvariantCulture) +
                " tasks=" + (snapshot != null ? snapshot.ModelIqValidTasks : -1).ToString(CultureInfo.InvariantCulture) +
                " tokens=" + (snapshot != null ? snapshot.ModelIqEfficiencyTotalTokens : -1.0).ToString(CultureInfo.InvariantCulture) +
                " seconds=" + (snapshot != null ? snapshot.ModelIqEfficiencySerialSeconds : -1.0).ToString(CultureInfo.InvariantCulture) +
                " models=" + (snapshot != null && snapshot.CodexIqModels != null ? snapshot.CodexIqModels.Count : -1).ToString(CultureInfo.InvariantCulture) +
                " current0=" + (snapshot != null && snapshot.CodexIqModels != null && snapshot.CodexIqModels.Count > 0 && snapshot.CodexIqModels[0].Current).ToString(CultureInfo.InvariantCulture) +
                " source=" + (snapshot != null && snapshot.ModelIqSourceUpdatedAtKnown ? snapshot.ModelIqSourceUpdatedAtLocal.ToString("o", CultureInfo.InvariantCulture) : "unknown"));
        }

        CodexRadarSnapshot overlaid = CodexRadarSnapshot.CreateDefault();
        overlaid.ModelIqSourceUpdatedAtLocal = new DateTime(2026, 7, 13, 0, 0, 0, DateTimeKind.Local);
        overlaid.ModelIqSourceUpdatedAtKnown = true;
        CopyCodexModelIqSnapshot(overlaid, snapshot);
        if (!overlaid.ModelIqSourceUpdatedAtKnown ||
            overlaid.ModelIqSourceUpdatedAtLocal != snapshot.ModelIqSourceUpdatedAtLocal)
        {
            throw new InvalidOperationException(
                "Codex Radar intelligence overlay retained the compatibility source timestamp.");
        }

        if (TryParseCodexRadarIntelligenceStatus(
                metrics.Replace("\"schema\":3", "\"schema\":4"),
                insights,
                "gpt_6_astra_medium",
                false,
                out snapshot,
                out update))
        {
            throw new InvalidOperationException("Codex Radar intelligence adapter accepted an unsupported schema.");
        }

        if (!TryParseCodexRadarIntelligenceStatus(
                metrics,
                insights.Replace(
                    CodexRadarWeightedRecommendationMode,
                    CodexRadarArithmeticRecommendationMode),
                "gpt_6_astra_medium",
                false,
                out snapshot,
                out update))
        {
            throw new InvalidOperationException("Codex Radar intelligence adapter rejected the legacy arithmetic mode.");
        }

        if (TryParseCodexRadarIntelligenceStatus(
                metrics,
                insights.Replace(
                    CodexRadarWeightedRecommendationMode,
                    "comprehensive_unknown_mean"),
                "gpt_6_astra_medium",
                false,
                out snapshot,
                out update))
        {
            throw new InvalidOperationException("Codex Radar intelligence adapter accepted an unknown recommendation mode.");
        }
    }
}
