using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;

using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;

// Uso:
//   DataverseProbe [--org https://qgs.crm4.dynamics.com] [--only wam,browser,device] [--client <guid>] [--tenant <guid|domain>]
//
// Senza argomenti prova, in ordine, WAM, browser di sistema e device code con i client ID pubblici
// elencati sotto. Con --client prova anche il client ID di una tua app registration
// (redirect URI: http://localhost, "Allow public client flows" = Sì).
// Il token resta in memoria: non viene salvato né stampato. L'output mostra solo metadati dei ticket
// (numero, date, stato) e titoli troncati: non committare né condividere l'output senza controllarlo.

var org = Arg("--org") ?? "https://qgs.crm4.dynamics.com";
var tenant = Arg("--tenant") ?? "e40359a3-6fe2-46f4-b190-a1f4f644834d";
var only = (Arg("--only") ?? "wam,browser,device").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
var ownClient = Arg("--client");
// --first: si ferma al primo metodo che funziona (evita di rifare tutti i login).
var stopAtFirst = Environment.GetCommandLineArgs().Contains("--first");
org = org.TrimEnd('/');
string[] scopes = [$"{org}/.default"];

// Client ID pubblici usati da strumenti Microsoft/community per Dataverse.
// Nessuno è "nostro": se il tenant li blocca, il risultato ti dice perché.
var clients = new List<(string Name, string Id)>
{
    ("Power Platform CLI (pac)", "9cee029c-6210-4654-90bb-17e6e9d36617"),
    ("Esempi Dataverse / XrmToolBox", "51f81489-12ee-4a9e-aaae-a2591f45987d"),
};
if (!string.IsNullOrWhiteSpace(ownClient)) clients.Insert(0, ("App registration propria", ownClient!));

var results = new List<(string Method, string Client, string Outcome)>();
string? firstWorkingToken = null;
string? firstWorkingLabel = null;

foreach (var (name, id) in clients)
{
    if (stopAtFirst && firstWorkingToken is not null) break;
    foreach (var method in only)
    {
        if (stopAtFirst && firstWorkingToken is not null) break;
        var label = $"{method} / {name}";
        Console.WriteLine();
        Console.WriteLine($"=== {label} ===");
        try
        {
            var token = await AcquireAsync(method, id);
            var probe = await CallAsync(token, "WhoAmI");
            Console.WriteLine($"  Token ottenuto, WhoAmI: {(probe.ok ? "OK" : "FALLITO " + probe.info)}");
            results.Add((method, name, probe.ok ? "OK" : "token ok ma API rifiutata: " + probe.info));
            if (probe.ok && firstWorkingToken is null) { firstWorkingToken = token; firstWorkingLabel = label; }
        }
        catch (MsalException ex)
        {
            var msg = $"{ex.ErrorCode}: {FirstLine(ex.Message)}";
            Console.WriteLine("  " + msg);
            results.Add((method, name, "KO " + msg));
        }
        catch (Exception ex)
        {
            Console.WriteLine("  " + ex.GetType().Name + ": " + FirstLine(ex.Message));
            results.Add((method, name, "KO " + ex.GetType().Name + ": " + FirstLine(ex.Message)));
        }
    }
}

Console.WriteLine();
Console.WriteLine("================ RIEPILOGO ================");
foreach (var r in results) Console.WriteLine($"{r.Method,-8} {r.Client,-32} {r.Outcome}");

if (firstWorkingToken is null)
{
    Console.WriteLine();
    Console.WriteLine("Nessun metodo ha funzionato: incolla il riepilogo sopra (senza dati sensibili) per decidere come procedere.");
    return 1;
}

Console.WriteLine();
Console.WriteLine($"Uso il primo metodo funzionante per leggere i dati: {firstWorkingLabel}");
await DumpDataModelAsync(firstWorkingToken);
return 0;

// ------------------------------------------------------------------------------------------

async Task<string> AcquireAsync(string method, string clientId)
{
    var builder = PublicClientApplicationBuilder.Create(clientId)
        .WithAuthority($"https://login.microsoftonline.com/{tenant}");

    switch (method)
    {
        case "wam":
            builder = builder
                .WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows))
                .WithRedirectUri($"ms-appx-web://Microsoft.AAD.BrokerPlugin/{clientId}");
            break;
        default:
            builder = builder.WithRedirectUri("http://localhost");
            break;
    }

    var app = builder.Build();

    switch (method)
    {
        case "wam":
            // Prima in silenzio con l'account Windows, poi con la UI del broker.
            var accounts = await app.GetAccountsAsync();
            var login = accounts.FirstOrDefault() ?? PublicClientApplication.OperatingSystemAccount;
            try { return (await app.AcquireTokenSilent(scopes, login).ExecuteAsync()).AccessToken; }
            catch (MsalUiRequiredException) { }
            return (await app.AcquireTokenInteractive(scopes)
                .WithParentActivityOrWindow(GetConsoleWindow())
                .ExecuteAsync()).AccessToken;

        case "browser":
            return (await app.AcquireTokenInteractive(scopes)
                .WithUseEmbeddedWebView(false)
                .ExecuteAsync()).AccessToken;

        case "device":
            return (await app.AcquireTokenWithDeviceCode(scopes, dc =>
            {
                Console.WriteLine("  " + dc.Message);
                return Task.CompletedTask;
            }).ExecuteAsync()).AccessToken;

        default:
            throw new ArgumentException("Metodo sconosciuto: " + method);
    }
}

async Task<(bool ok, string info, string body)> CallAsync(string token, string path)
{
    using var http = new HttpClient { BaseAddress = new Uri(org + "/api/data/v9.2/") };
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    http.DefaultRequestHeaders.Add("OData-MaxVersion", "4.0");
    http.DefaultRequestHeaders.Add("OData-Version", "4.0");
    http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    using var resp = await http.GetAsync(path);
    var body = await resp.Content.ReadAsStringAsync();
    return (resp.IsSuccessStatusCode, $"HTTP {(int)resp.StatusCode}", body);
}

async Task DumpDataModelAsync(string token)
{
    var who = await CallAsync(token, "WhoAmI");
    if (who.ok)
    {
        using var d = JsonDocument.Parse(who.body);
        Console.WriteLine($"Utente (systemuserid): {d.RootElement.GetProperty("UserId").GetString()}");
    }

    Console.WriteLine();
    Console.WriteLine("--- Ultimi 5 ticket (incident) ---");
    var inc = await CallAsync(token,
        "incidents?$select=ticketnumber,title,createdon,modifiedon,statecode,statuscode,prioritycode,_ownerid_value,_customerid_value" +
        "&$orderby=modifiedon desc&$top=5");
    if (inc.ok)
    {
        using var d = JsonDocument.Parse(inc.body);
        foreach (var r in d.RootElement.GetProperty("value").EnumerateArray())
        {
            var title = r.GetProperty("title").GetString() ?? "";
            if (title.Length > 40) title = title[..40] + "…";
            Console.WriteLine($"  {Str(r, "ticketnumber")} | {Str(r, "modifiedon")} | stato {Str(r, "statecode")}/{Str(r, "statuscode")} | prio {Str(r, "prioritycode")} | {title}");
        }
    }
    else Console.WriteLine("  " + inc.info + " " + FirstLine(inc.body));

    Console.WriteLine();
    Console.WriteLine("--- Entità che potrebbero contenere i messaggi del portale ---");
    // "contains" non è supportato sui metadati: si scaricano i nomi e si filtra in locale.
    var defs = await CallAsync(token, "EntityDefinitions?$select=LogicalName");
    if (defs.ok)
    {
        using var d = JsonDocument.Parse(defs.body);
        string[] keys = ["comment", "message", "chat", "note", "email", "queue", "sla", "incident", "gs_", "cr754_", "adx_"];
        var names = d.RootElement.GetProperty("value").EnumerateArray()
            .Select(e => e.GetProperty("LogicalName").GetString() ?? "")
            .Where(n => keys.Any(k => n.Contains(k, StringComparison.OrdinalIgnoreCase)))
            .Order().ToList();
        Console.WriteLine($"  ({names.Count} corrispondenze) " + string.Join(", ", names));
    }
    else Console.WriteLine("  " + defs.info + " " + FirstLine(defs.body));

    foreach (var (set, order) in new[] { ("adx_portalcomments", "createdon"), ("annotations", "createdon") })
    {
        Console.WriteLine();
        Console.WriteLine($"--- {set}: ultimi 3 (solo metadati) ---");
        var r = await CallAsync(token, $"{set}?$orderby={order} desc&$top=3");
        if (!r.ok) { Console.WriteLine("  " + r.info); continue; }
        using var d = JsonDocument.Parse(r.body);
        foreach (var row in d.RootElement.GetProperty("value").EnumerateArray())
        {
            // Solo i nomi dei campi, mai i contenuti dei messaggi.
            Console.WriteLine("  campi: " + string.Join(", ", row.EnumerateObject().Select(p => p.Name).Where(n => !n.StartsWith('@')).Take(40)));
            break;
        }
    }

    Console.WriteLine();
    Console.WriteLine("--- Campi dell'entità incident (attributi custom: prefisso diverso da quelli standard) ---");
    var attrs = await CallAsync(token,
        "EntityDefinitions(LogicalName='incident')/Attributes?$select=LogicalName,AttributeType&$filter=IsCustomAttribute eq true");
    if (attrs.ok)
    {
        using var d = JsonDocument.Parse(attrs.body);
        foreach (var a in d.RootElement.GetProperty("value").EnumerateArray())
            Console.WriteLine($"  {a.GetProperty("LogicalName").GetString()} ({a.GetProperty("AttributeType").GetString()})");
    }
    else Console.WriteLine("  " + attrs.info);

    await DumpPhase2Async(token);
}

// Seconda parte: etichette dei valori, conteggi, change tracking e dove finiscono le e-mail/note.
async Task DumpPhase2Async(string token)
{
    async Task<JsonElement?> GetJsonAsync(string path)
    {
        var r = await CallAsync(token, path);
        if (!r.ok) { Console.WriteLine($"  ({r.info}) {FirstLine(r.body)}"); return null; }
        using var d = JsonDocument.Parse(r.body);
        return d.RootElement.Clone();
    }

    Console.WriteLine();
    Console.WriteLine("--- Change tracking su incident / email ---");
    foreach (var ent in new[] { "incident", "email", "annotation" })
    {
        var j = await GetJsonAsync($"EntityDefinitions(LogicalName='{ent}')?$select=ChangeTrackingEnabled");
        if (j is not null) Console.WriteLine($"  {ent}: ChangeTrackingEnabled = {j.Value.GetProperty("ChangeTrackingEnabled")}");
    }

    Console.WriteLine();
    Console.WriteLine("--- Etichette dei valori (incident) ---");
    foreach (var (attr, kind) in new[]
    {
        ("statecode", "State"), ("statuscode", "Status"), ("prioritycode", "Picklist"),
        ("gs_statocomunicazioni", "Picklist"), ("gs_hdresponse", "Picklist"), ("gs_sollecito", "Picklist"),
        ("gs_insospeso", "Picklist"), ("gs_prioritariqualificata", "Picklist"), ("gs_classificazione", "Picklist"),
    })
    {
        Console.WriteLine($"  {attr}:");
        var j = await GetJsonAsync(
            $"EntityDefinitions(LogicalName='incident')/Attributes(LogicalName='{attr}')/Microsoft.Dynamics.CRM.{kind}AttributeMetadata" +
            "?$select=LogicalName&$expand=OptionSet($select=Options)");
        if (j is null) continue;
        foreach (var o in j.Value.GetProperty("OptionSet").GetProperty("Options").EnumerateArray())
        {
            var lbl = o.GetProperty("Label").GetProperty("UserLocalizedLabel");
            var text = lbl.ValueKind == JsonValueKind.Null ? "?" : lbl.GetProperty("Label").GetString();
            var st = o.TryGetProperty("State", out var s) && s.ValueKind != JsonValueKind.Null ? $" (stato {s})" : "";
            Console.WriteLine($"    {o.GetProperty("Value")} = {text}{st}");
        }
    }

    var who = await GetJsonAsync("WhoAmI");
    var me = who?.GetProperty("UserId").GetString();

    Console.WriteLine();
    Console.WriteLine("--- Conteggi ticket ---");
    async Task CountAsync(string title, string filter)
    {
        var j = await GetJsonAsync($"incidents?$select=ticketnumber&$filter={filter}&$count=true&$top=1");
        if (j is not null) Console.WriteLine($"  {title}: {j.Value.GetProperty("@odata.count")}");
    }
    await CountAsync("aperti (statecode 0)", "statecode eq 0");
    if (me is not null)
    {
        await CountAsync("aperti, owner = io", $"statecode eq 0 and _ownerid_value eq {me}");
        await CountAsync("aperti, gs_utentediliv2 = io", $"statecode eq 0 and _gs_utentediliv2_value eq {me}");
        await CountAsync("aperti, gs_proprietarioliv2 = io", $"statecode eq 0 and _gs_proprietarioliv2_value eq {me}");
    }

    Console.WriteLine();
    Console.WriteLine("--- Ultimo ticket modificato: campi chiave e attività collegate (solo metadati) ---");
    var last = await GetJsonAsync("incidents?$select=incidentid,ticketnumber,gs_dataricezioneultimamail,gs_statocomunicazioni,lastinteraction,createdon,modifiedon" +
        "&$orderby=modifiedon desc&$top=1");
    if (last is not null && last.Value.GetProperty("value").GetArrayLength() > 0)
    {
        var row = last.Value.GetProperty("value")[0];
        var id = row.GetProperty("incidentid").GetString();
        Console.WriteLine($"  {Str(row, "ticketnumber")} | creato {Str(row, "createdon")} | ultima mail {Str(row, "gs_dataricezioneultimamail")} | statocomunicazioni {Str(row, "gs_statocomunicazioni")} | lastinteraction {Str(row, "lastinteraction")}");

        foreach (var (set, sel) in new[] { ("emails", "createdon,directioncode,statuscode"), ("annotations", "createdon,isdocument"), ("tasks", "createdon,statuscode"), ("phonecalls", "createdon,directioncode") })
        {
            var j = await GetJsonAsync($"{set}?$select={sel}&$filter=_regardingobjectid_value eq {id}&$orderby=createdon desc&$top=5".Replace("_regardingobjectid_value", set == "annotations" ? "_objectid_value" : "_regardingobjectid_value"));
            if (j is null) continue;
            Console.WriteLine($"  {set} collegati: {j.Value.GetProperty("value").GetArrayLength()}");
            foreach (var r in j.Value.GetProperty("value").EnumerateArray())
                Console.WriteLine("    " + string.Join(" | ", r.EnumerateObject().Where(p => !p.Name.StartsWith('@')).Select(p => $"{p.Name}={p.Value}")));
        }
    }

    Console.WriteLine();
    Console.WriteLine("--- E-mail create nelle ultime 24 ore ---");
    var since = DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-ddTHH:mm:ssZ");
    var em = await GetJsonAsync($"emails?$select=createdon,directioncode,_regardingobjectid_value&$filter=createdon gt {since}&$count=true&$top=1");
    if (em is not null) Console.WriteLine($"  totale: {em.Value.GetProperty("@odata.count")}");
    var emIn = await GetJsonAsync($"emails?$select=createdon&$filter=createdon gt {since} and directioncode eq false&$count=true&$top=1");
    if (emIn is not null) Console.WriteLine($"  in ingresso: {emIn.Value.GetProperty("@odata.count")}");
}

string? Arg(string name)
{
    var a = Environment.GetCommandLineArgs();
    var i = Array.IndexOf(a, name);
    return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
}

static string Str(JsonElement e, string prop) =>
    e.TryGetProperty(prop, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : "-";

static string FirstLine(string s) => s.Split('\n')[0].Trim();

[DllImport("kernel32.dll")]
static extern IntPtr GetConsoleWindow();
