using SharpCompress.Archives;
using SharpCompress.Readers;

namespace Dropstone.Services;

internal static class ArchiveExtractor
{
    private static readonly string[] SupportedExtensions = { ".zip", ".rar", ".dll" };

    /// <summary>
    /// Upper bound for the uncompressed size of an archive. Without it a small
    /// download can expand into gigabytes and fill the disk.
    /// </summary>
    private const long MaximumExpandedBytes = 1024L * 1024 * 1024;

    private const long MaximumEntryBytes = 512L * 1024 * 1024;

    public static void Extract(string archivePath, string destinationDirectory, CancellationToken cancellationToken)
    {
        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException("The selected payload file no longer exists.", archivePath);
        }

        var extension = Path.GetExtension(archivePath);
        if (!SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Only .zip and .rar archives and .dll files are supported.");
        }

        Directory.CreateDirectory(destinationDirectory);

        if (extension.Equals(".dll", StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(
                archivePath,
                Path.Combine(destinationDirectory, Path.GetFileName(archivePath)),
                overwrite: true);
            return;
        }

        var root = Path.GetFullPath(destinationDirectory);
        var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        using var archive = ArchiveFactory.Open(archivePath, new ReaderOptions());

        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(entry.Key) || !string.IsNullOrEmpty(entry.LinkTarget))
            {
                continue;
            }

            var rawPath = entry.Key
                .Replace('/', Path.DirectorySeparatorChar)
                .TrimStart(Path.DirectorySeparatorChar);

            if (rawPath.Length == 0)
            {
                continue;
            }

            // An archive may contain directory entries that are not flagged as
            // such. A trailing separator always means "directory" - writing a
            // file with a directory's name would make the next entry fail.
            var isDirectory = entry.IsDirectory || rawPath.EndsWith(Path.DirectorySeparatorChar);
            var relativePath = isDirectory
                ? rawPath.TrimEnd(Path.DirectorySeparatorChar)
                : rawPath;

            if (relativePath.Length == 0)
            {
                continue;
            }

            var destination = Path.GetFullPath(Path.Combine(root, relativePath));
            if (!destination.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) &&
                !destination.Equals(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Archive entry escapes the destination folder: {entry.Key}");
            }

            if (isDirectory)
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            if (entry.Size > MaximumEntryBytes)
            {
                throw new InvalidDataException(
                    $"Archive entry '{entry.Key}' is too large (limit: 512 MB).");
            }

            expanded += Math.Max(entry.Size, 0);
            if (expanded > MaximumExpandedBytes)
            {
                throw new InvalidDataException(
                    "The archive expands to more than 1 GB. That looks like a malformed or malicious archive.");
            }

            var parent = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(ToLongPath(parent));
            }

            using var input = entry.OpenEntryStream();
            using var output = new FileStream(
                ToLongPath(destination),
                FileMode.Create,
                FileAccess.Write,
                FileShare.None);
            input.CopyTo(output);
        }
    }

    /// <summary>
    /// Returns the extended-length form of an absolute path. Windows only
    /// accepts paths longer than MAX_PATH when they use the "\\?\" prefix,
    /// which works regardless of the long-path manifest.
    /// </summary>
    private static string ToLongPath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return path;
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return @"\\?\UNC\" + path[2..];
        }

        return path.Length > 240 ? @"\\?\" + path : path;
    }

    public static void FlattenSingleRoot(string directory)
    {
        var entries = Directory.GetFileSystemEntries(directory);
        if (entries.Length != 1 || !Directory.Exists(entries[0]))
        {
            return;
        }

        var singleRoot = entries[0];
        var wrapperName = Path.GetFileName(singleRoot);
        var children = Directory.GetFileSystemEntries(singleRoot);

        // A child with the same name as the wrapper cannot be moved up - the
        // name is already taken. In that case the wrapper stays.
        if (children.Any(child =>
                string.Equals(Path.GetFileName(child), wrapperName, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        foreach (var item in children)
        {
            var destination = Path.Combine(directory, Path.GetFileName(item));
            if (Directory.Exists(item))
            {
                Directory.Move(ToLongPath(item), ToLongPath(destination));
            }
            else
            {
                File.Move(ToLongPath(item), ToLongPath(destination));
            }
        }

        Directory.Delete(ToLongPath(singleRoot), recursive: true);
    }
}
