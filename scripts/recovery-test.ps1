param([string]$ApiUrl = 'http://localhost:8080', [int]$TimeoutSeconds = 120)
$ErrorActionPreference = 'Stop'
Push-Location (Join-Path $PSScriptRoot '..')
function New-TestOrder {
    $body = @{
        customerId = [Guid]::NewGuid().ToString()
        items = @(@{ menuItemId = '00000000-0000-0000-0000-000000000001'; quantity = 1 })
    } | ConvertTo-Json -Depth 5
    Invoke-RestMethod "$ApiUrl/api/orders" -Method Post -ContentType 'application/json' -Body $body
}
function Wait-Status([string]$Id, [string]$Status) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $order = Invoke-RestMethod "$ApiUrl/api/orders/$Id"
        if ($order.status -eq $Status) { return }
        Start-Sleep -Milliseconds 200
    }
    throw "Order $Id did not reach $Status"
}
try {
    docker compose stop rabbitmq
    if ($LASTEXITCODE -ne 0) { throw 'Could not stop RabbitMQ' }
    try {
        $order = New-TestOrder
        Start-Sleep -Seconds 2
        $pending = Invoke-RestMethod "$ApiUrl/api/orders/$($order.id)"
        if ($pending.status -ne 'Created') { throw 'Order should remain Created during broker outage' }
    }
    finally {
        docker compose start rabbitmq
        if ($LASTEXITCODE -ne 0) { throw 'Could not restore RabbitMQ' }
    }
    Wait-Status $order.id 'Ready'
    Write-Output "PASS: order $($order.id) persisted during broker outage and reached Ready after recovery"

    $interrupted = New-TestOrder
    Wait-Status $interrupted.id 'Preparing'
    docker compose restart kitchen-worker
    if ($LASTEXITCODE -ne 0) { throw 'Could not restart kitchen worker' }
    Wait-Status $interrupted.id 'Ready'
    Write-Output "PASS: interrupted Preparing order $($interrupted.id) resumed after worker restart"
}
finally { Pop-Location }
