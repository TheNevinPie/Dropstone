using SharpCompress.Archives;
using SharpCompress.Readers;

namespace Dropstone.Services;

internal static class ArchiveExtractor
{
    private static readonly string[] SupportedExtensions = { ".zip", ".rar", ".dll" };

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

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(entry.Key) || !string.IsNullOrEmpty(entry.LinkTarget))
            {
                continue;
            }

            var relativePath = entry.Key
                .Replace('/', Path.DirectorySeparatorChar)
                .TrimStart(Path.DirectorySeparatorChar);

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

            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            var parent = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            using var input = entry.OpenEntryStream();
            using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
        }
    }

    public static void FlattenSingleRoot(string directory)
    {
        var entries = Directory.GetFileSystemEntries(directory);
        if (entries.Length != 1 || !Directory.Exists(entries[0]))
        {
            return;
        }

        var singleRoot = entries[0];
        foreach (var item in Directory.GetFileSystemEntries(singleRoot))
        {
            var destination = Path.Combine(directory, Path.GetFileName(item));
            if (Directory.Exists(item))
            {
                Directory.Move(item, destination);
            }
            else
            {
                File.Move(item, destination);
            }
        }

        Directory.Delete(singleRoot, recursive: true);
    }
}
