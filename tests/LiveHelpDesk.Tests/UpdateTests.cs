using LiveHelpDesk.App.Services;
using LiveHelpDesk.App.ViewModels;
using Xunit;

namespace LiveHelpDesk.Tests;

public class UpdateTests
{
    private static readonly UpdatePackage Msi = new(new Uri("https://example.test/LiveHelpDesk-0.2.0-x64.msi"), "aa");
    private static readonly UpdatePackage Zip = new(new Uri("https://example.test/LiveHelpDesk-0.2.0-win-x64.zip"), "bb");

    [Theory]
    [InlineData("0.2.0", "0.2.0.0")]
    [InlineData("v1.2.3", "1.2.3.0")]
    [InlineData("1.2.3.4", "1.2.3.4")]
    [InlineData("1.2.3-beta+abc", "1.2.3.0")]
    public void Versions_AreParsedAndNormalized(string text, string expected) =>
        Assert.Equal(Version.Parse(expected), UpdateVersions.TryParse(text));

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    public void BadVersions_AreNull(string text) => Assert.Null(UpdateVersions.TryParse(text));

    [Fact]
    public void SameOrOlderVersion_IsNoUpdate()
    {
        var current = new Version(0, 2, 0, 0);
        Assert.Null(UpdateChecker.Evaluate(new UpdateManifest("0.2.0", Msi: Msi), current, InstallKind.Msi));
        Assert.Null(UpdateChecker.Evaluate(new UpdateManifest("0.1.9", Msi: Msi), current, InstallKind.Msi));
    }

    [Theory]
    [InlineData(InstallKind.Msi, "aa")]
    [InlineData(InstallKind.ZipPerUser, "bb")]
    [InlineData(InstallKind.ZipAllUsers, "bb")]
    [InlineData(InstallKind.None, null)]
    public void NewerVersion_PicksThePackageForThisInstall(InstallKind kind, string? expectedHash)
    {
        var update = UpdateChecker.Evaluate(new UpdateManifest("0.2.0", Msi: Msi, Zip: Zip), new Version(0, 1, 1, 0), kind);

        Assert.NotNull(update);
        Assert.Equal(new Version(0, 2, 0, 0), update.Version);
        Assert.Equal(expectedHash, update.Package?.Sha256);
    }

    [Fact]
    public void InvalidManifestVersion_Throws() =>
        Assert.Throws<InvalidDataException>(() => UpdateChecker.Evaluate(new UpdateManifest("nope"), new Version(0, 1, 0, 0), InstallKind.Msi));

    [Fact]
    public async Task Manifest_IsReadFromHttp()
    {
        const string json = """
            { "version": "0.3.0", "notesUrl": "https://example.test/notes",
              "msi": { "url": "https://example.test/a.msi", "sha256": "abc", "size": 10 },
              "zip": { "url": "https://example.test/a.zip", "sha256": "def", "size": 20 } }
            """;
        Uri? asked = null;
        using var http = new HttpClient(new StubHandler(request =>
        {
            asked = request.RequestUri;
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json) };
        }));

        var update = await new UpdateChecker(http, new Uri("https://pages.test/update.json"), new Version(0, 2, 0, 0), InstallKind.ZipPerUser)
            .CheckAsync();

        Assert.NotNull(update);
        Assert.Equal("def", update.Package?.Sha256);
        Assert.Equal(new Uri("https://example.test/notes"), update.Manifest.NotesUrl);
        Assert.StartsWith("https://pages.test/update.json?t=", asked!.ToString());
    }

    [Fact]
    public void InstallKind_FollowsWhereTheAppLives()
    {
        const string pf = @"C:\Program Files";
        Func<string, bool> zipInstalled = path => path.EndsWith("uninstall.ps1", StringComparison.OrdinalIgnoreCase);
        Func<string, bool> nothing = _ => false;

        Assert.Equal(InstallKind.Msi, InstallKindDetector.Detect(@"C:\Program Files\LiveHelpDesk\", true, pf, nothing));
        Assert.Equal(InstallKind.ZipAllUsers, InstallKindDetector.Detect(@"C:\Program Files\LiveHelpDesk\", false, pf, zipInstalled));
        Assert.Equal(InstallKind.ZipPerUser, InstallKindDetector.Detect(@"C:\Users\u\AppData\Local\Programs\LiveHelpDesk\", false, pf, zipInstalled));
        Assert.Equal(InstallKind.ZipPerUser, InstallKindDetector.Detect(@"C:\Users\u\AppData\Local\Programs\LiveHelpDesk\", true, pf, zipInstalled));
        Assert.Equal(InstallKind.None, InstallKindDetector.Detect(@"C:\src\LiveHelpDesk\bin\Debug\", true, pf, nothing));
        Assert.Equal(InstallKind.None, InstallKindDetector.Detect(@"C:\Program Files Extra\LiveHelpDesk\", true, pf, nothing));
    }

    [Fact]
    public async Task Download_ChecksTheHash()
    {
        var bytes = "pacchetto"u8.ToArray();
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
        var dir = Path.Combine(Path.GetTempPath(), "lhd-update-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var url = new Uri("https://example.test/dl/LiveHelpDesk-9.9.9-x64.msi");
            var reported = new List<double>();
            var path = await UpdateInstaller.DownloadAsync(http, new UpdatePackage(url, hash), dir, new SyncProgress(reported.Add));

            Assert.Equal(Path.Combine(dir, "LiveHelpDesk-9.9.9-x64.msi"), path);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
            Assert.Equal(1.0, reported[^1]);

            await Assert.ThrowsAsync<InvalidDataException>(() => UpdateInstaller.DownloadAsync(http, new UpdatePackage(url, "00"), dir, null));
            Assert.False(File.Exists(path));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void InstallerArguments_CarryEverythingTheScriptNeeds()
    {
        var args = UpdateInstaller.BuildArguments(
            InstallKind.ZipAllUsers, @"C:\t\apply-update.ps1", @"C:\t\p.zip", @"C:\Program Files\LiveHelpDesk\", @"C:\Program Files\LiveHelpDesk\LiveHelpDesk.exe", 42, @"C:\l\update.log");

        Assert.Equal(@"C:\t\apply-update.ps1", args[args.ToList().IndexOf("-File") + 1]);
        Assert.Equal("ZipAllUsers", args[args.ToList().IndexOf("-Kind") + 1]);
        Assert.Equal(@"C:\Program Files\LiveHelpDesk", args[args.ToList().IndexOf("-InstallDir") + 1]);
        Assert.Equal("42", args[args.ToList().IndexOf("-WaitPid") + 1]);
    }

    [Fact]
    public async Task ViewModel_OffersTheInstallOnlyWhenThereIsAPackage()
    {
        var manifest = new UpdateManifest("0.2.0", Msi: Msi);
        AvailableUpdate? installed = null;
        var vm = new UpdatesViewModel(
            new Version(0, 1, 1, 0), InstallKind.Msi,
            _ => Task.FromResult(UpdateChecker.Evaluate(manifest, new Version(0, 1, 1, 0), InstallKind.Msi)),
            (u, _, _) => { installed = u; return Task.CompletedTask; },
            _ => { });

        Assert.False(vm.CanInstall);
        await vm.CheckCommand.ExecuteAsync(null);

        Assert.True(vm.IsAvailable);
        Assert.Equal("0.2.0", vm.AvailableVersion);
        Assert.True(vm.CanInstall);

        await vm.InstallCommand.ExecuteAsync(null);
        Assert.Equal(new Version(0, 2, 0, 0), installed?.Version);
    }

    [Fact]
    public async Task ViewModel_OnlyAnnouncesForANonInstalledCopy()
    {
        var vm = new UpdatesViewModel(
            new Version(0, 1, 1, 0), InstallKind.None,
            _ => Task.FromResult(UpdateChecker.Evaluate(new UpdateManifest("0.2.0", Msi: Msi), new Version(0, 1, 1, 0), InstallKind.None)),
            (_, _, _) => Task.CompletedTask, _ => { });

        await vm.CheckCommand.ExecuteAsync(null);

        Assert.True(vm.IsAvailable);
        Assert.False(vm.CanInstall);
    }

    [Fact]
    public async Task ViewModel_ReportsAFailedCheck()
    {
        var vm = new UpdatesViewModel(
            new Version(0, 1, 1, 0), InstallKind.Msi, _ => throw new HttpRequestException("offline"), (_, _, _) => Task.CompletedTask, _ => { });

        await vm.CheckCommand.ExecuteAsync(null);

        Assert.False(vm.IsAvailable);
        Assert.Contains("offline", vm.Status);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
