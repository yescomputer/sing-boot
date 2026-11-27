using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace SingBoot;

internal static class CoreBinaryUpdater
{
    private static readonly Uri LatestRelease = new("https://api.github.com/repos/SagerNet/sing-box/releases/latest");
    private const long MaxArchiveBytes = 256L * 1024 * 1024;
    private static readonly Regex VersionLine = new(
        @"^sing-box version (?<version>(?<number>[0-9]+\.[0-9]+\.[0-9]+)(?<pre>-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?)[ \t]*\r?$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    public static async Task<string> UpdateAsync(
        string baseDirectory,
        Func<CoreUpdatePackage, CancellationToken, Task> install,
        HttpMessageHandler? handler,
        CancellationToken cancellationToken)
    {
        using var client = UpdateHttpClient.Create(handler);
        client.MaxResponseContentBufferSize = 2 * 1024 * 1024;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        var token = deadline.Token;
        using var package = new CoreUpdatePackage(baseDirectory);
        Version version;

        try
        {
            ReleaseAsset asset;
            (version, asset) = await GetReleaseAsync(client, token).ConfigureAwait(false);
            var executable = Path.Combine(baseDirectory, CoreDiscovery.SingBoxExecutableName);
            if (File.Exists(executable))
            {
                try
                {
                    var local = await ReadVersionAsync(executable, token).ConfigureAwait(false);
                    if (local.Number > version)
                        return $"Installed sing-box {local.Text} is newer than the latest stable release ({version}).";
                    if (local.Number == version && !local.Prerelease)
                        return $"sing-box {local.Text} is already up to date.";
                }
                catch (Exception ex) when (ex is InvalidDataException or Win32Exception or TimeoutException)
                {
                    // A damaged core can still be replaced by a verified release.
                    AppLog.Write($"core update: unable to identify the installed version: {ex.Message}");
                }
            }

            AppLog.Write($"core update: downloading sing-box {version} for Windows x64");
            var url = new Uri($"https://github.com/SagerNet/sing-box/releases/download/v{version}/{asset.Name}");
            await DownloadPackageAsync(client, url, asset, package, token).ConfigureAwait(false);
            var candidate = await ReadVersionAsync(package.ExecutablePath, token).ConfigureAwait(false);
            if (candidate.Number != version || candidate.Prerelease)
                throw new InvalidDataException($"The downloaded executable reports {candidate.Text}, expected {version}.");

            token.ThrowIfCancellationRequested();
        }
        catch (Exception ex) when (deadline.IsCancellationRequested &&
            ex is OperationCanceledException or ObjectDisposedException or IOException or HttpRequestException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("The sing-box release download timed out.", ex);
        }

        await install(package, cancellationToken).ConfigureAwait(false);
        return $"Updated sing-box to {version}.";
    }

    private static async Task<(Version Version, ReleaseAsset Asset)> GetReleaseAsync(HttpClient client, CancellationToken token)
    {
        using var response = await client.GetAsync(LatestRelease, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var content = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        var release = (Release?)new DataContractJsonSerializer(typeof(Release)).ReadObject(content);
        var tag = release?.Tag;
        if (release is null || release.Draft || release.Prerelease || tag is null ||
            !tag.StartsWith("v", StringComparison.Ordinal) || !Version.TryParse(tag.Substring(1), out var version) ||
            version.Build < 0 || version.Revision >= 0 || tag != $"v{version}")
        {
            throw new InvalidDataException("GitHub did not return a stable sing-box release.");
        }

        var name = $"sing-box-{version}-windows-amd64.zip";
        var asset = release.Assets?.SingleOrDefault(item => item.Name == name);
        if (asset is null)
            throw new InvalidDataException($"The release has no {name} package.");
        if (asset.Size <= 0 || asset.Size > MaxArchiveBytes ||
            !Regex.IsMatch(asset.Digest ?? "", @"\Asha256:[0-9a-fA-F]{64}\z", RegexOptions.CultureInvariant))
        {
            throw new InvalidDataException($"GitHub did not provide a valid size and SHA-256 digest for {name}.");
        }
        return (version, asset);
    }

    private static async Task DownloadPackageAsync(
        HttpClient client, Uri url, ReleaseAsset asset, CoreUpdatePackage package, CancellationToken token)
    {
        Directory.CreateDirectory(package.DirectoryPath);
        using var file = new FileStream(Path.Combine(package.DirectoryPath, "release.zip"), FileMode.CreateNew,
            FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            // On .NET Framework, disposing the response also interrupts a blocked body read.
            using var registration = token.Register(response.Dispose);
            using var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            await CoreUpdatePackage.CopyExactlyAsync(source, file, asset.Size, token).ConfigureAwait(false);
        }

        file.Position = 0;
        using var sha = SHA256.Create();
        var digest = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "");
        if (!string.Equals("sha256:" + digest, asset.Digest, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The sing-box download failed SHA-256 verification.");

        file.Position = 0;
        using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
        await package.ExtractAsync(archive, Path.GetFileNameWithoutExtension(asset.Name), token).ConfigureAwait(false);
    }

    private static async Task<(string Text, Version Number, bool Prerelease)> ReadVersionAsync(string executable, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var process = Process.Start(new ProcessStartInfo(executable, "version")
        {
            WorkingDirectory = Path.GetDirectoryName(executable),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true
        }) ?? throw new IOException("Unable to start sing-box to check its version.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var output = process.StandardOutput.ReadToEndAsync();
            while (!process.HasExited || !output.IsCompleted)
                await Task.Delay(50, deadline.Token).ConfigureAwait(false);

            var match = VersionLine.Match(await output.ConfigureAwait(false));
            if (process.ExitCode != 0 || !match.Success || !Version.TryParse(match.Groups["number"].Value, out var number))
                throw new InvalidDataException("Unable to determine the version reported by sing-box.exe.");
            return (match.Groups["version"].Value, number, match.Groups["pre"].Success);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("sing-box.exe version timed out.");
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(); }
                catch (InvalidOperationException) when (process.HasExited) { }
                process.WaitForExit();
            }
        }
    }

    [DataContract]
    private sealed class Release
    {
        [DataMember(Name = "tag_name")] public string? Tag { get; set; }
        [DataMember(Name = "draft", IsRequired = true)] public bool Draft { get; set; }
        [DataMember(Name = "prerelease", IsRequired = true)] public bool Prerelease { get; set; }
        [DataMember(Name = "assets")] public ReleaseAsset[]? Assets { get; set; }
    }

    [DataContract]
    private sealed class ReleaseAsset
    {
        [DataMember(Name = "name")] public string Name { get; set; } = "";
        [DataMember(Name = "size")] public long Size { get; set; }
        [DataMember(Name = "digest")] public string? Digest { get; set; }
    }
}
