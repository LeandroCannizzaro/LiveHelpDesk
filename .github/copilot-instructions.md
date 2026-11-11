# Istruzioni per GitHub Copilot

## Convenzioni del repository

- **Lingua dei commit:** per questo repository i messaggi di commit si scrivono **in italiano**
  (titolo e corpo), con descrizioni chiare e congruenti con le modifiche.

## Pubblicazione di una versione

A **ogni** pubblicazione (nuovo tag `vX.Y.Z`), prima del tag:

1. **Analizzare tutte le modifiche dall'ultimo tag** (`.\installer\release-notes.ps1` le elenca: commit, descrizioni, file).
2. **Note di rilascio:** aggiungere in cima a `docs/release-notes.html` la sezione `<article id="vX.Y.Z">` (e la voce
   nell'indice) con le ultime modifiche, in italiano e per l'utente: *Novità*, *Modifiche*, *Correzioni*.
   La pagina è su GitHub Pages ed è aperta dall'app in *Impostazioni → Aggiornamenti → Note di rilascio*.
3. **Manuale utente:** aggiornare `docs/index.html` (pubblicato su https://leandrocannizzaro.github.io/LiveHelpDesk/)
   con tutto ciò che è cambiato.
4. Aggiornare `VersionPrefix` in `Directory.Build.props`.

Il workflow `Release` (`release-notes.ps1 -Check`) si ferma se mancano la sezione della versione o l'aggiornamento del manuale.
