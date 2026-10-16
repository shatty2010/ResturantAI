param(
    [string]$ApiUrl = 'http://localhost:8080',
    [string]$RabbitUrl = 'http://localhost:15672',
    [string]$RabbitUser = 'restaurant',
    [string]$RabbitPassword = 'restaurant_dev',
    [int]$TimeoutSeconds = 90
)
$ErrorActionPreference = 'Stop'
$headers = @{ Authorization = 'Basic ' + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("${RabbitUser}:${RabbitPassword}")) }

function Wait-Until([scriptblock]$Check, [string]$Description) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (& $Check) { return }
        Start-Sleep -Seconds 1
    }
    throw "Timed out: $Description"
}

function Get-Queue([string]$Name) {
    Invoke-RestMethod "$RabbitUrl/api/queues/%2F/$Name" -Headers $headers
}

function Publish-Event($EventPayload, [string]$MessageId) {
    $body = @{
        properties = @{ delivery_mode = 2; content_type = 'application/json'; message_id = $MessageId }
        routing_key = 'order.created'
        payload = ($EventPayload | ConvertTo-Json -Compress)
        payload_encoding = 'string'
    } | ConvertTo-Json -Depth 5
    $result = Invoke-RestMethod "$RabbitUrl/api/exchanges/%2F/restaurant.orders/publish" -Method Post -Headers $headers -ContentType 'application/json' -Body $body
    if (-not $result.routed) { throw 'Event was not routed to kitchen.orders' }
}

Wait-Until { try { (Invoke-WebRequest "$ApiUrl/health/ready" -UseBasicParsing).StatusCode -eq 200 } catch { $false } } 'API readiness'
$menu = Invoke-RestMethod "$ApiUrl/api/menu"
if ($menu.Count -ne 10) { throw "Expected 10 seeded menu items; found $($menu.Count)" }
$item = $menu[0]
$request = @{
    customerId = [Guid]::NewGuid().ToString()
    items = @(@{ menuItemId = $item.id; quantity = 2; unitPrice = 0.01; specialInstructions = 'Smoke test' })
} | ConvertTo-Json -Depth 5
$response = Invoke-WebRequest "$ApiUrl/api/orders" -Method Post -ContentType 'application/json' -Body $request -UseBasicParsing
if ($response.StatusCode -ne 201) { throw 'Expected HTTP 201' }
$order = $response.Content | ConvertFrom-Json
if ([decimal]$order.totalAmount -ne 2 * [decimal]$item.price) { throw 'Server price calculation failed' }
Wait-Until { (Invoke-RestMethod "$ApiUrl/api/orders/$($order.id)").status -eq 'Ready' } 'order becomes Ready through PostgreSQL/outbox/RabbitMQ/kitchen'
$ready = Invoke-RestMethod "$ApiUrl/api/orders/$($order.id)"
Write-Output "PASS: menu, server-side prices, order creation, outbox publication, kitchen processing: $($order.id) is Ready"

# Wait for RabbitMQ management statistics, then verify the duplicate is consumed.
Wait-Until { $q = Get-Queue 'kitchen.orders'; $q.message_stats.ack -ge 1 } 'initial acknowledgement statistics'
$ackBefore = (Get-Queue 'kitchen.orders').message_stats.ack
$eventId = [Guid]::NewGuid().ToString()
Publish-Event @{ eventId = $eventId; orderId = $order.id; customerId = $order.customerId; createdAt = $order.createdAt } $eventId
Wait-Until { (Get-Queue 'kitchen.orders').message_stats.ack -gt $ackBefore } 'duplicate acknowledgement'
$after = Invoke-RestMethod "$ApiUrl/api/orders/$($order.id)"
if ($after.status -ne 'Ready' -or $after.updatedAt -ne $ready.updatedAt) { throw 'Duplicate event changed the completed order' }
Write-Output 'PASS: duplicate event acknowledged without modifying Ready order'

$deadBefore = (Get-Queue 'kitchen.orders.dead').messages
$badId = [Guid]::NewGuid().ToString()
Publish-Event @{ eventId = $badId; orderId = [Guid]::NewGuid().ToString(); customerId = $order.customerId; createdAt = $order.createdAt } $badId
Wait-Until { (Get-Queue 'kitchen.orders.dead').messages -gt $deadBefore } 'poison event dead-lettered'
Write-Output 'PASS: invalid event routed to durable dead-letter queue'
