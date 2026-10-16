param([Parameter(Mandatory = $true)][string]$Subject)
$ErrorActionPreference = 'Stop'
$certificates = @(Get-ChildItem Cert:\CurrentUser\Root, Cert:\LocalMachine\Root |
    Where-Object { $_.Subject -eq $Subject } | Sort-Object Thumbprint -Unique)
if ($certificates.Count -ne 1) { throw 'Provide the exact subject of one trusted root certificate.' }
$destination = Join-Path $PSScriptRoot '../docker/certs/local-build-root.crt'
$pem = "-----BEGIN CERTIFICATE-----`n" + [Convert]::ToBase64String($certificates[0].RawData, [Base64FormattingOptions]::InsertLineBreaks) + "`n-----END CERTIFICATE-----`n"
[IO.File]::WriteAllText([IO.Path]::GetFullPath($destination), $pem, [Text.Encoding]::ASCII)
Write-Output "Exported public root certificate for Docker build: $($certificates[0].Subject)"
