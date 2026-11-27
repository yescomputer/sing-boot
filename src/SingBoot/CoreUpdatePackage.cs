using System.IO.Compression;

namespace SingBoot;

internal sealed class CoreUpdatePackage : IDisposable
{
    // Install the executable last so its companion DLL is in place when it starts.
    private static readonly string[] RuntimeFiles = ["libcronet.dll", CoreDiscovery.SingBoxExecutableName];
    private readonly string _baseDirectory;
    private readonly List<(string Destination, bool HadOriginal)> _installed = [];

    public string DirectoryPath { get; }
    public string ExecutablePath => Path.Combine(DirectoryPath, CoreDiscovery.SingBoxExecutableName);

    public CoreUpdatePackage(string baseDirectory)
    {
        _baseDirectory = baseDirectory;
        DirectoryPath = Path.Combine(baseDirectory, $".sing-box-update-{Guid.NewGuid():N}");
    }

    public async Task ExtractAsync(ZipArchive archive, string folder, CancellationToken token)
    {
        foreach (var name in RuntimeFiles)
        {
            var entry = archive.Entries.SingleOrDefault(item => item.FullName == $"{folder}/{name}");
            if (entry is null)
            {
                if (name == CoreDiscovery.SingBoxExecutableName)
                    throw new InvalidDataException("The sing-box executable is missing from the release archive.");
                continue;
            }
            if (entry.Length <= 0 || entry.Length > 256L * 1024 * 1024)
                throw new InvalidDataException($"Invalid size for {name} in the release archive.");

            using var source = entry.Open();
            using var destination = File.Open(Path.Combine(DirectoryPath, name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await CopyExactlyAsync(source, destination, entry.Length, token).ConfigureAwait(false);
        }
    }

    internal static async Task CopyExactlyAsync(Stream source, Stream destination, long length, CancellationToken token)
    {
        var buffer = new byte[81920];
        long written = 0;
        int count;
        while ((count = await source.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) != 0)
        {
            written += count;
            if (written > length)
                throw new InvalidDataException("The sing-box package exceeds its declared size.");
            await destination.WriteAsync(buffer, 0, count, token).ConfigureAwait(false);
        }
        if (written != length)
            throw new InvalidDataException("The sing-box package is incomplete.");
    }

    public void Install()
    {
        foreach (var name in RuntimeFiles)
        {
            var staged = Path.Combine(DirectoryPath, name);
            if (name != CoreDiscovery.SingBoxExecutableName && !File.Exists(staged))
                continue;
            var destination = Path.Combine(_baseDirectory, name);
            var hadOriginal = File.Exists(destination);
            if (hadOriginal)
                File.Replace(staged, destination, destination + ".bak", ignoreMetadataErrors: true);
            else
                File.Move(staged, destination);
            _installed.Add((destination, hadOriginal));
        }
    }

    public void Rollback()
    {
        var errors = new List<string>();
        for (var i = _installed.Count - 1; i >= 0; i--)
        {
            var (destination, hadOriginal) = _installed[i];
            try
            {
                if (hadOriginal)
                    File.Replace(destination + ".bak", destination, null, ignoreMetadataErrors: true);
                else
                    File.Delete(destination);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{Path.GetFileName(destination)}: {ex.Message}");
            }
        }
        if (errors.Count > 0)
            throw new IOException(string.Join(Environment.NewLine, errors));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(DirectoryPath))
                Directory.Delete(DirectoryPath, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"core update: could not remove staging directory: {ex.Message}");
        }
    }
}
