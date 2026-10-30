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
| `src/LiveHelpDesk.Channels` | canali di uscita (e-mail, Teams, WhatsApp, SMS) |
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

Requisiti: .NET 10 SDK e il WebView2 Runtime (già presente in Windows 11 e con Office/Edge).

L'app salva il profilo della WebView in `%LocalAppData%\LiveHelpDesk\webview`, così l'accesso a Dynamics resta
valido tra un avvio e l'altro. Opzione di prova: `--exit-after <secondi>` chiude la finestra da sola.
Gli eventi di navigazione (solo host e percorso) vanno in `%LocalAppData%\LiveHelpDesk\logs\webview-spike.log`.

## Stato

Fase 1 (scheletro e spike WebView) in corso. Repository privato.
