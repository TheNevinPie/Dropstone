using System.Net.Http;

namespace Dropstone.Services;

internal static class PayloadDownloader
{
    private const long MaximumBytes = 2L * 1024 * 1024 * 1024;

    public static string Download(Uri uri, IProgress<string>? progress)
    {
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Only http and https payload URLs are supported.");
        }

        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        using var response = client
            .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead)
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
            !extension.Equals(".rar", StringComparison.OrdinalIgnoreCase))
        {
            extension = ".zip";
        }

        var directory = Path.Combine(Path.GetTempPath(), "Dropstone");
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, $"{Guid.NewGuid():N}{extension}");

        try
        {
            using var source = response.Content.ReadAsStream();
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var buffer = new byte[81920];
            long total = 0;
            long reported = 0;
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                if (total > MaximumBytes)
                {
                    throw new InvalidOperationException("The payload is too large (limit: 2 GB).");
                }

                output.Write(buffer, 0, read);
                if (progress is not null && (total - reported >= 1024 * 1024 || total == length))
                {
                    progress.Report(length is > 0
                        ? $"Downloading payload... {total * 100 / length.Value}%"
                        : $"Downloading payload... {total / (1024 * 1024)} MB");
                    reported = total;
                }
            }

            return destination;
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
