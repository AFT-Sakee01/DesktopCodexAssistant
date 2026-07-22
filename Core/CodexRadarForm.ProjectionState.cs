using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

internal sealed partial class CodexRadarForm
{
    private readonly RadarProjectionPublisher radarProjectionPublisher =
        new RadarProjectionPublisher(RadarPublishedProjectionState.CreateDefault());

    // Published state is never mutated after this hand-off. Producers build the replacement before
    // entering the publisher lock; the lock itself only assigns its publication revision and swaps
    // one reference. This keeps network, disk, logging, painting, and UI dispatch outside the lock.
    private void PublishProjectionStateFromOwner()
    {
        RadarPublishedProjectionState replacement = CaptureProjectionStateFromOwner();
        this.radarProjectionPublisher.Publish(replacement);
    }

    private RadarPublishedProjectionState ClonePublishedProjectionState()
    {
        return this.radarProjectionPublisher.ClonePublished();
    }

    private RadarPublishedProjectionState CaptureProjectionStateFromOwner()
    {
        DeepSeekServiceSnapshot deepSeek = DeepSeekServiceMonitor.GetSnapshot();
        StatuspageSnapshot claudeStatus = StatuspageMonitor.GetSnapshot(StatuspageMonitor.ClaudeServiceKey);
        StatuspageSnapshot openAiStatus = StatuspageMonitor.GetSnapshot(StatuspageMonitor.OpenAiServiceKey);
        bool claudeUsageRequestRunning = ClaudeCodeUsageScheduler.IsRequestRunning;

        RadarFamilyProjectionState codexFamily;
        RadarFamilyProjectionState claudeFamily;
        bool radarServiceProbeRunning;
        lock (this.codexRadarStatusLock)
        {
            codexFamily = RadarFamilyProjectionState.FromRuntime(this.codexRuntimeState);
            claudeFamily = RadarFamilyProjectionState.FromRuntime(this.claudeRuntimeState);
            radarServiceProbeRunning = this.codexRadarServiceProbeRunning;
        }

        bool networkAvailable;
        ServiceHealthState openAiHealth;
        ServiceHealthState claudeHealth;
        lock (this.serviceHealthLock)
        {
            networkAvailable = this.serviceNetworkAvailable;
            openAiHealth = this.openAiServiceHealth;
            claudeHealth = this.claudeServiceHealth;
        }

        bool claudeRequestRunning;
        lock (this.claudeStatusLock)
        {
            claudeRequestRunning = this.claudeStatusRequestRunning;
        }

        bool openAiRequestRunning;
        lock (this.openAiStatusLock)
        {
            openAiRequestRunning = this.openAiStatusRequestRunning;
        }

        List<CodexRadarModelInfo> catalog;
        long catalogRevision;
        lock (this.codexIqCatalogSnapshotLock)
        {
            catalog = CloneCodexRadarModelCatalog(this.codexIqCatalogSnapshot);
            catalogRevision = this.codexIqCatalogRevision;
        }

        WidgetSettings settings = this.CurrentSettings;
        CodexRadarRandomTestSnapshot random = this.codexRadarRandomTestSnapshot;
        bool useRandomServiceState = settings != null &&
            settings.CodexRadarRandomTestEnabled &&
            random != null;
        if (useRandomServiceState)
        {
            networkAvailable = random.NetworkAvailable;
            codexFamily.RadarHealth = random.RadarHealth;
            openAiHealth = random.OpenAiHealth;
            claudeHealth = random.ClaudeHealth;
            codexFamily.RadarRequestRunning = false;
            claudeFamily.RadarRequestRunning = false;
            radarServiceProbeRunning = false;
            claudeRequestRunning = false;
            openAiRequestRunning = false;
        }

        ServiceProjectionState services = new ServiceProjectionState
        {
            NetworkAvailable = networkAvailable,
            RadarHealth = codexFamily.RadarHealth,
            OpenAiHealth = openAiHealth,
            ClaudeHealth = claudeHealth,
            RadarRequestRunning = codexFamily.RadarRequestRunning || radarServiceProbeRunning,
            OpenAiRequestRunning = openAiRequestRunning,
            ClaudeRequestRunning = claudeRequestRunning,
            ClaudeQuotaKnown = claudeFamily.QuotaSourceKnown || this.claudeQuotaSourceKnown,
            ClaudeUsageRequestRunning = claudeUsageRequestRunning,
            ClaudeUsageHealth = this.claudeCodeUsageHealth,
            ClaudeUsageErrorCode = this.claudeCodeUsageErrorCode ?? string.Empty,
            ClaudeStatusSource = claudeStatus == null
                ? StatuspageSnapshot.CreateDefault(StatuspageMonitor.ClaudeServiceKey)
                : claudeStatus.Clone(),
            OpenAiStatusSource = openAiStatus == null
                ? StatuspageSnapshot.CreateDefault(StatuspageMonitor.OpenAiServiceKey)
                : openAiStatus.Clone(),
            DeepSeekSource = deepSeek == null
                ? DeepSeekServiceSnapshot.CreateUnknown()
                : deepSeek.Clone()
        };

        return new RadarPublishedProjectionState
        {
            CodexFamily = codexFamily,
            ClaudeFamily = claudeFamily,
            Catalog = catalog,
            CatalogRevision = catalogRevision,
            Services = services,
            RadarClockTimeDisplayMode = settings == null
                ? RadarClockTimeDisplayMode.Utc
                : settings.RadarClockTimeDisplayMode
        };
    }

    private sealed class RadarProjectionPublisher
    {
        private readonly object syncRoot = new object();
        private RadarPublishedProjectionState published;
        private long publicationRevision;

        public RadarProjectionPublisher(RadarPublishedProjectionState initial)
        {
            this.published = initial ?? RadarPublishedProjectionState.CreateDefault();
        }

        public void Publish(RadarPublishedProjectionState replacement)
        {
            if (replacement == null)
            {
                replacement = RadarPublishedProjectionState.CreateDefault();
            }

            lock (this.syncRoot)
            {
                unchecked
                {
                    this.publicationRevision++;
                }

                replacement.PublicationRevision = this.publicationRevision;
                this.published = replacement;
            }
        }

        public RadarPublishedProjectionState ClonePublished()
        {
            lock (this.syncRoot)
            {
                return this.published == null
                    ? RadarPublishedProjectionState.CreateDefault()
                    : this.published.Clone();
            }
        }
    }

    private sealed class RadarPublishedProjectionState
    {
        public long PublicationRevision { get; set; }
        public long GenerationSentinel { get; set; }
        public RadarFamilyProjectionState CodexFamily { get; set; }
        public RadarFamilyProjectionState ClaudeFamily { get; set; }
        public List<CodexRadarModelInfo> Catalog { get; set; }
        public long CatalogRevision { get; set; }
        public ServiceProjectionState Services { get; set; }
        public RadarClockTimeDisplayMode RadarClockTimeDisplayMode { get; set; }

        public static RadarPublishedProjectionState CreateDefault()
        {
            return new RadarPublishedProjectionState
            {
                CodexFamily = RadarFamilyProjectionState.CreateDefault(CodexRadarSoftwareMode.Codex),
                ClaudeFamily = RadarFamilyProjectionState.CreateDefault(CodexRadarSoftwareMode.Claude),
                Catalog = new List<CodexRadarModelInfo>(),
                Services = ServiceProjectionState.CreateDefault(),
                RadarClockTimeDisplayMode = RadarClockTimeDisplayMode.Utc
            };
        }

        public RadarFamilyProjectionState GetFamily(CodexRadarSoftwareMode family)
        {
            return RadarSoftwareModeController.NormalizeEffectiveSoftwareMode(family) == CodexRadarSoftwareMode.Claude
                ? this.ClaudeFamily
                : this.CodexFamily;
        }

        public RadarPublishedProjectionState Clone()
        {
            return new RadarPublishedProjectionState
            {
                PublicationRevision = this.PublicationRevision,
                GenerationSentinel = this.GenerationSentinel,
                CodexFamily = this.CodexFamily == null
                    ? RadarFamilyProjectionState.CreateDefault(CodexRadarSoftwareMode.Codex)
                    : this.CodexFamily.Clone(),
                ClaudeFamily = this.ClaudeFamily == null
                    ? RadarFamilyProjectionState.CreateDefault(CodexRadarSoftwareMode.Claude)
                    : this.ClaudeFamily.Clone(),
                Catalog = CloneCodexRadarModelCatalog(this.Catalog),
                CatalogRevision = this.CatalogRevision,
                Services = this.Services == null
                    ? ServiceProjectionState.CreateDefault()
                    : this.Services.Clone(),
                RadarClockTimeDisplayMode = this.RadarClockTimeDisplayMode
            };
        }
    }

    private sealed class RadarFamilyProjectionState
    {
        public long GenerationSentinel { get; set; }
        public CodexRadarSoftwareMode Family { get; set; }
        public string ModelKey { get; set; }
        public CodexRadarSnapshot RadarSnapshot { get; set; }
        public CodexQuotaSnapshot QuotaSnapshot { get; set; }
        public bool QuotaSourceKnown { get; set; }
        public List<WeeklyBurnSample> FiveHourBurnSamples { get; set; }
        public List<WeeklyBurnSample> WeeklyBurnSamples { get; set; }
        public List<WeeklyBurnSample> FiveHourWallBurnSamples { get; set; }
        public List<WeeklyBurnSample> WeeklyWallBurnSamples { get; set; }
        public ServiceHealthState RadarHealth { get; set; }
        public bool RadarRequestRunning { get; set; }
        public DateTime LastRadarStatusAttemptLocal { get; set; }
        public DateTime LastRadarStatusRefreshUtc { get; set; }
        public long RuntimeRevision { get; set; }

        public static RadarFamilyProjectionState CreateDefault(CodexRadarSoftwareMode family)
        {
            return new RadarFamilyProjectionState
            {
                Family = RadarSoftwareModeController.NormalizeEffectiveSoftwareMode(family),
                ModelKey = string.Empty,
                RadarSnapshot = CodexRadarSnapshot.CreateDefault(),
                QuotaSnapshot = CodexQuotaSnapshot.CreateDefault(),
                FiveHourBurnSamples = new List<WeeklyBurnSample>(),
                WeeklyBurnSamples = new List<WeeklyBurnSample>(),
                FiveHourWallBurnSamples = new List<WeeklyBurnSample>(),
                WeeklyWallBurnSamples = new List<WeeklyBurnSample>(),
                RadarHealth = ServiceHealthState.Unknown
            };
        }

        public static RadarFamilyProjectionState FromRuntime(RadarFamilyRuntimeState state)
        {
            if (state == null)
            {
                return CreateDefault(CodexRadarSoftwareMode.Codex);
            }

            QuotaRuntimeState quota = state.Quota;
            return new RadarFamilyProjectionState
            {
                Family = state.Family,
                ModelKey = state.ModelKey ?? string.Empty,
                RadarSnapshot = state.RadarSnapshot == null
                    ? CodexRadarSnapshot.CreateDefault()
                    : state.RadarSnapshot.Clone(),
                QuotaSnapshot = quota == null || quota.Snapshot == null
                    ? CodexQuotaSnapshot.CreateDefault()
                    : quota.Snapshot.Clone(),
                QuotaSourceKnown = quota != null && quota.SourceKnown,
                FiveHourBurnSamples = CloneWeeklyBurnSamples(quota == null ? null : quota.FiveHourBurnSamples),
                WeeklyBurnSamples = CloneWeeklyBurnSamples(quota == null ? null : quota.WeeklyBurnSamples),
                FiveHourWallBurnSamples = CloneWeeklyBurnSamples(quota == null ? null : quota.FiveHourWallBurnSamples),
                WeeklyWallBurnSamples = CloneWeeklyBurnSamples(quota == null ? null : quota.WeeklyWallBurnSamples),
                RadarHealth = state.RadarSiteHealth,
                RadarRequestRunning = state.RadarStatusRequestRunning,
                LastRadarStatusAttemptLocal = state.LastRadarStatusAttemptLocal,
                LastRadarStatusRefreshUtc = state.LastRadarStatusRefreshUtc,
                RuntimeRevision = state.Revision
            };
        }

        public RadarFamilyProjectionState Clone()
        {
            return new RadarFamilyProjectionState
            {
                GenerationSentinel = this.GenerationSentinel,
                Family = this.Family,
                ModelKey = this.ModelKey ?? string.Empty,
                RadarSnapshot = this.RadarSnapshot == null
                    ? CodexRadarSnapshot.CreateDefault()
                    : this.RadarSnapshot.Clone(),
                QuotaSnapshot = this.QuotaSnapshot == null
                    ? CodexQuotaSnapshot.CreateDefault()
                    : this.QuotaSnapshot.Clone(),
                QuotaSourceKnown = this.QuotaSourceKnown,
                FiveHourBurnSamples = CloneWeeklyBurnSamples(this.FiveHourBurnSamples),
                WeeklyBurnSamples = CloneWeeklyBurnSamples(this.WeeklyBurnSamples),
                FiveHourWallBurnSamples = CloneWeeklyBurnSamples(this.FiveHourWallBurnSamples),
                WeeklyWallBurnSamples = CloneWeeklyBurnSamples(this.WeeklyWallBurnSamples),
                RadarHealth = this.RadarHealth,
                RadarRequestRunning = this.RadarRequestRunning,
                LastRadarStatusAttemptLocal = this.LastRadarStatusAttemptLocal,
                LastRadarStatusRefreshUtc = this.LastRadarStatusRefreshUtc,
                RuntimeRevision = this.RuntimeRevision
            };
        }
    }

    private sealed class ServiceProjectionState
    {
        public long GenerationSentinel { get; set; }
        public bool NetworkAvailable { get; set; }
        public ServiceHealthState RadarHealth { get; set; }
        public ServiceHealthState OpenAiHealth { get; set; }
        public ServiceHealthState ClaudeHealth { get; set; }
        public bool RadarRequestRunning { get; set; }
        public bool OpenAiRequestRunning { get; set; }
        public bool ClaudeRequestRunning { get; set; }
        public bool ClaudeQuotaKnown { get; set; }
        public bool ClaudeUsageRequestRunning { get; set; }
        public ServiceHealthState ClaudeUsageHealth { get; set; }
        public string ClaudeUsageErrorCode { get; set; }
        public StatuspageSnapshot ClaudeStatusSource { get; set; }
        public StatuspageSnapshot OpenAiStatusSource { get; set; }
        public DeepSeekServiceSnapshot DeepSeekSource { get; set; }

        public static ServiceProjectionState CreateDefault()
        {
            return new ServiceProjectionState
            {
                NetworkAvailable = true,
                RadarHealth = ServiceHealthState.Unknown,
                OpenAiHealth = ServiceHealthState.Unknown,
                ClaudeHealth = ServiceHealthState.Unknown,
                ClaudeUsageHealth = ServiceHealthState.Unknown,
                ClaudeUsageErrorCode = string.Empty,
                ClaudeStatusSource = StatuspageSnapshot.CreateDefault(StatuspageMonitor.ClaudeServiceKey),
                OpenAiStatusSource = StatuspageSnapshot.CreateDefault(StatuspageMonitor.OpenAiServiceKey),
                DeepSeekSource = DeepSeekServiceSnapshot.CreateUnknown()
            };
        }

        public ServiceProjectionState Clone()
        {
            return new ServiceProjectionState
            {
                GenerationSentinel = this.GenerationSentinel,
                NetworkAvailable = this.NetworkAvailable,
                RadarHealth = this.RadarHealth,
                OpenAiHealth = this.OpenAiHealth,
                ClaudeHealth = this.ClaudeHealth,
                RadarRequestRunning = this.RadarRequestRunning,
                OpenAiRequestRunning = this.OpenAiRequestRunning,
                ClaudeRequestRunning = this.ClaudeRequestRunning,
                ClaudeQuotaKnown = this.ClaudeQuotaKnown,
                ClaudeUsageRequestRunning = this.ClaudeUsageRequestRunning,
                ClaudeUsageHealth = this.ClaudeUsageHealth,
                ClaudeUsageErrorCode = this.ClaudeUsageErrorCode ?? string.Empty,
                ClaudeStatusSource = this.ClaudeStatusSource == null
                    ? StatuspageSnapshot.CreateDefault(StatuspageMonitor.ClaudeServiceKey)
                    : this.ClaudeStatusSource.Clone(),
                OpenAiStatusSource = this.OpenAiStatusSource == null
                    ? StatuspageSnapshot.CreateDefault(StatuspageMonitor.OpenAiServiceKey)
                    : this.OpenAiStatusSource.Clone(),
                DeepSeekSource = this.DeepSeekSource == null
                    ? DeepSeekServiceSnapshot.CreateUnknown()
                    : this.DeepSeekSource.Clone()
            };
        }
    }

    private static List<WeeklyBurnSample> CloneWeeklyBurnSamples(IList<WeeklyBurnSample> source)
    {
        List<WeeklyBurnSample> result = new List<WeeklyBurnSample>();
        for (int i = 0; source != null && i < source.Count; i++)
        {
            WeeklyBurnSample sample = source[i];
            if (sample != null)
            {
                result.Add(new WeeklyBurnSample
                {
                    Utc = sample.Utc,
                    ActiveHours = sample.ActiveHours,
                    RemainingPercent = sample.RemainingPercent
                });
            }
        }

        return result;
    }

    internal static void RunProjectionStateAtomicitySelfTest()
    {
        const int swapsPerProducer = 10000;
        RadarProjectionPublisher publisher = new RadarProjectionPublisher(CreateProjectionSentinel(101));
        RadarPublishedProjectionState stateA = CreateProjectionSentinel(101);
        RadarPublishedProjectionState stateB = CreateProjectionSentinel(202);
        ManualResetEventSlim start = new ManualResetEventSlim(false);
        int producersRemaining = 2;
        int mixed = 0;
        int projected = 0;

        Task producerA = Task.Run(delegate
        {
            start.Wait();
            for (int i = 0; i < swapsPerProducer; i++)
            {
                publisher.Publish(stateA.Clone());
                if ((i & 127) == 0)
                {
                    Thread.Yield();
                }
            }
            Interlocked.Decrement(ref producersRemaining);
        });
        Task producerB = Task.Run(delegate
        {
            start.Wait();
            for (int i = 0; i < swapsPerProducer; i++)
            {
                publisher.Publish(stateB.Clone());
                if ((i & 127) == 0)
                {
                    Thread.Yield();
                }
            }
            Interlocked.Decrement(ref producersRemaining);
        });
        Task consumer = Task.Run(delegate
        {
            start.Wait();
            while (Volatile.Read(ref producersRemaining) > 0 || Volatile.Read(ref projected) < swapsPerProducer)
            {
                RadarPublishedProjectionState projection = publisher.ClonePublished();
                if (!IsCompleteProjectionSentinel(projection, 101) &&
                    !IsCompleteProjectionSentinel(projection, 202))
                {
                    Interlocked.Increment(ref mixed);
                }

                Interlocked.Increment(ref projected);
                if ((projected & 127) == 0)
                {
                    Thread.Yield();
                }
            }
        });

        start.Set();
        Task.WaitAll(producerA, producerB, consumer);
        start.Dispose();
        if (mixed != 0 || projected < swapsPerProducer)
        {
            throw new InvalidOperationException(
                "Radar projection atomicity self-test failed. mixed=" + mixed + ", projected=" + projected);
        }
    }

    private static RadarPublishedProjectionState CreateProjectionSentinel(long generation)
    {
        int marker = generation == 101 ? 11 : 22;
        DateTime sourceUtc = new DateTime(2026, 7, marker, 1, 2, 3, DateTimeKind.Utc);
        CodexRadarSnapshot radar = CodexRadarSnapshot.CreateDefault();
        radar.ModelIqKnown = true;
        radar.ModelIqPassRatePercent = marker;
        radar.ModelIqSourceUpdatedAtKnown = true;
        radar.ModelIqSourceUpdatedAtLocal = sourceUtc.ToLocalTime();
        radar.CodexIqModels.Add(new CodexIqBoardModelPoint
        {
            Key = "sentinel-" + generation,
            Label = "sentinel-" + generation
        });
        CodexQuotaSnapshot quota = CodexQuotaSnapshot.CreateDefault();
        quota.FiveHourPercent = marker;
        quota.WeeklyPercent = marker;
        quota.SourceUpdatedKnown = true;
        quota.SourceUpdatedUtc = sourceUtc;

        RadarFamilyProjectionState family = new RadarFamilyProjectionState
        {
            GenerationSentinel = generation,
            Family = CodexRadarSoftwareMode.Codex,
            ModelKey = "sentinel-" + generation,
            RadarSnapshot = radar,
            QuotaSnapshot = quota,
            QuotaSourceKnown = true,
            FiveHourBurnSamples = new List<WeeklyBurnSample>
            {
                new WeeklyBurnSample { Utc = sourceUtc, ActiveHours = marker, RemainingPercent = marker }
            },
            WeeklyBurnSamples = new List<WeeklyBurnSample>
            {
                new WeeklyBurnSample { Utc = sourceUtc, ActiveHours = marker, RemainingPercent = marker }
            },
            FiveHourWallBurnSamples = new List<WeeklyBurnSample>
            {
                new WeeklyBurnSample { Utc = sourceUtc, ActiveHours = marker, RemainingPercent = marker }
            },
            WeeklyWallBurnSamples = new List<WeeklyBurnSample>
            {
                new WeeklyBurnSample { Utc = sourceUtc, ActiveHours = marker, RemainingPercent = marker }
            },
            RadarHealth = generation == 101 ? ServiceHealthState.Normal : ServiceHealthState.Unavailable,
            RadarRequestRunning = generation == 202,
            LastRadarStatusAttemptLocal = sourceUtc.ToLocalTime(),
            LastRadarStatusRefreshUtc = sourceUtc,
            RuntimeRevision = generation
        };
        ServiceProjectionState services = ServiceProjectionState.CreateDefault();
        services.GenerationSentinel = generation;
        services.NetworkAvailable = generation == 101;
        services.RadarHealth = family.RadarHealth;
        services.OpenAiHealth = family.RadarHealth;
        services.ClaudeHealth = family.RadarHealth;
        services.RadarRequestRunning = family.RadarRequestRunning;
        services.OpenAiRequestRunning = generation == 202;
        services.ClaudeRequestRunning = generation == 202;
        services.ClaudeQuotaKnown = generation == 101;
        services.ClaudeUsageRequestRunning = generation == 202;
        services.ClaudeUsageHealth = family.RadarHealth;
        services.ClaudeUsageErrorCode = "sentinel-" + generation;
        services.ClaudeStatusSource.CheckedAtUtc = sourceUtc;
        services.OpenAiStatusSource.CheckedAtUtc = sourceUtc;
        services.DeepSeekSource.CheckedAtUtc = sourceUtc;
        services.DeepSeekSource.ErrorCode = "sentinel-" + generation;

        return new RadarPublishedProjectionState
        {
            GenerationSentinel = generation,
            CodexFamily = family,
            ClaudeFamily = family.Clone(),
            Catalog = new List<CodexRadarModelInfo>
            {
                new CodexRadarModelInfo { Key = "sentinel-" + generation, Label = "sentinel-" + generation }
            },
            CatalogRevision = generation,
            Services = services,
            RadarClockTimeDisplayMode = generation == 101
                ? RadarClockTimeDisplayMode.Utc
                : RadarClockTimeDisplayMode.CurrentLocal
        };
    }

    private static bool IsCompleteProjectionSentinel(RadarPublishedProjectionState state, long generation)
    {
        if (state == null || state.CodexFamily == null || state.ClaudeFamily == null ||
            state.Services == null || state.Catalog == null || state.Catalog.Count != 1)
        {
            return false;
        }

        int marker = generation == 101 ? 11 : 22;
        DateTime sourceUtc = new DateTime(2026, 7, marker, 1, 2, 3, DateTimeKind.Utc);
        string key = "sentinel-" + generation;
        ServiceHealthState health = generation == 101
            ? ServiceHealthState.Normal
            : ServiceHealthState.Unavailable;
        return state.GenerationSentinel == generation &&
            state.CatalogRevision == generation &&
            string.Equals(state.Catalog[0].Key, key, StringComparison.Ordinal) &&
            state.CodexFamily.GenerationSentinel == generation &&
            state.ClaudeFamily.GenerationSentinel == generation &&
            state.CodexFamily.RuntimeRevision == generation &&
            string.Equals(state.CodexFamily.ModelKey, key, StringComparison.Ordinal) &&
            state.CodexFamily.RadarSnapshot != null &&
            state.CodexFamily.RadarSnapshot.ModelIqPassRatePercent == marker &&
            state.CodexFamily.RadarSnapshot.ModelIqSourceUpdatedAtKnown &&
            state.CodexFamily.RadarSnapshot.ModelIqSourceUpdatedAtLocal == sourceUtc.ToLocalTime() &&
            state.CodexFamily.QuotaSnapshot != null &&
            state.CodexFamily.QuotaSnapshot.SourceUpdatedUtc == sourceUtc &&
            state.CodexFamily.QuotaSnapshot.FiveHourPercent == marker &&
            state.CodexFamily.FiveHourBurnSamples != null &&
            state.CodexFamily.FiveHourBurnSamples.Count == 1 &&
            state.CodexFamily.FiveHourBurnSamples[0].RemainingPercent == marker &&
            state.CodexFamily.WeeklyBurnSamples != null &&
            state.CodexFamily.WeeklyBurnSamples.Count == 1 &&
            state.CodexFamily.WeeklyBurnSamples[0].RemainingPercent == marker &&
            state.CodexFamily.FiveHourWallBurnSamples != null &&
            state.CodexFamily.FiveHourWallBurnSamples.Count == 1 &&
            state.CodexFamily.FiveHourWallBurnSamples[0].RemainingPercent == marker &&
            state.CodexFamily.WeeklyWallBurnSamples != null &&
            state.CodexFamily.WeeklyWallBurnSamples.Count == 1 &&
            state.CodexFamily.WeeklyWallBurnSamples[0].RemainingPercent == marker &&
            state.CodexFamily.RadarHealth == health &&
            state.CodexFamily.RadarRequestRunning == (generation == 202) &&
            state.Services.GenerationSentinel == generation &&
            state.Services.NetworkAvailable == (generation == 101) &&
            state.Services.RadarHealth == health &&
            state.Services.OpenAiHealth == health &&
            state.Services.ClaudeHealth == health &&
            string.Equals(state.Services.ClaudeUsageErrorCode, key, StringComparison.Ordinal) &&
            state.Services.ClaudeStatusSource != null &&
            state.Services.ClaudeStatusSource.CheckedAtUtc == sourceUtc &&
            state.Services.OpenAiStatusSource != null &&
            state.Services.OpenAiStatusSource.CheckedAtUtc == sourceUtc &&
            state.Services.DeepSeekSource != null &&
            state.Services.DeepSeekSource.CheckedAtUtc == sourceUtc &&
            string.Equals(state.Services.DeepSeekSource.ErrorCode, key, StringComparison.Ordinal) &&
            state.RadarClockTimeDisplayMode == (generation == 101
                ? RadarClockTimeDisplayMode.Utc
                : RadarClockTimeDisplayMode.CurrentLocal);
    }
}
