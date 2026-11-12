$ErrorActionPreference = 'Stop'

# Only the setup requests this image. Pages reads the statistics API instead.
$uri = 'https://hits.sh/leandrocannizzaro.github.io/livehelpdesk/installations.svg'
try {
    $response = Invoke-WebRequest -UseBasicParsing -Uri $uri -TimeoutSec 5 -MaximumRedirection 0 `
        -UserAgent 'LiveHelpDesk-Setup' -Headers @{ 'Cache-Control' = 'no-cache' }
    if ($response.Headers['Content-Type'] -notlike 'image/svg+xml*') {
        throw [IO.InvalidDataException]::new('Il contatore non ha restituito un''immagine SVG.')
    }
}
catch [Net.WebException], [InvalidOperationException], [IO.InvalidDataException] {
    Write-Warning "Conteggio installazione non disponibile; il setup resta completato: $($_.Exception.Message)"
}
