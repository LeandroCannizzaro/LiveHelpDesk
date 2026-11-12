param([string]$MsiPath)

$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot)
$helper = Join-Path $root 'installer\count-installation.ps1'

function Assert($condition, [string]$message) {
    if (-not $condition) { throw $message }
}

$state = @{
    Requests = [Collections.Generic.List[object]]::new()
    Warnings = [Collections.Generic.List[string]]::new()
    Failure = $null
    ContentType = 'image/svg+xml;charset=UTF-8'
}

function Invoke-WebRequest {
    param([switch]$UseBasicParsing, $Uri, $TimeoutSec, $MaximumRedirection, $UserAgent, $Headers)
    $state.Requests.Add(@{
        Uri = $Uri; Timeout = $TimeoutSec; Redirects = $MaximumRedirection
        UserAgent = $UserAgent; Headers = $Headers
    })
    if ($state.Failure) { throw $state.Failure }
    return @{ Headers = @{ 'Content-Type' = $state.ContentType } }
}

function Write-Warning {
    param([string]$Message)
    $state.Warnings.Add($Message)
}

& $helper
Assert ($state.Requests.Count -eq 1) 'Un completamento deve generare una sola richiesta.'
Assert ($state.Warnings.Count -eq 0) 'Il segnale riuscito non deve generare avvisi.'
$request = $state.Requests[0]
Assert ($request.Uri -ceq 'https://hits.sh/leandrocannizzaro.github.io/livehelpdesk/installations.svg') 'Endpoint del contatore inatteso.'
Assert ($request.Timeout -eq 5) 'Il timeout deve essere limitato a 5 secondi.'
Assert ($request.Redirects -eq 0) 'Non seguire redirect che potrebbero duplicare il segnale.'
Assert ($request.UserAgent -eq 'LiveHelpDesk-Setup') 'Non inviare informazioni sul PC nello User-Agent.'
Assert ($request.Headers.Count -eq 1 -and $request.Headers['Cache-Control'] -eq 'no-cache') 'Inviare solo le intestazioni previste.'

foreach ($failure in @(
    [Net.WebException]::new('Rete non disponibile'),
    [Net.WebException]::new('Timeout', [Net.WebExceptionStatus]::Timeout),
    [InvalidOperationException]::new('Richiesta non disponibile')
)) {
    $state.Requests.Clear()
    $state.Warnings.Clear()
    $state.Failure = $failure
    & $helper
    Assert ($state.Requests.Count -eq 1) 'Non ritentare un segnale che potrebbe essere stato conteggiato.'
    Assert ($state.Warnings.Count -eq 1) 'Il mancato conteggio deve produrre un avviso senza interrompere il setup.'
}
$state.Failure = $null
$state.ContentType = 'text/html'
$state.Warnings.Clear()
& $helper
Assert ($state.Warnings.Count -eq 1) 'Una risposta non SVG deve essere segnalata.'

$package = [xml](Get-Content (Join-Path $root 'installer\LiveHelpDesk.Setup\Package.wxs') -Raw)
$counter = @($package.Wix.Package.CustomAction) | Where-Object Id -eq 'CountCompletedInstallation'
$command = @($package.Wix.Package.CustomAction) | Where-Object Id -eq 'SetInstallCounterCommand'
$commandSequence = @($package.Wix.Package.InstallExecuteSequence.Custom) | Where-Object Action -eq 'SetInstallCounterCommand'
$sequence = @($package.Wix.Package.InstallExecuteSequence.Custom) | Where-Object Action -eq 'CountCompletedInstallation'
Assert ($counter.Execute -eq 'immediate' -and $counter.Return -eq 'ignore') 'Il segnale MSI non deve far fallire o annullare il setup.'
Assert ($commandSequence.After -eq 'InstallFinalize' -and $sequence.After -eq 'SetInstallCounterCommand') 'Contare solo dopo il completamento della transazione MSI.'
Assert ($sequence.Condition -ceq 'NOT Installed AND NOT REMOVE AND NOT UPGRADINGPRODUCTCODE AND INSTALLCOUNTER = "1"') 'La condizione MSI deve escludere manutenzione, rimozione e opt-out.'
Assert ($commandSequence.Condition -ceq $sequence.Condition) 'Preparare il comando solo quando deve essere eseguito.'
Assert ($command.Value.Contains('[#InstallCounterScript]')) 'Il setup deve eseguire lo script condiviso installato.'
Assert ($counter.DllEntry -eq 'WixQuietExec') 'Il segnale deve nascondere la console e registrare gli avvisi nel log MSI.'

$install = Get-Content (Join-Path $root 'install.ps1') -Raw
Assert ($install.Contains('[switch]$NoInstallCounter')) 'Manca la disattivazione del contatore ZIP.'
Assert ($install.TrimEnd().EndsWith('if (-not $NoInstallCounter) { & (Join-Path $InstallDir ''count-installation.ps1'') }')) 'Il segnale ZIP deve essere l''ultima operazione e rispettare l''opt-out.'
Assert ($install.IndexOf('Write-Host "LiveHelpDesk $version installato') -lt $install.LastIndexOf("'count-installation.ps1'")) 'Non contare prima del completamento del setup ZIP.'

$page = Get-Content (Join-Path $root 'docs\index.html') -Raw
Assert (-not ($page -match '<img[^>]+src="https://hits\.sh/')) 'Il sito non deve caricare l''immagine che incrementa il contatore.'
Assert ($page.Contains('https%3A%2F%2Fhits.sh%2Fapi%2Furns%2Fleandrocannizzaro.github.io%2Flivehelpdesk%2Finstallations')) 'Il badge deve leggere le statistiche dello stesso contatore.'
Assert ($page.Contains('query=%24.total')) 'Il badge deve mostrare il totale, non i conteggi giornalieri.'

$fixture = Join-Path ([IO.Path]::GetTempPath()) ('lhd-counter-test-' + [Guid]::NewGuid().ToString('N'))
try {
    New-Item (Join-Path $fixture 'app') -ItemType Directory | Out-Null
    'fixture' | Set-Content (Join-Path $fixture 'app\LiveHelpDesk.exe')
    Copy-Item $helper (Join-Path $fixture 'app')
    Copy-Item (Join-Path $root 'install.ps1') $fixture
    Copy-Item (Join-Path $root 'uninstall.ps1') $fixture
    $state.ContentType = 'image/svg+xml'
    $state.FailCopy = $false
    $state.Starts = 0

    & {
        function Get-ItemProperty { return @{ pv = 'fixture' } }
        function Get-Service { }
        function Get-Process { }
        function Set-ItemProperty { }
        function Start-Process { $state.Starts++ }
        function New-Item {
            param($Path, $ItemType, [switch]$Force)
            if ($Path -notlike 'HKCU:*') {
                Microsoft.PowerShell.Management\New-Item -Path $Path -ItemType $ItemType -Force:$Force
            }
        }
        function New-Object {
            $shortcut = [pscustomobject]@{ TargetPath = ''; WorkingDirectory = ''; Description = '' }
            $shortcut | Add-Member -MemberType ScriptMethod -Name Save -Value { }
            $shell = [pscustomobject]@{ Shortcut = $shortcut }
            $shell | Add-Member -MemberType ScriptMethod -Name CreateShortcut -Value { return $this.Shortcut }
            return $shell
        }
        function robocopy {
            param($Source, $Destination)
            if ($state.FailCopy) { throw [IO.IOException]::new('Copia fallita (test)') }
            Copy-Item (Join-Path $Source '*') $Destination -Recurse -Force
            $global:LASTEXITCODE = 0
        }

        $setup = Join-Path $fixture 'install.ps1'
        $destination = Join-Path $fixture 'installed'
        $state.Requests.Clear()
        & $setup -InstallDir $destination -NoStart
        Assert ($state.Requests.Count -eq 1 -and $state.Starts -eq 0) 'Un setup ZIP riuscito deve contare anche con NoStart.'
        $state.Requests.Clear()
        & $setup -InstallDir $destination -NoInstallCounter
        Assert ($state.Requests.Count -eq 0 -and $state.Starts -eq 1) 'L''opt-out non deve inviare segnali o impedire l''avvio.'
        $state.FailCopy = $true
        $failed = $false
        try { & $setup -InstallDir $destination -NoStart }
        catch [IO.IOException] { $failed = $true }
        Assert ($failed -and $state.Requests.Count -eq 0) 'Un setup ZIP fallito non deve essere conteggiato.'
    }
}
finally {
    Remove-Item -LiteralPath $fixture -Recurse -Force
}

if ($MsiPath) {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $session = $installer.OpenPackage((Resolve-Path $MsiPath).Path, 1)
    foreach ($case in @(
        @{ Name = 'Installazione'; Installed = ''; REMOVE = ''; UPGRADINGPRODUCTCODE = ''; WIX_UPGRADE_DETECTED = ''; INSTALLCOUNTER = '1'; Expected = 1 },
        @{ Name = 'Aggiornamento'; Installed = ''; REMOVE = ''; UPGRADINGPRODUCTCODE = ''; WIX_UPGRADE_DETECTED = 'old-product'; INSTALLCOUNTER = '1'; Expected = 1 },
        @{ Name = 'Riparazione'; Installed = '1'; REMOVE = ''; UPGRADINGPRODUCTCODE = ''; WIX_UPGRADE_DETECTED = ''; INSTALLCOUNTER = '1'; Expected = 0 },
        @{ Name = 'Disinstallazione'; Installed = '1'; REMOVE = 'ALL'; UPGRADINGPRODUCTCODE = ''; WIX_UPGRADE_DETECTED = ''; INSTALLCOUNTER = '1'; Expected = 0 },
        @{ Name = 'Rimozione versione precedente'; Installed = '1'; REMOVE = 'ALL'; UPGRADINGPRODUCTCODE = 'new-product'; WIX_UPGRADE_DETECTED = ''; INSTALLCOUNTER = '1'; Expected = 0 },
        @{ Name = 'Opt-out'; Installed = ''; REMOVE = ''; UPGRADINGPRODUCTCODE = ''; WIX_UPGRADE_DETECTED = ''; INSTALLCOUNTER = '0'; Expected = 0 }
    )) {
        foreach ($property in @('Installed', 'REMOVE', 'UPGRADINGPRODUCTCODE', 'WIX_UPGRADE_DETECTED', 'INSTALLCOUNTER')) {
            $session.Property($property) = $case[$property]
        }
        Assert ($session.EvaluateCondition($sequence.Condition) -eq $case.Expected) "Condizione MSI errata: $($case.Name)."
    }
    $database = $installer.OpenDatabase((Resolve-Path $MsiPath).Path, 0)
    $view = $database.OpenView('SELECT `Condition`, `Sequence` FROM `InstallExecuteSequence` WHERE `Action` = ''CountCompletedInstallation''')
    $view.Execute()
    $record = $view.Fetch()
    Assert ($null -ne $record) 'L''MSI compilato non contiene il contatore.'
    Assert ($record.StringData(1) -ceq $sequence.Condition) 'La condizione compilata deve coincidere con quella sorgente.'
    $counterSequence = $record.IntegerData(2)
    $view.Close()
    $view = $database.OpenView('SELECT `Sequence` FROM `InstallExecuteSequence` WHERE `Action` = ''InstallFinalize''')
    $view.Execute()
    Assert ($counterSequence -gt $view.Fetch().IntegerData(1)) 'Nell''MSI compilato il contatore deve seguire InstallFinalize.'
    $view.Close()
    $view = $database.OpenView('SELECT `FileName` FROM `File` WHERE `File` = ''InstallCounterScript''')
    $view.Execute()
    Assert ($null -ne $view.Fetch()) 'L''MSI compilato deve includere lo script.'
    $view.Close()
}

Write-Host 'Test contatore installazioni superati (nessuna richiesta al contatore reale).'
