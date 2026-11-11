using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace LiveHelpDesk.App.Services;

/// <summary>One downloadable package of a release, as listed in <c>update.json</c>.</summary>
public sealed record UpdatePackage(Uri Url, string Sha256, long Size = 0);

/// <summary>
/// <c>update.json</c>, published on GitHub Pages by the Release workflow: the latest version and where its packages are.
/// </summary>
public sealed record UpdateManifest(
    string Version, DateTimeOffset? PublishedAt = null, Uri? NotesUrl = null, UpdatePackage? Msi = null, UpdatePackage? Zip = null);

/// <summary>How this copy was installed, which decides how it can replace itself.</summary>
public enum InstallKind
{
    /// <summary>Not an installed copy (a build folder): updates are only announced.</summary>
    None,

    /// <summary>The MSI, per machine in Program Files.</summary>
    Msi,

    /// <summary>The ZIP's install.ps1, for this user only (no administrator).</summary>
    ZipPerUser,

    /// <summary>The ZIP's install.ps1 with -AllUsers (administrator).</summary>
    ZipAllUsers,
}

/// <summary>A newer version than the running one; <see cref="Package"/> is null when this copy cannot install it by itself.</summary>
public sealed record AvailableUpdate(UpdateManifest Manifest, Version Version, UpdatePackage? Package);

public static class UpdateVersions
{
    /// <summary>The running app's version (from VersionPrefix or the -p:Version of the release build).</summary>
    public static Version Current() =>
        Normalize(Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0));

    /// <summary>"1.2.3", "v1.2.3" or "1.2.3.4"; anything after '-' or '+' is ignored.</summary>
    public static Version? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var core = text.Trim().TrimStart('v', 'V').Split('-', '+')[0];
        return System.Version.TryParse(core, out var version) ? Normalize(version) : null;
    }

    // 1.2.3 and 1.2.3.0 must compare equal: missing parts count as zero.
    private static Version Normalize(Version v) =>
        new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
}

/// <summary>Reads <c>update.json</c> and tells whether it describes a newer version.</summary>
public sealed class UpdateChecker(HttpClient http, Uri manifestUrl, Version current, InstallKind kind)
{
    public static readonly Uri DefaultManifestUrl = new("https://leandrocannizzaro.github.io/LiveHelpDesk/update.json");

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The manifest address; LIVEHELPDESK_UPDATE_URL overrides it (a fork, a local test server).</summary>
    public static Uri ManifestUrl() =>
        Uri.TryCreate(Environment.GetEnvironmentVariable("LIVEHELPDESK_UPDATE_URL"), UriKind.Absolute, out var url) ? url : DefaultManifestUrl;

    /// <summary>The release notes page, published next to the manifest.</summary>
    public static Uri ReleaseNotesUrl(Uri manifestUrl) => new(manifestUrl, "release-notes.html");

    public async Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken = default)
    {
        // Pages sits behind a CDN that caches for minutes: a changing query string asks for a fresh copy.
        var url = new UriBuilder(manifestUrl) { Query = $"t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}" }.Uri;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(await response.Content.ReadAsStringAsync(cancellationToken), Json)
            ?? throw new InvalidDataException("update.json vuoto.");
        return Evaluate(manifest, current, kind);
    }

    /// <summary>The update the manifest offers to this copy, or null when it is not newer.</summary>
    public static AvailableUpdate? Evaluate(UpdateManifest manifest, Version current, InstallKind kind)
    {
        var version = UpdateVersions.TryParse(manifest.Version) ?? throw new InvalidDataException($"Versione non valida in update.json: '{manifest.Version}'.");
        if (version <= current)
        {
            return null;
        }

        var package = kind switch
        {
            InstallKind.Msi => manifest.Msi,
            InstallKind.ZipPerUser or InstallKind.ZipAllUsers => manifest.Zip,
            _ => null,
        };
        return new AvailableUpdate(manifest, version, package);
    }
}

public static class InstallKindDetector
{
    /// <summary>The running copy's install kind, from where it lives and the MSI's registry marker.</summary>
    public static InstallKind Detect() =>
        Detect(AppContext.BaseDirectory, HasMsiMarker(), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), File.Exists);

    public static InstallKind Detect(string baseDirectory, bool msiMarker, string programFiles, Func<string, bool> fileExists)
    {
        var dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
        var inProgramFiles = !string.IsNullOrEmpty(programFiles)
            && dir.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(programFiles)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        if (msiMarker && inProgramFiles)
        {
            return InstallKind.Msi;
        }

        // install.ps1 copies uninstall.ps1 next to the app: its presence is what tells an installed ZIP from a build folder.
        if (fileExists(Path.Combine(dir, "uninstall.ps1")))
        {
            return inProgramFiles ? InstallKind.ZipAllUsers : InstallKind.ZipPerUser;
        }

        return InstallKind.None;
    }

    private static bool HasMsiMarker()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(@"Software\LiveHelpDesk");
            return key?.GetValue("Installed") is int value && value == 1;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}

/// <summary>Downloads a package, checks it, and hands it to a small script that installs it once the app has closed.</summary>
public static class UpdateInstaller
{
    public static string DownloadDirectory(Version version) =>
        Path.Combine(Path.GetTempPath(), "LiveHelpDesk-update", version.ToString(3));

    /// <summary>Downloads into <paramref name="directory"/> and verifies the SHA-256; a mismatch deletes the file and throws.</summary>
    public static async Task<string> DownloadAsync(
        HttpClient http, UpdatePackage package, string directory, IProgress<double>? progress, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Path.GetFileName(package.Url.LocalPath));

        using (var response = await http.GetAsync(package.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? package.Size;
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var target = File.Create(path);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                done += read;
                if (total > 0)
                {
                    progress?.Report((double)done / total);
                }
            }
        }

        await VerifyAsync(path, package.Sha256, cancellationToken);
        return path;
    }

    public static async Task VerifyAsync(string path, string expectedSha256, CancellationToken cancellationToken = default)
    {
        string actual;
        await using (var stream = File.OpenRead(path))
        {
            actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        }

        if (!string.Equals(actual, expectedSha256?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(path);
            throw new InvalidDataException("Il file scaricato non corrisponde a quello pubblicato (SHA-256 diverso): aggiornamento annullato.");
        }
    }

    /// <summary>
    /// Starts the installer script, detached: it waits for this process to end, installs, and starts the app again.
    /// The caller must exit right after, so the files can be replaced.
    /// </summary>
    public static void Launch(InstallKind kind, string packagePath, string installDirectory, string executable, int processId, string logPath)
    {
        if (kind == InstallKind.None)
        {
            throw new InvalidOperationException("Questa copia non è installata: l'aggiornamento automatico non è disponibile.");
        }

        var script = Path.Combine(Path.GetDirectoryName(packagePath)!, "apply-update.ps1");
        File.WriteAllText(script, ApplyScript);
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath(),
        };
        foreach (var arg in BuildArguments(kind, script, packagePath, installDirectory, executable, processId, logPath))
        {
            start.ArgumentList.Add(arg);
        }

        Process.Start(start);
    }

    public static IReadOnlyList<string> BuildArguments(
        InstallKind kind, string script, string packagePath, string installDirectory, string executable, int processId, string logPath) =>
    [
        "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script,
        "-Kind", kind.ToString(),
        "-Package", packagePath,
        "-InstallDir", Path.TrimEndingDirectorySeparator(installDirectory),
        "-Exe", executable,
        "-WaitPid", processId.ToString(),
        "-Log", logPath,
    ];

    // Runs after the app has closed. The MSI asks for UAC by itself (per-machine package); install.ps1 -AllUsers is elevated here.
    // Whatever happens, the app is started again as the user (never elevated), so a failed update leaves the old version running.
    internal const string ApplyScript = """
        param([string]$Kind, [string]$Package, [string]$InstallDir, [string]$Exe, [int]$WaitPid, [string]$Log)
        $ErrorActionPreference = 'Stop'
        Start-Transcript -Path $Log -Append | Out-Null
        try {
            if ($WaitPid) { Wait-Process -Id $WaitPid -Timeout 60 -ErrorAction SilentlyContinue }
            if ($Kind -eq 'Msi') {
                $msiLog = [IO.Path]::ChangeExtension($Log, '.msi.log')
                $p = Start-Process msiexec.exe -ArgumentList "/i `"$Package`" /passive /norestart /l*v `"$msiLog`"" -Wait -PassThru
                if ($p.ExitCode -notin 0, 3010) { throw "msiexec ha restituito $($p.ExitCode)" }
            }
            else {
                $dir = Join-Path (Split-Path $Package) 'zip'
                if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
                Expand-Archive -Path $Package -DestinationPath $dir -Force
                $arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $dir 'install.ps1')`" -InstallDir `"$InstallDir`" -NoStart"
                if ($Kind -eq 'ZipAllUsers') {
                    $p = Start-Process powershell.exe -ArgumentList "$arguments -AllUsers" -Verb RunAs -Wait -PassThru
                }
                else {
                    $p = Start-Process powershell.exe -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
                }
                if ($p.ExitCode) { throw "install.ps1 ha restituito $($p.ExitCode)" }
            }
            Write-Output 'Aggiornamento completato.'
        }
        catch {
            Write-Output "Aggiornamento non riuscito: $_"
        }
        finally {
            Start-Process -FilePath $Exe
            Stop-Transcript | Out-Null
        }
        """;
}
