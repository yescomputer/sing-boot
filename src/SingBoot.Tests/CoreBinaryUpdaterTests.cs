using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SingBoot.Tests;

[TestClass]
public sealed class CoreBinaryUpdaterTests
{
    private const string ReleaseFolder = "sing-box-1.2.0-windows-amd64";
    private const string Config = "{/* keep this */\"inbounds\":[]}";
    private static readonly string[] OriginalLaunch = ["1.0.0"];
    private static readonly string[] UpdatedLaunches = ["1.0.0", "1.2.0"];
    private static readonly string[] RollbackLaunches = ["1.0.0", "1.2.0", "1.0.0"];

    [DataRow("1.2.0", "already up to date")]
    [DataRow("1.2.0+build.2", "already up to date")]
    [DataRow("1.10.0-alpha.1", "newer than")]
    [TestMethod]
    public async Task CurrentOrNewerCoreSkipsDownloadAndRestart(string installed, string expected)
    {
        using var directory = new TestDirectory();
        StubSingBox.Compile(Path.Combine(directory.Path, "sing-box.exe"), installed);
        using var app = CreateApp(directory);
        using var github = new ReleaseHandler([1]);

        StringAssert.Contains(await app.UpdateCoreAsync(CancellationToken.None, github), expected);
        Assert.AreEqual(0, github.Downloads);
        Assert.AreEqual(CoreState.Stopped, app.State);
        Assert.IsFalse(File.Exists(Path.Combine(directory.Path, "sing-box.exe.bak")));
    }

    [DataRow("1.2.0-rc.1")]
    [DataRow("damaged")]
    [DataRow("missing")]
    [TestMethod]
    public async Task UpdateWhileStoppedInstallsOnlyTheReleaseRuntimeFiles(string installed)
    {
        using var directory = new TestDirectory();
        var executable = Path.Combine(directory.Path, "sing-box.exe");
        if (installed == "damaged")
            directory.WriteFile("sing-box.exe", "damaged executable");
        else if (installed != "missing")
            StubSingBox.Compile(executable, installed);

        var original = File.Exists(executable) ? File.ReadAllBytes(executable) : null;
        var dll = directory.WriteFile("libcronet.dll", "old DLL");
        var config = directory.WriteFile("config.json", Config);
        var candidate = CompileCandidate(directory);
        using var github = new ReleaseHandler(BuildArchive(candidate));
        using var app = CreateApp(directory);

        StringAssert.Contains(await app.UpdateCoreAsync(CancellationToken.None, github), "1.2.0");
        CollectionAssert.AreEqual(candidate, File.ReadAllBytes(executable));
        if (original is not null)
            CollectionAssert.AreEqual(original, File.ReadAllBytes(executable + ".bak"));
        else
            Assert.IsFalse(File.Exists(executable + ".bak"));
        Assert.AreEqual("new DLL", File.ReadAllText(dll));
        Assert.AreEqual("old DLL", File.ReadAllText(dll + ".bak"));
        Assert.AreEqual(Config, File.ReadAllText(config));
        Assert.IsFalse(File.Exists(Path.Combine(directory.Path, "unrelated.exe")));
        Assert.IsFalse(File.Exists(Path.Combine(directory.Path, "versions.log")));
        Assert.AreEqual(CoreState.Stopped, app.State);
        AssertNoStagingDirectory(directory);
    }

    [DataRow(false)]
    [DataRow(true)]
    [TestMethod]
    public async Task RunningCoreRestartsWithItsConfigAndRecoversIfNewCoreFails(bool failOnRun)
    {
        using var directory = new TestDirectory();
        var executable = Path.Combine(directory.Path, "sing-box.exe");
        StubSingBox.Compile(executable);
        var original = File.ReadAllBytes(executable);
        var candidate = CompileCandidate(directory, failOnRun: failOnRun);
        var config = directory.WriteFile("config.json", Config);
        var dll = directory.WriteFile("libcronet.dll", "old DLL");
        using var app = CreateApp(directory);
        await StartCoreAsync(app);
        using var github = new ReleaseHandler(BuildArchive(candidate)) { OnDownload = () => Assert.IsTrue(app.IsRunning) };

        if (failOnRun)
        {
            var error = await Assert.ThrowsExactlyAsync<IOException>(() => app.UpdateCoreAsync(CancellationToken.None, github));
            StringAssert.Contains(error.Message, "incompatible config");
            StringAssert.Contains(error.Message, "restored and restarted");
            CollectionAssert.AreEqual(original, File.ReadAllBytes(executable));
            Assert.AreEqual("old DLL", File.ReadAllText(dll));
        }
        else
        {
            await app.UpdateCoreAsync(CancellationToken.None, github);
            CollectionAssert.AreEqual(candidate, File.ReadAllBytes(executable));
            CollectionAssert.AreEqual(original, File.ReadAllBytes(executable + ".bak"));
            Assert.AreEqual("new DLL", File.ReadAllText(dll));
        }

        Assert.IsTrue(app.IsRunning);
        var versions = File.ReadAllLines(Path.Combine(directory.Path, "versions.log"));
        CollectionAssert.AreEqual(failOnRun ? RollbackLaunches : UpdatedLaunches, versions);
        var launches = File.ReadAllLines(Path.Combine(directory.Path, "launches.log"));
        Assert.AreEqual(2, launches.Length);
        Assert.AreEqual(launches[0], launches[1]);
        Assert.AreEqual(Config, File.ReadAllText(config));
        AssertNoStagingDirectory(directory);
    }

    [DataRow("checksum")]
    [DataRow("version")]
    [DataRow("missing executable")]
    [DataRow("truncated download")]
    [DataRow("cancel")]
    [TestMethod]
    public async Task FailedOrCancelledDownloadLeavesRunningCoreInPlace(string failure)
    {
        using var directory = new TestDirectory();
        var executable = Path.Combine(directory.Path, "sing-box.exe");
        StubSingBox.Compile(executable);
        var original = File.ReadAllBytes(executable);
        directory.WriteFile("config.json", Config);
        using var app = CreateApp(directory);
        await StartCoreAsync(app);
        using var cancellation = new CancellationTokenSource();
        var candidate = failure == "version" ? original : new byte[] { 1, 2, 3 };
        using var github = new ReleaseHandler(BuildArchive(candidate, includeExecutable: failure != "missing executable"));
        switch (failure)
        {
            case "checksum": github.Digest = "sha256:" + new string('0', 64); break;
            case "truncated download": github.Size++; break;
            case "cancel": github.OnDownload = cancellation.Cancel; break;
        }
        if (failure == "cancel")
            await Assert.ThrowsAsync<OperationCanceledException>(() => app.UpdateCoreAsync(cancellation.Token, github));
        else
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => app.UpdateCoreAsync(cancellation.Token, github));

        Assert.IsTrue(app.IsRunning);
        CollectionAssert.AreEqual(OriginalLaunch, File.ReadAllLines(Path.Combine(directory.Path, "versions.log")));
        CollectionAssert.AreEqual(original, File.ReadAllBytes(executable));
        Assert.IsFalse(File.Exists(executable + ".bak"));
        Assert.IsFalse(File.Exists(Path.Combine(directory.Path, "libcronet.dll")));
        AssertNoStagingDirectory(directory);
    }

    [DataRow("draft")]
    [DataRow("prerelease")]
    [DataRow("asset")]
    [DataRow("digest")]
    [TestMethod]
    public async Task InvalidReleaseMetadataPreventsPackageDownload(string failure)
    {
        using var directory = new TestDirectory();
        using var app = CreateApp(directory);
        using var github = new ReleaseHandler([1]);
        switch (failure)
        {
            case "draft": github.Draft = true; break;
            case "prerelease": github.Prerelease = true; break;
            case "asset": github.Name = ReleaseFolder + "-legacy-windows-7.zip"; break;
            case "digest": github.Digest = ""; break;
        }
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => app.UpdateCoreAsync(CancellationToken.None, github));
        Assert.AreEqual(0, github.Downloads);
        AssertNoStagingDirectory(directory);
    }

    [TestMethod]
    public async Task FailureReplacingExecutableRollsBackAlreadyInstalledDll()
    {
        using var directory = new TestDirectory();
        var exe = Path.Combine(directory.Path, "sing-box.exe");
        StubSingBox.Compile(exe);
        var original = File.ReadAllBytes(exe);
        var dll = directory.WriteFile("libcronet.dll", "old DLL");
        using var app = CreateApp(directory);
        using var github = new ReleaseHandler(BuildArchive(CompileCandidate(directory)));
        using var locked = File.Open(exe, FileMode.Open, FileAccess.Read, FileShare.Read);
        await Assert.ThrowsExactlyAsync<IOException>(() => app.UpdateCoreAsync(CancellationToken.None, github));
        CollectionAssert.AreEqual(original, File.ReadAllBytes(exe));
        Assert.AreEqual("old DLL", File.ReadAllText(dll));
        AssertNoStagingDirectory(directory);
    }

    [TestMethod]
    public async Task CancellationDuringRestartRestoresOldExecutableAndRemovesNewDll()
    {
        using var directory = new TestDirectory();
        var exe = Path.Combine(directory.Path, "sing-box.exe");
        StubSingBox.Compile(exe);
        var original = File.ReadAllBytes(exe);
        directory.WriteFile("config.json", Config);
        using var app = CreateApp(directory);
        await StartCoreAsync(app);

        using var cancellation = new CancellationTokenSource();
        app.OnCoreEvent += evt =>
        {
            if (evt.Kind == CoreEventKind.StateChanged && evt.State == CoreState.Starting)
                cancellation.Cancel();
        };
        using var github = new ReleaseHandler(BuildArchive(CompileCandidate(directory)));
        await Assert.ThrowsAsync<OperationCanceledException>(() => app.UpdateCoreAsync(cancellation.Token, github));

        Assert.AreEqual(CoreState.Stopped, app.State);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(exe));
        Assert.IsFalse(File.Exists(Path.Combine(directory.Path, "libcronet.dll")));
        AssertNoStagingDirectory(directory);
    }

    [TestMethod]
    public async Task ExternallyManagedExecutableIsLeftInPlaceEvenWhenConfigIsMissing()
    {
        using var directory = new TestDirectory();
        var exe = Path.Combine(directory.Path, "sing-box.exe");
        StubSingBox.Compile(exe);
        var original = File.ReadAllBytes(exe);
        using var app = new SingBootApp(LaunchMode.Normal, directory.Path, remoteDownloader: null,
            conflictDetector: (profile, _) => profile.ExecutablePath == exe);
        using var github = new ReleaseHandler(BuildArchive(CompileCandidate(directory)));
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => app.UpdateCoreAsync(CancellationToken.None, github));
        StringAssert.Contains(error.Message, "Another sing-box process");
        CollectionAssert.AreEqual(original, File.ReadAllBytes(exe));
        Assert.IsFalse(File.Exists(exe + ".bak"));
        AssertNoStagingDirectory(directory);
    }

    private static byte[] CompileCandidate(TestDirectory directory, bool failOnRun = false)
    {
        var exe = Path.Combine(directory.Path, "candidate.exe");
        StubSingBox.Compile(exe, "1.2.0", failOnRun);
        return File.ReadAllBytes(exe);
    }

    private static SingBootApp CreateApp(TestDirectory directory) =>
        new(LaunchMode.Normal, directory.Path, remoteDownloader: null, conflictDetector: (_, _) => false, startupGracePeriodMs: 200);

    private static async Task StartCoreAsync(SingBootApp app)
    {
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnEvent(CoreEvent evt)
        {
            if (evt.State == CoreState.Running)
                ready.TrySetResult(true);
        }
        app.OnCoreEvent += OnEvent;
        try
        {
            Assert.AreEqual(StartPreparationResult.Ready, app.PrepareForStart(out var preparation), preparation);
            Assert.IsTrue(app.Start(out var message), message);
            Assert.AreSame(ready.Task, await Task.WhenAny(ready.Task, Task.Delay(TimeSpan.FromSeconds(8))));
        }
        finally
        {
            app.OnCoreEvent -= OnEvent;
        }
    }

    private static void AssertNoStagingDirectory(TestDirectory directory) =>
        Assert.IsEmpty(Directory.GetDirectories(directory.Path, ".sing-box-update-*"));

    private static byte[] BuildArchive(byte[] candidate, bool includeExecutable = true)
    {
        using var data = new MemoryStream();
        using (var zip = new ZipArchive(data, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, byte[] bytes)
            {
                using var entry = zip.CreateEntry(ReleaseFolder + "/" + name).Open();
                entry.Write(bytes, 0, bytes.Length);
            }
            if (includeExecutable)
                Add("sing-box.exe", candidate);
            Add("libcronet.dll", Encoding.UTF8.GetBytes("new DLL"));
            Add("config.json", Encoding.UTF8.GetBytes("{}"));
            Add("../unrelated.exe", candidate);
        }
        return data.ToArray();
    }

    private sealed class ReleaseHandler(byte[] archive) : HttpMessageHandler
    {
        public bool Draft { get; set; }
        public bool Prerelease { get; set; }
        public string Name { get; set; } = ReleaseFolder + ".zip";
        public string Digest { get; set; } = ComputeDigest(archive);
        public int Size { get; set; } = archive.Length;
        public int Downloads { get; private set; }
        public Action? OnDownload { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpContent content;
            if (request.RequestUri.AbsoluteUri == "https://api.github.com/repos/SagerNet/sing-box/releases/latest")
            {
                var release = new
                {
                    tag_name = "v1.2.0", draft = Draft, prerelease = Prerelease,
                    assets = new[] {
                        new { name = "sing-box-1.2.0-windows-arm64.zip", size = Size, digest = Digest },
                        new { name = Name, size = Size, digest = Digest }
                    }
                };
                content = new StringContent(new JavaScriptSerializer().Serialize(release));
            }
            else
            {
                Assert.AreEqual("https://github.com/SagerNet/sing-box/releases/download/v1.2.0/" + ReleaseFolder + ".zip",
                    request.RequestUri.AbsoluteUri);
                Downloads++;
                OnDownload?.Invoke();
                cancellationToken.ThrowIfCancellationRequested();
                content = new ByteArrayContent(archive);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }

        private static string ComputeDigest(byte[] archive)
        {
            using var sha = SHA256.Create();
            return "sha256:" + BitConverter.ToString(sha.ComputeHash(archive)).Replace("-", "");
        }
    }
}
