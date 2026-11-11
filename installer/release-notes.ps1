<#
.SYNOPSIS
  Prepara e verifica le note di rilascio e il manuale prima di pubblicare una versione.
.DESCRIPTION
  Senza -Check elenca tutte le modifiche dall'ultimo tag (commit con descrizione e file toccati): e' la base da cui
  scrivere la sezione della nuova versione in docs\release-notes.html, in italiano e per chi usa il programma.

  Con -Check (lo usa il workflow Release) si ferma con un errore se:
    - docs\release-notes.html non ha la sezione id="vX.Y.Z" della versione;
    - il manuale (docs\index.html) non e' cambiato dall'ultimo tag.
.EXAMPLE
  .\installer\release-notes.ps1                  # modifiche dall'ultimo tag, per scrivere le note
  .\installer\release-notes.ps1 -Check           # verifica per la versione di Directory.Build.props
  .\installer\release-notes.ps1 -Check -Version 0.2.0
#>
param(
    [string]$Version,
    [switch]$Check
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$notes = Join-Path $root 'docs\release-notes.html'
$manual = 'docs/index.html'

if (-not $Version) {
    $Version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.VersionPrefix | Where-Object { $_ } | Select-Object -First 1
}

# L'ultimo tag prima di questa versione: se HEAD e' gia' il tag vX.Y.Z (nel workflow) si guarda al commit precedente.
$tag = "v$Version"
$headTags = @(git -C $root tag --points-at HEAD)
$from = if ($headTags -contains $tag) { 'HEAD^' } else { 'HEAD' }
$previous = git -C $root describe --tags --abbrev=0 --match 'v*' $from 2>$null
if ($LASTEXITCODE) { $previous = $null }
$range = if ($previous) { "$previous..HEAD" } else { 'HEAD' }

if (-not $Check) {
    Write-Host "Versione $Version - modifiche da $(if ($previous) { $previous } else { "l'inizio" })" -ForegroundColor Cyan
    git -C $root --no-pager log $range --reverse --format='%n=== %h %ad %s%n%b' --date=short
    Write-Host "`nFile modificati:" -ForegroundColor Cyan
    git -C $root --no-pager diff --stat $range
    Write-Host "`nNon ancora committato:" -ForegroundColor Cyan
    git -C $root status --short
    Write-Host "`nScrivi la sezione id=`"$tag`" in docs\release-notes.html e aggiorna docs\index.html." -ForegroundColor Yellow
    return
}

$problems = @()
if (-not (Select-String -Path $notes -Pattern "id=`"$([regex]::Escape($tag))`"" -Quiet)) {
    $problems += "docs/release-notes.html non contiene la sezione id=`"$tag`": scrivi le note di rilascio della versione $Version."
}

if ($previous) {
    git -C $root diff --quiet $previous HEAD -- $manual
    if ($LASTEXITCODE -eq 0) {
        $problems += "Il manuale ($manual) non e' cambiato da ${previous}: aggiornalo con le novita' della versione $Version."
    }
}

if ($problems) {
    $problems | ForEach-Object { Write-Host "::error::$_" }
    throw "Note di rilascio o manuale non pronti per la versione $Version."
}

Write-Host "Note di rilascio e manuale pronti per la versione $Version (modifiche da $(if ($previous) { $previous } else { "l'inizio" }))."

# git diff --quiet lascia 1 in $LASTEXITCODE quando il manuale e' cambiato (il caso buono): non deve diventare l'esito dello script.
exit 0
