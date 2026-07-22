using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal sealed class BoundedHttpTextResult
{
    public bool Success { get; set; }
    public int StatusCode { get; set; }
    public string ContentType { get; set; }
    public string Content { get; set; }
    public int Bytes { get; set; }
    public string ErrorCode { get; set; }
}

internal static class BoundedHttpTextReader
{
    internal const int AuthenticatedJsonMaxBytes = 512 * 1024;
    internal const int PublicJsonMaxBytes = 1024 * 1024;
    internal const int RssMaxBytes = 512 * 1024;
    internal const int HtmlMaxBytes = 2 * 1024 * 1024;
    internal const int SmallProbeMaxBytes = 256 * 1024;
    internal const int TinyProbeMaxBytes = 64 * 1024;

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    internal static JavaScriptSerializer CreateJsonSerializer(int maxBodyBytes)
    {
        if (maxBodyBytes <= 0)
        {
            throw new ArgumentOutOfRangeException("maxBodyBytes");
        }

        return new JavaScriptSerializer
        {
            MaxJsonLength = maxBodyBytes,
            RecursionLimit = 64
        };
    }

    internal static BoundedHttpTextResult Execute(
        HttpWebRequest request,
        int maxBodyBytes,
        int deadlineMs,
        CancellationToken cancellationToken)
    {
        if (request == null)
        {
            throw new ArgumentNullException("request");
        }

        ValidateLimits(maxBodyBytes, deadlineMs);
        request.AutomaticDecompression |= DecompressionMethods.GZip | DecompressionMethods.Deflate;
        request.Timeout = ClampTimeout(request.Timeout, deadlineMs);
        request.ReadWriteTimeout = ClampTimeout(request.ReadWriteTimeout, deadlineMs);

        int deadlineTriggered = 0;
        int cancellationTriggered = 0;
        using (CancellationTokenRegistration registration = cancellationToken.Register(delegate
        {
            Interlocked.Exchange(ref cancellationTriggered, 1);
            request.Abort();
        }))
        using (Timer deadlineTimer = new Timer(delegate
        {
            Interlocked.Exchange(ref deadlineTriggered, 1);
            request.Abort();
        }, null, deadlineMs, Timeout.Infinite))
        {
            HttpWebResponse response = null;
            try
            {
                try
                {
                    response = (HttpWebResponse)request.GetResponse();
                }
                catch (WebException ex)
                {
                    response = ex.Response as HttpWebResponse;
                    if (response == null)
                    {
                        return Failure(
                            Interlocked.CompareExchange(ref cancellationTriggered, 0, 0) != 0
                                ? "CANCELLED"
                                : (Interlocked.CompareExchange(ref deadlineTriggered, 0, 0) != 0
                                    ? "BODY_DEADLINE"
                                    : "NETWORK"));
                    }
                }

                BoundedHttpTextResult result = ReadResponseBody(
                    response,
                    maxBodyBytes,
                    deadlineMs,
                    cancellationToken);
                result.StatusCode = (int)response.StatusCode;
                if (result.ErrorCode.Length == 0 &&
                    (result.StatusCode < 200 || result.StatusCode >= 300))
                {
                    result.Success = false;
                    result.ErrorCode = "HTTP_STATUS";
                }

                return result;
            }
            catch (WebException)
            {
                return Failure(
                    Interlocked.CompareExchange(ref cancellationTriggered, 0, 0) != 0
                        ? "CANCELLED"
                        : (Interlocked.CompareExchange(ref deadlineTriggered, 0, 0) != 0
                            ? "BODY_DEADLINE"
                            : "NETWORK"));
            }
            catch (OperationCanceledException)
            {
                return Failure(cancellationToken.IsCancellationRequested ? "CANCELLED" : "BODY_DEADLINE");
            }
            catch (IOException)
            {
                return Failure(
                    Interlocked.CompareExchange(ref deadlineTriggered, 0, 0) != 0
                        ? "BODY_DEADLINE"
                        : "BODY_IO");
            }
            finally
            {
                if (response != null)
                {
                    response.Dispose();
                }
            }
        }
    }

    internal static async Task<BoundedHttpTextResult> ReadHttpContentAsync(
        HttpResponseMessage response,
        int maxBodyBytes,
        int deadlineMs,
        CancellationToken cancellationToken)
    {
        if (response == null)
        {
            throw new ArgumentNullException("response");
        }

        ValidateLimits(maxBodyBytes, deadlineMs);
        long contentLength = response.Content == null || !response.Content.Headers.ContentLength.HasValue
            ? -1L
            : response.Content.Headers.ContentLength.Value;
        string contentType = response.Content == null || response.Content.Headers.ContentType == null
            ? string.Empty
            : response.Content.Headers.ContentType.ToString();
        int statusCode = (int)response.StatusCode;
        if (contentLength > maxBodyBytes)
        {
            return new BoundedHttpTextResult
            {
                Success = false,
                StatusCode = statusCode,
                ContentType = contentType,
                Content = string.Empty,
                Bytes = 0,
                ErrorCode = "BODY_TOO_LARGE"
            };
        }

        if (response.Content == null)
        {
            return new BoundedHttpTextResult
            {
                Success = statusCode >= 200 && statusCode < 300,
                StatusCode = statusCode,
                ContentType = contentType,
                Content = string.Empty,
                Bytes = 0,
                ErrorCode = statusCode >= 200 && statusCode < 300 ? string.Empty : "HTTP_STATUS"
            };
        }

        using (CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            deadline.CancelAfter(deadlineMs);
            try
            {
                using (Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                {
                    BoundedHttpTextResult result = await ReadBodyAsync(
                        stream,
                        contentLength,
                        contentType,
                        maxBodyBytes,
                        deadlineMs,
                        deadline.Token).ConfigureAwait(false);
                    result.StatusCode = statusCode;
                    if (result.ErrorCode.Length == 0 && (statusCode < 200 || statusCode >= 300))
                    {
                        result.Success = false;
                        result.ErrorCode = "HTTP_STATUS";
                    }

                    return result;
                }
            }
            catch (OperationCanceledException)
            {
                return new BoundedHttpTextResult
                {
                    Success = false,
                    StatusCode = statusCode,
                    ContentType = contentType,
                    Content = string.Empty,
                    Bytes = 0,
                    ErrorCode = cancellationToken.IsCancellationRequested ? "CANCELLED" : "BODY_DEADLINE"
                };
            }
        }
    }

    internal static void RunSelfTest()
    {
        AssertBody(new byte[] { 0x41, 0x42, 0x43, 0x44 }, -1, 4, 1000, true, string.Empty, "ABCD");
        AssertBody(new byte[] { 0x41, 0x42, 0x43, 0x44 }, 1, 3, 1000, false, "BODY_TOO_LARGE", string.Empty);
        AssertBody(new byte[] { 0x41, 0x42, 0x43, 0x44 }, 4, 4, 1000, true, string.Empty, "ABCD");
        AssertBody(new byte[] { 0x41, 0x42, 0x43, 0x44, 0x45 }, -1, 4, 1000, false, "BODY_TOO_LARGE", string.Empty);
        AssertBody(new byte[] { 0xC3, 0x28 }, -1, 8, 1000, false, "BODY_ENCODING", string.Empty);

        using (SlowTestStream slow = new SlowTestStream(Encoding.UTF8.GetBytes("slow"), 35))
        {
            BoundedHttpTextResult deadline = ReadBody(
                slow,
                -1,
                "text/plain; charset=utf-8",
                16,
                10,
                CancellationToken.None);
            if (deadline.Success || !string.Equals(deadline.ErrorCode, "BODY_DEADLINE", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Bounded HTTP reader deadline self-test failed.");
            }
        }

        using (CancellationTokenSource cancelled = new CancellationTokenSource())
        using (MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes("cancel")))
        {
            cancelled.Cancel();
            BoundedHttpTextResult result = ReadBody(
                stream,
                -1,
                "text/plain",
                16,
                1000,
                cancelled.Token);
            if (result.Success || !string.Equals(result.ErrorCode, "CANCELLED", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Bounded HTTP reader cancellation self-test failed.");
            }
        }
    }

    private static BoundedHttpTextResult ReadResponseBody(
        HttpWebResponse response,
        int maxBodyBytes,
        int deadlineMs,
        CancellationToken cancellationToken)
    {
        string contentType = response.ContentType ?? string.Empty;
        if (response.ContentLength > maxBodyBytes)
        {
            return new BoundedHttpTextResult
            {
                Success = false,
                StatusCode = (int)response.StatusCode,
                ContentType = contentType,
                Content = string.Empty,
                Bytes = 0,
                ErrorCode = "BODY_TOO_LARGE"
            };
        }

        using (Stream stream = response.GetResponseStream())
        {
            if (stream == null)
            {
                return new BoundedHttpTextResult
                {
                    Success = true,
                    StatusCode = (int)response.StatusCode,
                    ContentType = contentType,
                    Content = string.Empty,
                    Bytes = 0,
                    ErrorCode = string.Empty
                };
            }

            return ReadBody(
                stream,
                response.ContentLength,
                contentType,
                maxBodyBytes,
                deadlineMs,
                cancellationToken);
        }
    }

    private static BoundedHttpTextResult ReadBody(
        Stream stream,
        long contentLength,
        string contentType,
        int maxBodyBytes,
        int deadlineMs,
        CancellationToken cancellationToken)
    {
        if (contentLength > maxBodyBytes)
        {
            return Failure("BODY_TOO_LARGE");
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        using (MemoryStream body = new MemoryStream(Math.Min(maxBodyBytes, 16 * 1024)))
        {
            byte[] buffer = new byte[8192];
            while (true)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return Failure("CANCELLED");
                }

                int remainingMs = deadlineMs - (int)Math.Min(int.MaxValue, stopwatch.ElapsedMilliseconds);
                if (remainingMs <= 0)
                {
                    return Failure("BODY_DEADLINE");
                }

                if (stream.CanTimeout)
                {
                    stream.ReadTimeout = Math.Max(1, remainingMs);
                }

                int read = stream.Read(buffer, 0, Math.Min(buffer.Length, maxBodyBytes - (int)body.Length + 1));
                if (stopwatch.ElapsedMilliseconds >= deadlineMs)
                {
                    return Failure("BODY_DEADLINE");
                }

                if (read <= 0)
                {
                    break;
                }

                if (body.Length + read > maxBodyBytes)
                {
                    return Failure("BODY_TOO_LARGE");
                }

                body.Write(buffer, 0, read);
            }

            return Decode(body.ToArray(), contentType);
        }
    }

    private static async Task<BoundedHttpTextResult> ReadBodyAsync(
        Stream stream,
        long contentLength,
        string contentType,
        int maxBodyBytes,
        int deadlineMs,
        CancellationToken cancellationToken)
    {
        if (contentLength > maxBodyBytes)
        {
            return Failure("BODY_TOO_LARGE");
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        using (MemoryStream body = new MemoryStream(Math.Min(maxBodyBytes, 16 * 1024)))
        {
            byte[] buffer = new byte[8192];
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (stopwatch.ElapsedMilliseconds >= deadlineMs)
                {
                    return Failure("BODY_DEADLINE");
                }

                int read = await stream.ReadAsync(
                    buffer,
                    0,
                    Math.Min(buffer.Length, maxBodyBytes - (int)body.Length + 1),
                    cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                if (body.Length + read > maxBodyBytes)
                {
                    return Failure("BODY_TOO_LARGE");
                }

                body.Write(buffer, 0, read);
            }

            return Decode(body.ToArray(), contentType);
        }
    }

    private static BoundedHttpTextResult Decode(byte[] bytes, string contentType)
    {
        try
        {
            string content = StrictUtf8.GetString(bytes ?? new byte[0]);
            return new BoundedHttpTextResult
            {
                Success = true,
                StatusCode = 0,
                ContentType = contentType ?? string.Empty,
                Content = content,
                Bytes = bytes == null ? 0 : bytes.Length,
                ErrorCode = string.Empty
            };
        }
        catch (DecoderFallbackException)
        {
            return Failure("BODY_ENCODING");
        }
    }

    private static void AssertBody(
        byte[] bytes,
        long contentLength,
        int maxBytes,
        int deadlineMs,
        bool expectedSuccess,
        string expectedError,
        string expectedContent)
    {
        using (MemoryStream stream = new MemoryStream(bytes))
        {
            BoundedHttpTextResult result = ReadBody(
                stream,
                contentLength,
                "text/plain; charset=utf-8",
                maxBytes,
                deadlineMs,
                CancellationToken.None);
            if (result.Success != expectedSuccess ||
                !string.Equals(result.ErrorCode, expectedError, StringComparison.Ordinal) ||
                (expectedSuccess && !string.Equals(result.Content, expectedContent, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("Bounded HTTP reader body-limit self-test failed.");
            }
        }
    }

    private static int ClampTimeout(int current, int deadlineMs)
    {
        return current <= 0 ? deadlineMs : Math.Min(current, deadlineMs);
    }

    private static void ValidateLimits(int maxBodyBytes, int deadlineMs)
    {
        if (maxBodyBytes <= 0)
        {
            throw new ArgumentOutOfRangeException("maxBodyBytes");
        }

        if (deadlineMs <= 0)
        {
            throw new ArgumentOutOfRangeException("deadlineMs");
        }
    }

    private static BoundedHttpTextResult Failure(string errorCode)
    {
        return new BoundedHttpTextResult
        {
            Success = false,
            StatusCode = 0,
            ContentType = string.Empty,
            Content = string.Empty,
            Bytes = 0,
            ErrorCode = errorCode ?? string.Empty
        };
    }

    private sealed class SlowTestStream : MemoryStream
    {
        private readonly int delayMs;

        public SlowTestStream(byte[] buffer, int delayMs)
            : base(buffer)
        {
            this.delayMs = delayMs;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Thread.Sleep(this.delayMs);
            return base.Read(buffer, offset, count);
        }
    }
}
