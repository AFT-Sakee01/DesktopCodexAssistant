using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

// Coordinates one headless owner's asynchronous lifetime. Stop/suspend invalidates the captured
// generation before cancellation is signalled, so a non-cancellable transport can finish without
// committing state, disk, log, notification, or UI work into a later owner lifetime.
internal sealed class OwnerOperationGeneration : IDisposable
{
    private readonly object syncRoot = new object();
    private long generation;
    private bool active;
    private bool disposed;
    private CancellationTokenSource cancellationSource;

    internal bool StartOrResume()
    {
        lock (this.syncRoot)
        {
            if (this.disposed)
            {
                throw new ObjectDisposedException("OwnerOperationGeneration");
            }

            if (this.active)
            {
                return false;
            }

            unchecked
            {
                this.generation++;
            }

            this.cancellationSource = new CancellationTokenSource();
            this.active = true;
            return true;
        }
    }

    internal OwnerOperationLease Capture()
    {
        lock (this.syncRoot)
        {
            if (!this.active || this.disposed || this.cancellationSource == null)
            {
                return null;
            }

            return new OwnerOperationLease(this.generation, this.cancellationSource.Token);
        }
    }

    internal bool IsCurrent(OwnerOperationLease lease)
    {
        lock (this.syncRoot)
        {
            return IsCurrentNoLock(lease);
        }
    }

    internal bool TryExecuteCurrent(OwnerOperationLease lease, Action action)
    {
        if (action == null)
        {
            throw new ArgumentNullException("action");
        }

        // Keeping the check and commit under the same lock is what makes Stop a hard boundary:
        // once Stop returns, no previously captured action can begin a business side effect.
        lock (this.syncRoot)
        {
            if (!IsCurrentNoLock(lease))
            {
                return false;
            }

            action();
            return true;
        }
    }

    internal void StopOrSuspend()
    {
        CancellationTokenSource source = null;
        lock (this.syncRoot)
        {
            if (!this.active)
            {
                return;
            }

            this.active = false;
            unchecked
            {
                this.generation++;
            }

            source = this.cancellationSource;
            this.cancellationSource = null;
        }

        CancelAndDispose(source);
    }

    public void Dispose()
    {
        CancellationTokenSource source = null;
        lock (this.syncRoot)
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;
            this.active = false;
            unchecked
            {
                this.generation++;
            }

            source = this.cancellationSource;
            this.cancellationSource = null;
        }

        CancelAndDispose(source);
    }

    private bool IsCurrentNoLock(OwnerOperationLease lease)
    {
        return lease != null &&
            this.active &&
            !this.disposed &&
            this.cancellationSource != null &&
            lease.Generation == this.generation &&
            !lease.CancellationToken.IsCancellationRequested;
    }

    private static void CancelAndDispose(CancellationTokenSource source)
    {
        if (source == null)
        {
            return;
        }

        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            source.Dispose();
        }
    }

    internal static void RunSelfTest()
    {
        string root = Path.Combine(Path.GetTempPath(), "DesktopCodexAssistant-owner-generation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string sentinelPath = Path.Combine(root, "sentinel.bin");
        try
        {
            byte[] initial = new byte[] { 1, 3, 5, 7 };
            File.WriteAllBytes(sentinelPath, initial);
            DateTime fixedMtime = DateTime.UtcNow.AddDays(-2.0);
            File.SetLastWriteTimeUtc(sentinelPath, fixedMtime);
            string initialHash = ComputeFileHash(sentinelPath);
            DateTime initialMtime = File.GetLastWriteTimeUtc(sentinelPath);

            OwnerOperationGeneration owner = new OwnerOperationGeneration();
            if (!owner.StartOrResume() || owner.StartOrResume())
            {
                throw new InvalidOperationException("Owner generation self-test failed: start was not idempotent.");
            }

            OwnerOperationLease staleLease = owner.Capture();
            ManualResetEventSlim release = new ManualResetEventSlim(false);
            int stateRevision = 0;
            int businessLogCount = 0;
            int uiCallbackCount = 0;
            Task lateCompletion = Task.Run((Action)delegate
            {
                release.Wait();
                owner.TryExecuteCurrent(staleLease, delegate
                {
                    stateRevision++;
                    File.WriteAllText(sentinelPath, "late");
                    businessLogCount++;
                    uiCallbackCount++;
                });
            });

            owner.StopOrSuspend();
            owner.StopOrSuspend();
            if (!staleLease.CancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException("Owner generation self-test failed: stop did not cancel the lease.");
            }

            release.Set();
            if (!lateCompletion.Wait(TimeSpan.FromSeconds(5.0)))
            {
                throw new TimeoutException("Owner generation self-test timed out.");
            }

            if (stateRevision != 0 || businessLogCount != 0 || uiCallbackCount != 0 ||
                !string.Equals(initialHash, ComputeFileHash(sentinelPath), StringComparison.Ordinal) ||
                File.GetLastWriteTimeUtc(sentinelPath) != initialMtime)
            {
                throw new InvalidOperationException("Owner generation self-test failed: a stale completion committed side effects.");
            }

            if (!owner.StartOrResume())
            {
                throw new InvalidOperationException("Owner generation self-test failed: resume did not establish a generation.");
            }

            OwnerOperationLease resumedLease = owner.Capture();
            if (resumedLease == null || resumedLease.Generation == staleLease.Generation ||
                !owner.TryExecuteCurrent(resumedLease, delegate { stateRevision++; }) || stateRevision != 1)
            {
                throw new InvalidOperationException("Owner generation self-test failed: resumed generation was not current.");
            }

            owner.Dispose();
            owner.Dispose();
            release.Dispose();
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch
            {
            }
        }
    }

    private static string ComputeFileHash(string path)
    {
        using (SHA256 sha = SHA256.Create())
        using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            return Convert.ToBase64String(sha.ComputeHash(stream));
        }
    }
}

internal sealed class OwnerOperationLease
{
    internal OwnerOperationLease(long generation, CancellationToken cancellationToken)
    {
        this.Generation = generation;
        this.CancellationToken = cancellationToken;
    }

    internal long Generation { get; private set; }
    internal CancellationToken CancellationToken { get; private set; }
}
