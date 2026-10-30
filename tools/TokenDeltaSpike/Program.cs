using System.Diagnostics;
using System.Text.Json;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

// Uso:
//   TokenDeltaSpike login                      accesso interattivo, salva la cache token (cifrata DPAPI)
//   TokenDeltaSpike silent                     rinnovo silenzioso FORZATO + riga nel log (da schedulare)
//   TokenDeltaSpike delta [--max-pages N]      prima lettura con change tracking; salva i deltaLink
//   TokenDeltaSpike delta-poll                 rilegge i deltaLink salvati e stampa le modifiche
//   Opzioni: [--org https://qgs.crm4.dynamics.com] [--tenant <guid>] [--full-email]
// Solo lettura. Stato in %LocalAppData%\LiveHelpDesk\spike\ (cache token, log, deltaLink).

const string ClientId = "9cee029c-6210-4654-90bb-17e6e9d36617"; // Power Platform CLI (client pubblico)

var cmd = args.FirstOrDefault() ?? "help";
var org = (Arg("--org") ?? "https://qgs.crm4.dynamics.com").TrimEnd('/');
var tenant = Arg("--tenant") ?? "e40359a3-6fe2-46f4-b190-a1f4f644834d";
var maxPages = int.TryParse(Arg("--max-pages"), out var mp) ? mp : 2;
var fullEmail = args.Contains("--full-email");
string[] scopes = [$"{org}/.default"];

var stateDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiveHelpDesk", "spike");
Directory.CreateDirectory(stateDir);
var logPath = Path.Combine(stateDir, "spike-token.log");
var deltaPath = Path.Combine(stateDir, "spike-delta.json");

var app = await BuildAppAsync();

switch (cmd)
{
    case "login":
    {
        var r = await app.AcquireTokenInteractive(scopes).WithUseEmbeddedWebView(false).ExecuteAsync();
        Log($"LOGIN ok account={r.Account.Username} expires={r.ExpiresOn:u}");
        return 0;
    }
    case "silent":
    {
        var acc = (await app.GetAccountsAsync()).FirstOrDefault();
        if (acc is null) { Log("SILENT no-account (esegui login)"); return 2; }
        try
        {
            var r = await app.AcquireTokenSilent(scopes, acc).WithForceRefresh(true).ExecuteAsync();
            Log($"SILENT ok source={r.AuthenticationResultMetadata.TokenSource} expires={r.ExpiresOn:u}");
            return 0;
        }
        catch (MsalUiRequiredException ex)
        {
            Log($"SILENT ui-required {ex.ErrorCode}: {FirstLine(ex.Message)}");
            return 3;
        }
    }
    case "delta":
    case "delta-poll":
    {
        var token = await SilentTokenAsync();
        if (token is null) return 3;
        var links = File.Exists(deltaPath)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(deltaPath)) ?? []
            : [];
        var sets = new (string Label, string Set, string Select)[]
        {
            ("incident", "incidents", "ticketnumber,title,statecode,statuscode,prioritycode,modifiedon,_ownerid_value"),
            ("email", "emails", "createdon,directioncode,statuscode,_regardingobjectid_value"),
        };
        foreach (var (label, set, select) in sets)
        {
            Console.WriteLine();
            Console.WriteLine($"=== {label} ({cmd}) ===");
            if (cmd == "delta")
            {
                var cap = label == "email" && !fullEmail ? maxPages : int.MaxValue;
                var link = await SyncAsync(token, $"{set}?$select={select}", cap, initial: true);
                if (link is not null) links[label] = link;
                else Console.WriteLine("  nessun deltaLink (letture limitate da --max-pages: usa --full-email per la sincronizzazione completa)");
            }
            else
            {
                if (!links.TryGetValue(label, out var saved)) { Console.WriteLine("  nessun deltaLink salvato (esegui prima 'delta')"); continue; }
                var link = await SyncAsync(token, saved, int.MaxValue, initial: false);
                if (link is not null) links[label] = link;
            }
        }
        File.WriteAllText(deltaPath, JsonSerializer.Serialize(links));
        return 0;
    }
    default:
        Console.WriteLine("Comandi: login | silent | delta [--max-pages N] [--full-email] | delta-poll");
        return 1;
}

// ------------------------------------------------------------------------------------------

async Task<IPublicClientApplication> BuildAppAsync()
{
    var a = PublicClientApplicationBuilder.Create(ClientId)
        .WithAuthority($"https://login.microsoftonline.com/{tenant}")
        .WithRedirectUri("http://localhost")
        .Build();
    var props = new StorageCreationPropertiesBuilder("msal-cache.bin", stateDir).Build();
    var helper = await MsalCacheHelper.CreateAsync(props);
    helper.RegisterCache(a.UserTokenCache);
    return a;
}

async Task<string?> SilentTokenAsync()
{
    var acc = (await app.GetAccountsAsync()).FirstOrDefault();
    if (acc is null) { Console.WriteLine("Nessun account in cache: esegui 'login'."); return null; }
    try { return (await app.AcquireTokenSilent(scopes, acc).ExecuteAsync()).AccessToken; }
    catch (MsalUiRequiredException ex) { Log($"SILENT ui-required {ex.ErrorCode}"); return null; }
}

// Legge tutte le pagine; restituisce il deltaLink (null se la lettura è stata interrotta da cap).
async Task<string?> SyncAsync(string token, string url, int cap, bool initial)
{
    using var http = new HttpClient { BaseAddress = new Uri(org + "/api/data/v9.2/") };
    http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    var sw = Stopwatch.StartNew();
    int pages = 0, rows = 0, removed = 0;
    string? next = url, delta = null;
    var sample = new List<string>();

    while (next is not null && pages < cap)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, next);
        req.Headers.Add("Prefer", "odata.track-changes,odata.maxpagesize=5000");
        req.Headers.Add("OData-MaxVersion", "4.0");
        req.Headers.Add("OData-Version", "4.0");
        using var resp = await http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) { Console.WriteLine($"  HTTP {(int)resp.StatusCode} {FirstLine(body)}"); return null; }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        pages++;
        foreach (var item in root.GetProperty("value").EnumerateArray())
        {
            rows++;
            if (item.TryGetProperty("@removed", out _)) removed++;
            if (!initial && sample.Count < 20)
                sample.Add(string.Join(" | ", item.EnumerateObject()
                    .Where(p => !p.Name.StartsWith("@odata") && !p.Name.StartsWith("title"))
                    .Select(p => $"{p.Name}={p.Value}")));
        }
        next = root.TryGetProperty("@odata.nextLink", out var nl) ? nl.GetString() : null;
        delta = root.TryGetProperty("@odata.deltaLink", out var dl) ? dl.GetString() : delta;
        if (initial) Console.Write($"\r  pagine {pages}, righe {rows}   ");
    }

    if (initial) Console.WriteLine();
    Console.WriteLine($"  righe={rows} pagine={pages} rimosse={removed} tempo={sw.Elapsed.TotalSeconds:F1}s " +
                      $"deltaLink={(delta is null ? "NO" : "sì")}");
    foreach (var s in sample) Console.WriteLine("    " + s);
    return delta;
}

void Log(string line)
{
    var s = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}";
    Console.WriteLine(s);
    File.AppendAllText(logPath, s + Environment.NewLine);
}

string? Arg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static string FirstLine(string s) => s.Split('\n')[0].Trim();
