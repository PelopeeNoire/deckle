using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace WhispUI.Setup;

// ── Downloader ───────────────────────────────────────────────────────────────
//
// Streams an HTTP download to disk while reporting progress and computing
// SHA-256 incrementally. Used by the first-run wizard for HuggingFace
// model downloads — same code path for a 700 KB Silero file and a 3 GB
// Whisper model.
//
// Three guarantees:
//
//   1. Atomic write — bytes go to <destPath>.partial during the transfer
//      and the file is renamed to <destPath> only after SHA-256 verifies
//      (or unconditionally when ExpectedSha256 is null). An interrupted
//      or aborted download leaves a .partial the next attempt overwrites.
//
//   2. Streaming hash — IncrementalHash digests every buffer as it's
//      written, so verification is O(1) memory and doesn't require a
//      second full read of the file.
//
//   3. Cancellation — every async call honours the CancellationToken so
//      the wizard's Cancel button stops the transfer in milliseconds,
//      not at the next megabyte boundary.
//
// HttpClient is reused as a static field (Microsoft Learn guidance:
// instantiating per-call exhausts sockets under load). The default 100 s
// timeout is overridden to Infinite because large model files routinely
// exceed it on slow links — cancellation is the right escape hatch, not
// the timeout.
internal static class Downloader
{
    private const int BufferSize = 81920; // 80 KB, matches Stream.CopyToAsync default

    // Throttle progress reporting so a 3 GB download doesn't dispatch 39 000
    // callbacks at the UI thread. IProgress<T>.Report posts on the captured
    // SyncContext (the UI dispatcher when the page wired up the Progress
    // instance), and posting every 80 KB saturates the message pump enough
    // to freeze the window. 200 ms gives ~5 reports per second — smooth
    // perceived progress, no flood. The final position is always reported
    // outside the loop so the bar lands at 100% regardless of throttling.
    private const int ProgressReportThrottleMs = 200;

    private static readonly HttpClient _http = CreateHttpClient();

    public sealed record DownloadProgress(long BytesDownloaded, long? TotalBytes)
    {
        // Null when the server didn't send Content-Length — the UI shows
        // an indeterminate bar in that case (rare for HuggingFace, common
        // for proxied or gzipped responses).
        public double? Percent => TotalBytes is > 0
            ? (double)BytesDownloaded / TotalBytes.Value
            : null;
    }

    public sealed record DownloadResult(
        bool Success,
        string? ActualSha256,
        string? ErrorMessage)
    {
        public static DownloadResult Ok(string actualSha256) => new(true, actualSha256, null);
        public static DownloadResult Fail(string message)    => new(false, null, message);
    }

    public static async Task<DownloadResult> DownloadAsync(
        string url,
        string destPath,
        string? expectedSha256,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
            return DownloadResult.Fail("download URL is empty");

        string partialPath = destPath + ".partial";

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            long? total = response.Content.Headers.ContentLength;

            using var network = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long downloaded = 0;
            using (var file = new FileStream(partialPath,
                FileMode.Create, FileAccess.Write, FileShare.None,
                BufferSize, useAsync: true))
            {
                byte[] buffer = new byte[BufferSize];
                long lastReportTicks = Environment.TickCount64;
                int read;
                while ((read = await network.ReadAsync(buffer.AsMemory(0, BufferSize), ct).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, read);
                    downloaded += read;

                    long now = Environment.TickCount64;
                    if (now - lastReportTicks >= ProgressReportThrottleMs)
                    {
                        progress?.Report(new DownloadProgress(downloaded, total));
                        lastReportTicks = now;
                    }
                }
            }

            // Final tick — bar lands at 100% even if the last loop iteration
            // didn't cross the throttle threshold.
            progress?.Report(new DownloadProgress(downloaded, total));

            string actualSha = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();

            if (!string.IsNullOrWhiteSpace(expectedSha256) &&
                !string.Equals(actualSha, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(partialPath);
                return DownloadResult.Fail(
                    $"checksum mismatch: expected {expectedSha256}, got {actualSha}");
            }

            // Atomic publish: rename .partial to the final name. File.Move
            // with overwrite handles a stale destination from an interrupted
            // previous run.
            File.Move(partialPath, destPath, overwrite: true);

            return DownloadResult.Ok(actualSha);
        }
        catch (OperationCanceledException)
        {
            TryDelete(partialPath);
            throw;
        }
        catch (Exception ex)
        {
            TryDelete(partialPath);
            return DownloadResult.Fail($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true, // HuggingFace mirrors redirect to a CDN
        });
        client.Timeout = Timeout.InfiniteTimeSpan;
        // User-Agent helps shared infrastructure logs distinguish our traffic
        // from generic curl/wget. Bump the brand string when the app rename
        // lands.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WhispUI/1.0");
        return client;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort cleanup */ }
    }
}
