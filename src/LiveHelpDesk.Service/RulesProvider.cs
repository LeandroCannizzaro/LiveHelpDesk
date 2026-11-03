using LiveHelpDesk.Core.Rules;
using Microsoft.Extensions.Logging;

namespace LiveHelpDesk.Service;

/// <summary>
/// The rules in <c>rules.json</c>, reloaded when the file changes. A file with errors never replaces the last
/// good set: a typo while editing must not silence the monitor.
/// </summary>
public sealed class RulesProvider
{
    private readonly string _path;
    private readonly ILogger _logger;
    private DateTime _loadedFrom;
    private IReadOnlyList<Rule> _rules = [];

    public RulesProvider(string path, ILogger logger)
    {
        _path = path;
        _logger = logger;

        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, RulesConfig.Serialize(DefaultRules.Create()));
            _logger.LogInformation("Created default rules at {Path}", path);
        }

        Reload();
    }

    public IReadOnlyList<Rule> Current
    {
        get
        {
            if (File.GetLastWriteTimeUtc(_path) != _loadedFrom)
            {
                Reload();
            }

            return _rules;
        }
    }

    /// <summary>The file as it is on disk.</summary>
    public string ReadJson() => File.ReadAllText(_path);

    /// <summary>Makes <paramref name="json"/> the rules in force, but only if it is valid; the old rules stay otherwise.</summary>
    public RulesLoadResult Replace(string json)
    {
        var result = RulesConfig.Parse(json);
        if (!result.IsValid)
        {
            return result;
        }

        var temp = _path + ".tmp";
        File.WriteAllText(temp, RulesConfig.Serialize(result.File!));
        File.Move(temp, _path, overwrite: true);
        Reload();
        return result;
    }

    private void Reload()
    {
        _loadedFrom = File.GetLastWriteTimeUtc(_path);
        var result = RulesConfig.Parse(File.ReadAllText(_path));

        if (result.IsValid)
        {
            _rules = result.File!.Rules;
            _logger.LogInformation("Loaded {Count} rules from {Path}", _rules.Count, _path);
        }
        else
        {
            _logger.LogError("Rules file {Path} has errors, keeping the previous rules: {Errors}", _path, string.Join(" | ", result.Errors));
        }
    }
}
