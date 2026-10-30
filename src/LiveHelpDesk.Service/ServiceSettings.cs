using System.Text.Json;
using LiveHelpDesk.Core.Polling;
using LiveHelpDesk.Dynamics;

namespace LiveHelpDesk.Service;

/// <summary>Machine settings in <c>config.json</c>. Everything has a default, so a missing file is fine.</summary>
public sealed class ServiceSettings
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string Org { get; set; } = "https://qgs.crm4.dynamics.com";

    public Guid AppId { get; set; } = Guid.Parse("d2ed1394-6c4e-ed11-bba3-00224899337b");

    public string TenantId { get; set; } = "e40359a3-6fe2-46f4-b190-a1f4f644834d";

    /// <summary>Explicit proxy (e.g. http://proxy.corp:8080); empty uses the system one with the machine's credentials.</summary>
    public string? Proxy { get; set; }

    public int PollSeconds { get; set; } = 30;

    public int OverlapMinutes { get; set; } = 2;

    public int MaxGapMinutes { get; set; } = 360;

    public int UnattendedMinutes { get; set; } = 30;

    public int ReconcileMinutes { get; set; } = 15;

    public DynamicsOptions Dynamics => new(new Uri(Org), AppId, TenantId);

    public TimeSpan PollInterval => TimeSpan.FromSeconds(Math.Max(5, PollSeconds));

    public PollingOptions Polling => new()
    {
        Overlap = TimeSpan.FromMinutes(OverlapMinutes),
        MaxGap = TimeSpan.FromMinutes(MaxGapMinutes),
        UnattendedAfter = TimeSpan.FromMinutes(UnattendedMinutes),
        ReconcileEvery = TimeSpan.FromMinutes(ReconcileMinutes),
    };

    /// <summary>Reads the file, writing a documented default one the first time.</summary>
    public static ServiceSettings LoadOrCreate(string path)
    {
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new ServiceSettings(), Json));
        }

        return JsonSerializer.Deserialize<ServiceSettings>(File.ReadAllText(path), Json) ?? new ServiceSettings();
    }
}
