# LiveHelpDesk

Monitor proattivo dei ticket di assistenza su Dynamics 365: un **servizio Windows** controlla Dynamics in continuo e,
secondo filtri configurabili, avvisa su toast, overlay, e-mail, Teams, WhatsApp e SMS. L'**app desktop Avalonia**
mostra il sito Dynamics in una WebView e un'icona/overlay che lampeggia quando serve attenzione.

Solo Windows (10/11). C# / .NET 10, Avalonia 12.

📖 **Manuale d'uso (non tecnico):** https://leandrocannizzaro.github.io/LiveHelpDesk/

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
| `src/LiveHelpDesk.App` | app Avalonia: tray, toast, overlay, eventi, accesso, browser Dynamics |
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

**E-mail con Outlook aziendale (Microsoft 365).** L'invio SMTP con password (*Basic auth*) è dismesso in Exchange Online,
quindi per Microsoft 365 il canale e-mail ha il metodo *Outlook / Microsoft 365*: invia con Microsoft Graph
(`POST /me/sendMail`, permesso delegato `Mail.Send`) **come l'utente che accede**, senza salvare password. Nell'app,
*Canali → E-mail → Come inviare: Outlook*, poi **Accedi a Outlook** (codice da digitare su microsoft.com/devicelogin,
compatibile con Windows Hello) e *Salva*. Da riga di comando: `LiveHelpDesk.Service login-mail` / `logout-mail`.
Senza destinatari scrive all'account collegato; *Posta inviata* è facoltativa (di default no).
Il login è separato da quello di Dynamics (`msal-mail-cache.bin` cifrato con DPAPI) e ricordato dal servizio; se è
scaduto o assente le e-mail restano in coda (stato *serve l'accesso a Outlook*) e partono dopo il nuovo accesso.

Prerequisiti nel tenant Entra (da fare una volta, di solito dall'amministratore):

- Un'applicazione registrata come **client pubblico**: *Authentication → Allow public client flows = Sì*, redirect
  `http://localhost`, permesso delegato Microsoft Graph **`Mail.Send`**. Il suo *ID applicazione* e il tenant si
  scrivono nel form (campi *ID applicazione* e *Tenant*). Se vuoti si usa l'app pubblica *Microsoft Graph Command Line
  Tools* e il tenant dell'accesso a Dynamics: funziona solo se l'amministratore la consente.
- Il consenso a `Mail.Send` (dell'utente, se il tenant lo permette, altrimenti dell'amministratore).
- Le policy di accesso condizionale non devono bloccare il *device code flow* per questa app.

Con `msal-mail-cache.bin` e il resto, vale il limite sotto: il login Outlook è legato all'account che esegue il monitor.

Installato come servizio (una volta, da amministratore): `LiveHelpDesk.Service install`.
Opzione `--data <cartella>` per tenere i dati altrove (prove).

**Come gira il monitor.** L'app è solo un client: serve un monitor in funzione, in uno di due modi (mai insieme: la pipe è una).

| | processo `run` | servizio Windows |
|---|---|---|
| permessi | nessuno | amministratore, una volta (UAC) |
| parte | quando l'app lo avvia (opzione, attiva di default se il servizio non c'è) | con il PC |
| *Avvia* / *Ferma* | creano / terminano il processo | `sc start` / `sc stop` |
| *Installa* / *Disinstalla* | non c'entrano | creano / rimuovono il servizio |

Nel pannello *Servizio* dell'app: **Avvia/Ferma** agiscono sul processo `run` finché il servizio non è installato, poi
passano al servizio; **Installa/Disinstalla** riguardano solo il servizio e, se l'app non è già amministratore, Windows
chiede la conferma (l'app non si eleva mai da sola: rilancia `LiveHelpDesk.Service install|uninstall|start|stop`).
Se il processo `run` si ferma da solo viene riavviato qualche volta, poi segnalato come non riuscito. Alla chiusura
dell'app il processo che ha avviato viene fermato, il servizio no. `LiveHelpDesk.Service.exe` deve stare accanto all'app.
Il processo si ferma in modo pulito tramite l'evento `Local\LiveHelpDesk.Service.Stop` (solo se non è il vero servizio).

Limite noto: il processo `run` gira con l'utente dell'app, il servizio come `NT SERVICE\LiveHelpDesk`. Accesso a
Dynamics, canali e profili sono legati all'account che esegue il monitor, quindi cambiando modo vanno rifatti.

Requisiti: .NET 10 SDK e il WebView2 Runtime (già presente in Windows 11 e con Office/Edge).

L'app resta nell'area di notifica (chiudere la finestra la nasconde). Per vederla in azione senza ticket veri:
`LiveHelpDesk.exe --demo` (toast e overlay di esempio); `--minimized` parte solo nel tray.

L'app tiene il profilo della WebView in `%LocalAppData%\LiveHelpDesk\webview`. Con l'accesso tramite passkey
(Windows Hello) Entra non emette cookie persistenti: la WebView chiede di nuovo l'accesso a ogni riavvio dell'app,
per questo l'app resterà residente nell'area di notifica. Il monitoraggio non ne dipende (usa il token del servizio).
Opzione di prova: `--exit-after <secondi>` chiude la finestra da sola.
Gli eventi di navigazione (solo host e percorso) vanno in `%LocalAppData%\LiveHelpDesk\logs\webview-spike.log`.

## Rilasci e installazione

I pacchetti pronti all'uso si trovano nella pagina [Releases](https://github.com/LeandroCannizzaro/LiveHelpDesk/releases).
Ogni rilascio contiene due artefatti, prodotti automaticamente dal workflow `Release`:

| Artefatto | File | Quando usarlo |
|---|---|---|
| **Installer MSI** | `LiveHelpDesk-x.y.z-x64.msi` | installazione classica con doppio clic (per tutti gli utenti) |
| **Archivio portatile** | `LiveHelpDesk-x.y.z-win-x64.zip` | distribuzione da share di rete o senza installer (contiene gli script `install.cmd`/`uninstall.cmd`) |

(per esempio `git tag v0.1.1 && git push origin v0.1.1`)
oppure a mano da *Actions → Release → Run workflow*. Costruisce l'MSI (`installer\build-msi.ps1`) e lo ZIP (`installer\build-zip.ps1`),
li carica come artefatti e, sui tag, crea la Release allegandoli.

Il manuale d'uso è un sito statico in `docs/`, pubblicato su GitHub Pages dal workflow `.github/workflows/pages.yml`
(da abilitare una volta in *Settings → Pages → Source = GitHub Actions*).

## Installazione da una share di rete

Non è firmato: se i file vengono copiati da una share interna (senza "Mark of the Web") SmartScreen non interviene.

Dalla share, per l'utente corrente (senza amministratore):

```
install.cmd                                  # %LocalAppData%\Programs\LiveHelpDesk
install.cmd -Autostart                       # + avvio con Windows
install.cmd -AllUsers                        # %ProgramFiles%\LiveHelpDesk (amministratore)
install.cmd -AllUsers -InstallService        # + servizio Windows (amministratore)
uninstall.cmd [-AllUsers] [-RemoveData]      # conserva i dati salvo -RemoveData
```

Lo script avvisa se manca WebView2 Runtime, chiude app e servizio prima di aggiornare e non tocca mai i dati né `config.json`.

## Stato

Fasi 1-5 (scheletro, nucleo, servizio, canali esterni, app) in corso; mancano l'editor delle regole e dei canali nell'app.
Installer MSI e archivio ZIP disponibili (`installer\build-msi.ps1`, `installer\build-zip.ps1`) e pubblicati dal workflow `Release`. Repository privato.
