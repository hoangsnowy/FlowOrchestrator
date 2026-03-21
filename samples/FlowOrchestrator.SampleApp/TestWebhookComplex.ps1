param(
    [string]$BaseUrl = "http://localhost:5201",
    [string]$IdOrSlug = "trigger-body-test",
    [string]$WebhookKey = "",
    [switch]$UseBearer,
    [switch]$PollRun,
    [int]$PollTimeoutSeconds = 30
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$uri = "{0}/flows/api/webhook/{1}" -f $BaseUrl.TrimEnd('/'), $IdOrSlug

$payload = @{
    id = "evt_20260321_9f7c2a1b"
    timestamp = "2026-03-21T14:12:33.456Z"
    type = "NEW_JOB"
    source = "crunchworks"
    version = "2.3"
    correlationId = "corr-7d8f1c90-2f3a-4d7c-a620-6f6e3b8d11aa"
    retry = @{
        count = 1
        max = 5
        nextAttemptAt = "2026-03-21T14:17:33.456Z"
    }
    payload = @{
        id = "job_00098321"
        tenantId = "tenant_apac_01"
        client = "ACME_INSURANCE"
        projectExternalReference = "PRJ-SEA-2026-0019"
        status = "InReview"
        priority = "High"
        submittedAt = "2026-03-21T13:58:10.000Z"
        effectiveDate = "2026-04-01"
        currency = "USD"
        totalAmount = 12450.75
        flags = @{
            isUrgent = $true
            requiresManualReview = $false
            containsPII = $true
        }
        policy = @{
            policyNo = "POL-8891-XY"
            productCode = "HOME_PLUS"
            channel = "BROKER"
            broker = @{
                id = "BRK-1009"
                name = "Pacific Broker Ltd"
                contacts = @(
                    @{
                        name = "Alice Nguyen"
                        email = "alice.nguyen@example.com"
                        phone = "+84-28-1234-5678"
                    },
                    @{
                        name = "Tom Lee"
                        email = "tom.lee@example.com"
                        phone = "+65-6123-4567"
                    }
                )
            }
        }
        insuredParties = @(
            @{
                partyId = "P-001"
                type = "Individual"
                fullName = "John Carter"
                dob = "1990-02-14"
                addresses = @(
                    @{
                        type = "Home"
                        line1 = "12 Orchard Road"
                        city = "Singapore"
                        country = "SG"
                        postalCode = "238832"
                    }
                )
            },
            @{
                partyId = "P-002"
                type = "Company"
                companyName = "Carter Holdings Pte Ltd"
                registrationNo = "201912345N"
                addresses = @(
                    @{
                        type = "HQ"
                        line1 = "8 Marina Blvd"
                        city = "Singapore"
                        country = "SG"
                        postalCode = "018981"
                    }
                )
            }
        )
        premiumBreakdown = @(
            @{ code = "BASE"; amount = 10000.0 },
            @{ code = "FIRE_EXT"; amount = 1450.25 },
            @{ code = "FLOOD_EXT"; amount = 1000.5 }
        )
        documents = @(
            @{
                docId = "DOC-7781"
                type = "ApplicationForm"
                mimeType = "application/pdf"
                sizeBytes = 482391
                checksumSha256 = "0f4b7c3a58d19f9f4f1f17c0f2e7aa6bbf8d98f5f97e6ef6b8a09d1e5f0c72ab"
            },
            @{
                docId = "DOC-7782"
                type = "PropertyPhoto"
                mimeType = "image/jpeg"
                sizeBytes = 1823490
                checksumSha256 = "f3d3ec8f87f6ff65c1f3c2fa7e4ab6808ef69a1d6f8c2fd66d4f9cb8f12f64d1"
            }
        )
        riskScores = @{
            fireRisk = 0.31
            floodRisk = 0.62
            theftRisk = 0.24
            modelVersion = "risk-v5.12"
        }
        tags = @("home", "renewal", "priority-high", "region-apac")
        customFields = @{
            agentCode = "AGT-771"
            campaign = "Q2-RETENTION"
            notes = $null
        }
    }
}

$json = $payload | ConvertTo-Json -Depth 20

$headers = @{}
if (-not [string]::IsNullOrWhiteSpace($WebhookKey)) {
    if ($UseBearer.IsPresent) {
        $headers["Authorization"] = "Bearer $WebhookKey"
    }
    else {
        $headers["X-Webhook-Key"] = $WebhookKey
    }
}

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
        ($responseBody | ConvertTo-Json -Depth 50)
    }
    catch {
        $responseBody = $response.Content
        Write-Host $response.Content
    }
}

if ($response.StatusCode -lt 200 -or $response.StatusCode -ge 300) {
    throw ("Webhook request failed with status {0}." -f $response.StatusCode)
}

if (-not $PollRun.IsPresent) {
    return
}

if ($null -eq $responseBody -or -not $responseBody.PSObject.Properties.Name.Contains("runId")) {
    Write-Warning "No runId in response, skip polling."
    return
}

$runId = [string]$responseBody.runId
$runUri = "{0}/flows/api/runs/{1}" -f $BaseUrl.TrimEnd('/'), $runId
$deadline = (Get-Date).AddSeconds($PollTimeoutSeconds)
$terminal = @("Succeeded", "Failed", "Completed", "Canceled", "Cancelled")

Write-Host ("Polling run {0} for up to {1}s..." -f $runId, $PollTimeoutSeconds)
while ((Get-Date) -lt $deadline) {
    $runResp = Invoke-WebRequest -Method Get -Uri $runUri -SkipHttpErrorCheck
    if ($runResp.StatusCode -ge 200 -and $runResp.StatusCode -lt 300) {
        $run = $runResp.Content | ConvertFrom-Json -Depth 50
        $status = [string]$run.status
        Write-Host ("Run status: {0}" -f $status)

        if ($terminal -contains $status) {
            Write-Host ($run | ConvertTo-Json -Depth 50)
            break
        }
    }
    else {
        Write-Warning ("Polling failed with status {0}" -f $runResp.StatusCode)
    }

    Start-Sleep -Seconds 2
}
