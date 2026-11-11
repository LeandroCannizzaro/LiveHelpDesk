using LiveHelpDesk.Core.Notifications;
using Microsoft.Extensions.Logging;

namespace LiveHelpDesk.Service;

/// <summary>
/// The notification texts in <c>templates.json</c>, reloaded when the file changes. A file with errors never
/// replaces the last good set, so notifications keep going out with the texts that worked.
/// </summary>
public sealed class TemplatesProvider
{
    private readonly string _path;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private DateTime _loadedFrom;
    private IReadOnlyList<NotificationTemplate> _templates = [];

    public TemplatesProvider(string path, ILogger logger)
    {
        _path = path;
        _logger = logger;

        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, TemplatesConfig.Serialize(new TemplatesFile()));
        }

        Reload();
    }

    public IReadOnlyList<NotificationTemplate> Current
    {
        get
        {
            lock (_gate)
            {
                if (File.GetLastWriteTimeUtc(_path) != _loadedFrom)
                {
                    Reload();
                }

                return _templates;
            }
        }
    }

    /// <summary>The file as it is on disk.</summary>
    public string ReadJson() => File.ReadAllText(_path);

    /// <summary>Makes <paramref name="json"/> the texts in force, but only if it is valid; the old texts stay otherwise.</summary>
    public TemplatesLoadResult Replace(string json)
    {
        var result = TemplatesConfig.Parse(json);
        if (!result.IsValid)
        {
            return result;
        }

        lock (_gate)
        {
            var temp = _path + ".tmp";
            File.WriteAllText(temp, TemplatesConfig.Serialize(result.File!));
            File.Move(temp, _path, overwrite: true);
            Reload();
        }

        return result;
    }

    private void Reload()
    {
        _loadedFrom = File.GetLastWriteTimeUtc(_path);
        var result = TemplatesConfig.Parse(File.ReadAllText(_path));

        if (result.IsValid)
        {
            _templates = result.File!.Templates;
            _logger.LogInformation("Loaded {Count} notification texts from {Path}", _templates.Count, _path);
        }
        else
        {
            _logger.LogError("Notification texts file {Path} has errors, keeping the previous texts: {Errors}", _path, string.Join(" | ", result.Errors));
        }
    }
}
