using System.Net.Http;
using Dropstone.Models;

namespace Dropstone.Services;

internal static class PayloadDownloader
{
    private const long MaximumBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>Budget for the response headers.</summary>
    private static readonly TimeSpan HeaderTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Budget for the whole download. HttpClient.Timeout only covers the
    /// headers when the body is streamed, so the body needs its own deadline -
    /// without it a slow server can keep the installer busy forever.
    /// </summary>
    private static readonly TimeSpan TotalTimeout = TimeSpan.FromMinutes(30);

    public static string Download(
        Uri uri,
        IProgress<ProgressUpdate>? progress,
        CancellationToken cancellationToken = default)
    {
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Only http and https payload URLs are supported.");
        }

        using var client = new HttpClient { Timeout = HeaderTimeout };
        using var response = client
            .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .GetAwaiter()
            .GetResult();

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"The payload download failed with HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");
        }

        var length = response.Content.Headers.ContentLength;
        if (length > MaximumBytes)
        {
            throw new InvalidOperationException("The payload is too large (limit: 2 GB).");
        }

        var extension = Path.GetExtension(uri.AbsolutePath);
        if (!extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".rar", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".dll", StringComparison.OrdinalIgnoreCase))
        {
            extension = ".zip";
        }

        var directory = Path.Combine(Path.GetTempPath(), "Dropstone");
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, $"{Guid.NewGuid():N}{extension}");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TotalTimeout);

        try
        {
            using var source = response.Content.ReadAsStreamAsync(timeout.Token).GetAwaiter().GetResult();
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var buffer = new byte[81920];
            long total = 0;
            long reported = 0;
            while (true)
            {
                int read = source.ReadAsync(buffer.AsMemory(0, buffer.Length), timeout.Token)
                    .GetAwaiter()
                    .GetResult();
                if (read <= 0)
                {
                    break;
                }

                total += read;
                if (total > MaximumBytes)
                {
                    throw new InvalidOperationException("The payload is too large (limit: 2 GB).");
                }

                output.Write(buffer, 0, read);
                if (progress is not null && (total - reported >= 1024 * 1024 || total == length))
                {
                    progress.Report(new ProgressUpdate
                    {
                        Message = length is > 0
                            ? $"Downloading payload... {total / (1024 * 1024)} MB of {length.Value / (1024 * 1024)} MB"
                            : $"Downloading payload... {total / (1024 * 1024)} MB",
                        Percent = length is > 0
                            ? total * 100.0 / length.Value
                            : ProgressUpdate.IndeterminatePercent
                    });
                    reported = total;
                }
            }

            return destination;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryDelete(destination);
            throw new TimeoutException(
                $"The payload download did not finish within {TotalTimeout.TotalMinutes:F0} minutes.");
        }
        catch
        {
            TryDelete(destination);
            throw;
        }
    }

    public static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Temporary payload cleanup is best effort.
        }
    }
}
