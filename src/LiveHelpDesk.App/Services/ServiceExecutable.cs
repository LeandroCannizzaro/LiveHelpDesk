namespace LiveHelpDesk.App.Services;

/// <summary>Finds LiveHelpDesk.Service.exe: next to the app when installed, in the repository's build output when developing.</summary>
public static class ServiceExecutable
{
    public const string FileName = "LiveHelpDesk.Service.exe";

    public static string? Find(string baseDirectory, string? overridePath = null)
    {
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return Path.GetFullPath(overridePath);
        }

        foreach (var candidate in new[] { Path.Combine(baseDirectory, "service", FileName), Path.Combine(baseDirectory, FileName) })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        for (var directory = new DirectoryInfo(baseDirectory); directory is not null; directory = directory.Parent)
        {
            var bin = Path.Combine(directory.FullName, "src", "LiveHelpDesk.Service", "bin");
            if (Directory.Exists(bin))
            {
                return Directory.EnumerateFiles(bin, FileName, SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
            }
        }

        return null;
    }
}
