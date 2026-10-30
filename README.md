# LiveHelpDesk

Monitor proattivo dei ticket di assistenza su Dynamics 365: un **servizio Windows** controlla Dynamics in continuo e,
secondo filtri configurabili, avvisa su toast, overlay, e-mail, Teams, WhatsApp e SMS. L'**app desktop Avalonia**
mostra il sito Dynamics in una WebView e un'icona/overlay che lampeggia quando serve attenzione.

Solo Windows (10/11). C# / .NET 10, Avalonia 12.

> Il piano di implementazione completo e le decisioni sono nella [issue #1](https://github.com/LeandroCannizzaro/LiveHelpDesk/issues/1).

## Struttura

| Percorso | Contenuto |
|---|---|
| `src/LiveHelpDesk.Abstractions` | contratti: eventi, canali, notifiche |
| `src/LiveHelpDesk.Core` | percorsi, chiavi di dedup (poi: stato, regole, IPC) |
| `src/LiveHelpDesk.Dynamics` | accesso a Dataverse (opzioni, poi auth e client) |
| `src/LiveHelpDesk.Channels` | canali di uscita: e-mail (MailKit), Teams (webhook), SMS/WhatsApp (Twilio) |
| `src/LiveHelpDesk.Ipc` | contratti, server e client della pipe servizio↔app |
| `src/LiveHelpDesk.Service` | servizio Windows (Worker) |
| `src/LiveHelpDesk.App` | app Avalonia con WebView |
| `tests/LiveHelpDesk.Tests` | test unitari |
| `tools/` | sonde usa e getta della Fase 0 (`DataverseProbe`, `TokenDeltaSpike`) |

## Compilare e provare

```powershell
dotnet build LiveHelpDesk.slnx
dotnet test tests/LiveHelpDesk.Tests
dotnet run --project src/LiveHelpDesk.App
```

Il servizio si prova da una finestra normale (non serve essere amministratori):

```powershell
dotnet run --project src/LiveHelpDesk.Service -- run      # il monitor (restare aperta)
dotnet run --project src/LiveHelpDesk.Service -- login    # un'altra finestra: codice, poi Windows Hello
dotnet run --project src/LiveHelpDesk.Service -- dry-run  # prova le regole sui ticket di oggi (sola lettura)
dotnet run --project src/LiveHelpDesk.Service -- status   # stato, coda, ultimi eventi
```

Canali esterni (e-mail SMTP, Teams via webhook Workflows, SMS e WhatsApp via Twilio):

```powershell
LiveHelpDesk.Service channels-template > canali.json   # modello da compilare (poi cancellare il file: contiene segreti)
LiveHelpDesk.Service channels-import canali.json        # il servizio cifra i segreti
LiveHelpDesk.Service test email                         # messaggio di prova (anche: teams, sms, whatsapp)
LiveHelpDesk.Service channels                           # stato di ogni canale
```

Installato come servizio (una volta, da amministratore): `LiveHelpDesk.Service install`.
Opzione `--data <cartella>` per tenere i dati altrove (prove).

Requisiti: .NET 10 SDK e il WebView2 Runtime (già presente in Windows 11 e con Office/Edge).

L'app tiene il profilo della WebView in `%LocalAppData%\LiveHelpDesk\webview`. Con l'accesso tramite passkey
(Windows Hello) Entra non emette cookie persistenti: la WebView chiede di nuovo l'accesso a ogni riavvio dell'app,
per questo l'app resterà residente nell'area di notifica. Il monitoraggio non ne dipende (usa il token del servizio).
Opzione di prova: `--exit-after <secondi>` chiude la finestra da sola.
Gli eventi di navigazione (solo host e percorso) vanno in `%LocalAppData%\LiveHelpDesk\logs\webview-spike.log`.

## Stato

Fasi 1-4 (scheletro, nucleo, servizio, canali esterni) in corso; l'app mostra per ora solo Dynamics nella WebView. Repository privato.
