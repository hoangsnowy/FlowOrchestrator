<#
.SYNOPSIS
    Sends a payment event webhook to PaymentEventFlow and optionally polls the run to completion.

.DESCRIPTION
    Targets POST /flows/api/webhook/{IdOrSlug}.
    The default slug "payment-event" matches the webhook trigger in PaymentEventFlow.

    The payload envelope matches PaymentEventEnvelope (SerializeProbeStep):
        payload.id      → PaymentId in step output
        payload.orderId → OrderId in step output
        event           → EventType in step output
        timestamp       → Timestamp in step output

    Use -PollRun to wait for the run to reach a terminal status and print the final result.

.EXAMPLE
    # Trigger and watch — no auth required for payment-event
    .\TestWebhookComplex.ps1 -PollRun

.EXAMPLE
    # With optional secret (if webhookSecret is configured in the flow)
    .\TestWebhookComplex.ps1 -WebhookKey "payment-gateway-secret" -PollRun

.EXAMPLE
    # Target a different flow by slug or GUID
    .\TestWebhookComplex.ps1 -IdOrSlug "order-fulfillment" -WebhookKey "your-secret-key"
#>
param(
    [string]$BaseUrl            = "http://localhost:5201",
    [string]$IdOrSlug           = "payment-event",
    [string]$WebhookKey         = "",
    [switch]$UseBearer,
    [switch]$PollRun,
    [int]   $PollTimeoutSeconds = 60
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$uri = "{0}/flows/api/webhook/{1}" -f $BaseUrl.TrimEnd('/'), $IdOrSlug

# ── Payment event payload ─────────────────────────────────────────────────────
# This structure is deserialized by SerializeProbeStep into PaymentEventEnvelope.
# Fields: payload.id → PaymentId, payload.orderId → OrderId, event → EventType.
#
# The rich nested structure (card, billingAddress, metadata) exercises the
# @triggerBody()?.path expression system and JSON serialization depth.
# ─────────────────────────────────────────────────────────────────────────────
$payload = @{
    id        = "evt_$(Get-Date -Format 'yyyyMMddHHmmss')_9f7c2a1b"
    timestamp = (Get-Date -Format "yyyy-MM-ddTHH:mm:ss.fffZ")
    event     = "payment.confirmed"
    payload   = @{
        id       = "pay_00098321"
        orderId  = "ord_456789"
        amount   = 99.99
        currency = "USD"
        status   = "confirmed"
        method   = "card"
        card     = @{
            brand  = "visa"
            last4  = "4242"
            expiry = "12/28"
        }
        billingAddress = @{
            name       = "John Carter"
            line1      = "12 Orchard Road"
            city       = "Singapore"
            country    = "SG"
            postalCode = "238832"
        }
        lineItems = @(
            @{ sku = "SKU-001"; description = "FlowOrchestrator Pro License"; qty = 1; unitPrice = 79.99 },
            @{ sku = "SKU-002"; description = "Priority Support Add-on";      qty = 1; unitPrice = 19.99 }
            @{ sku = "SKU-003"; description = "Annual Renewal Discount";      qty = 1; unitPrice = -0.99 }  # negative for credit
        )
        metadata = @{
            customerId   = "cust_abc123"
            sessionId    = "sess_xyz789"
            ipAddress    = "203.0.113.1"
            userAgent    = "Mozilla/5.0 (OrderHub/2.1)"
            correlationId = "corr-7d8f1c90-2f3a-4d7c-a620-6f6e3b8d11aa"
            flags = @{
                isRecurring      = $false
                requiresCapture  = $false
                fraudScreenPassed = $true
            }
        }
    }
}

$json = $payload | ConvertTo-Json -Depth 20

# ── Auth headers (optional) ───────────────────────────────────────────────────
$headers = @{}
if (-not [string]::IsNullOrWhiteSpace($WebhookKey)) {
    if ($UseBearer.IsPresent) {
        $headers["Authorization"] = "Bearer $WebhookKey"
    }
    else {
        $headers["X-Webhook-Key"] = $WebhookKey
    }
}

# ── Send the webhook ──────────────────────────────────────────────────────────
Write-Host "POST $uri"
$response = Invoke-WebRequest `
    -Method Post `
    -Uri $uri `
    -ContentType "application/json" `
    -Body $json `
    -Headers $headers `
    -SkipHttpErrorCheck

Write-Host ("Status: {0}" -f $response.StatusCode)

$responseBody = $null
if (-not [string]::IsNullOrWhiteSpace($response.Content)) {
    try {
        $responseBody = $response.Content | ConvertFrom-Json -Depth 50
        $responseBody | ConvertTo-Json -Depth 50
    }
    catch {
        $responseBody = $response.Content
        Write-Host $response.Content
    }
}

if ($response.StatusCode -lt 200 -or $response.StatusCode -ge 300) {
    throw ("Webhook request failed with status {0}." -f $response.StatusCode)
}

if (-not $PollRun.IsPresent) { return }

# ── Poll run to terminal status ───────────────────────────────────────────────
if ($null -eq $responseBody -or -not $responseBody.PSObject.Properties.Name.Contains("runId")) {
    Write-Warning "No runId in response — skipping poll."
    return
}

$runId    = [string]$responseBody.runId
$runUri   = "{0}/flows/api/runs/{1}" -f $BaseUrl.TrimEnd('/'), $runId
$deadline = (Get-Date).AddSeconds($PollTimeoutSeconds)
$terminal = @("Succeeded", "Failed", "Completed", "Canceled", "Cancelled")

Write-Host ""
Write-Host ("Polling run {0} for up to {1}s ..." -f $runId, $PollTimeoutSeconds)
Write-Host ("Dashboard: {0}/flows  →  Runs  →  {1}" -f $BaseUrl.TrimEnd('/'), $runId)

while ((Get-Date) -lt $deadline) {
    $runResp = Invoke-WebRequest -Method Get -Uri $runUri -SkipHttpErrorCheck
    if ($runResp.StatusCode -ge 200 -and $runResp.StatusCode -lt 300) {
        $run    = $runResp.Content | ConvertFrom-Json -Depth 50
        $status = [string]$run.status
        Write-Host ("[{0}] Run status: {1}" -f (Get-Date -Format "HH:mm:ss"), $status)

        if ($terminal -contains $status) {
            Write-Host ""
            $run | ConvertTo-Json -Depth 50
            break
        }
    }
    else {
        Write-Warning ("Poll request failed with status {0}" -f $runResp.StatusCode)
    }

    Start-Sleep -Seconds 2
}
