#Requires -Version 7.4
<#
.SYNOPSIS
    Rehearses the FlowOrchestrator 1.34.x -> 2.0 upgrade end to end, the way a production customer
    performs it, following docs/articles/migrating-to-2.0.md.

.DESCRIPTION
    For every requested runtime x storage combination:
      1. builds the "OrderHub" customer app (./app) against FlowOrchestrator $FromVersion from nuget.org,
         on net8.0;
      2. packs this checkout as 2.0.0-rehearsal.1 into a local feed and builds the same app upgraded with
         ./upgrade-to-2.0.patch (the guide's Steps 1-3 and 6: retarget, package references, host code);
      3. starts a fresh database (and the Service Bus emulator), runs the 1.34 app, and creates live state:
         completed / failed / cancelled runs, runs parked in WaitForSignal, a Pending poll, a ForEach
         stopped mid-way, signed webhook deliveries (stored replay nonces), idempotency keys, a cron
         override and a paused schedule, and - from a web-only node - work that is queued but not executed;
      4. stops 1.34, runs the guide's pre-flight checks, starts 2.0 on the same database and verifies the
         migrator, the data, the queued work, cron, webhooks, idempotency and new runs;
      5. optionally rolls back to 1.34 (the guide's Rollback section) and verifies its claims.
    Prints a step -> result table per combination and "RESULT: N/N passed"; exits 1 on any failure.

    Needs Docker, the .NET 11 SDK (builds net8.0 and net10.0) and the .NET 8 ASP.NET Core runtime.

.EXAMPLE
    pwsh tests/upgrade/FlowOrchestrator.UpgradeRehearsal/rehearse.ps1
.EXAMPLE
    pwsh tests/upgrade/FlowOrchestrator.UpgradeRehearsal/rehearse.ps1 -Combination hangfire-postgresql -RollbackOn @()
#>
[CmdletBinding()]
param(
    # hangfire-sqlserver | hangfire-postgresql | servicebus-sqlserver; a comma-separated string works too
    # (pwsh -File passes "a,b" as one argument).
    [string[]] $Combination = @('hangfire-sqlserver', 'hangfire-postgresql', 'servicebus-sqlserver'),

    # Combinations on which the Rollback section (2.0 -> 1.34) is rehearsed after the upgrade.
    [string[]] $RollbackOn = @('hangfire-sqlserver', 'servicebus-sqlserver'),

    [string] $FromVersion = '1.34.7',

    [string] $WorkDir = (Join-Path ([IO.Path]::GetTempPath()) 'fo-upgrade-rehearsal'),

    # Reuse the packages already in the local feed instead of packing the checkout again.
    [switch] $SkipPack,

    # Leave the containers running at the end (for inspection). They are removed on the next run.
    [switch] $KeepContainers
)

$ErrorActionPreference = 'Stop'

$Known = @('hangfire-sqlserver', 'hangfire-postgresql', 'servicebus-sqlserver')
$Combination = @($Combination | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$RollbackOn = @($RollbackOn | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
foreach ($name in $Combination + $RollbackOn) {
    if ($name -notin $Known) { throw "Unknown combination '$name'. Known: $($Known -join ', ')." }
}
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$Here = $PSScriptRoot
$RepoRoot = (Resolve-Path (Join-Path $Here '../../..')).Path
$ToVersion = '2.0.0-rehearsal.1'          # must match upgrade-to-2.0.patch
$SaPassword = 'Rehearsal!Passw0rd'
$BasicAuth = 'Basic ' + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('ops:rehearsal-password'))
$HmacKey = 'rehearsal-hmac-key'
$Flow = @{
    Order    = '0a000000-0000-0000-0000-000000000001'
    Nightly  = '0a000000-0000-0000-0000-000000000002'
    Cleanup  = '0a000000-0000-0000-0000-000000000003'
    Approval = '0a000000-0000-0000-0000-000000000004'
    Shipment = '0a000000-0000-0000-0000-000000000005'
    Batch    = '0a000000-0000-0000-0000-000000000006'
    Payment  = '0a000000-0000-0000-0000-000000000007'
}
$NightlyJob = "flow-$($Flow.Nightly)-nightly"
$CleanupJob = "flow-$($Flow.Cleanup)-every_minute"

$Results = [System.Collections.Generic.List[object]]::new()
$C = $null   # the combination being rehearsed

# ── Output ────────────────────────────────────────────────────────────────────────────────────────
function Write-Phase([string] $Text) { Write-Host ''; Write-Host "── $Text" -ForegroundColor Cyan }

function Add-Result([string] $Step, [string] $Result, [string] $Detail) {
    $Results.Add([pscustomobject]@{ Combination = $C.Name; Step = $Step; Result = $Result; Detail = $Detail })
    $color = switch ($Result) { 'PASS' { 'Green' } 'FAIL' { 'Red' } default { 'DarkGray' } }
    Write-Host ("  [{0}] {1}{2}" -f $Result, $Step, ($Detail ? " — $Detail" : '')) -ForegroundColor $color
}

function Check([string] $Step, [bool] $Ok, [string] $Detail = '') { Add-Result $Step ($Ok ? 'PASS' : 'FAIL') $Detail }
function Note([string] $Step, [string] $Detail) { Add-Result $Step 'INFO' $Detail }

function Wait-Until([scriptblock] $Condition, [int] $TimeoutSec = 60, [int] $IntervalMs = 1000) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)
    do {
        try { if (& $Condition) { return $true } } catch { }
        Start-Sleep -Milliseconds $IntervalMs
    } while ([DateTime]::UtcNow -lt $deadline)
    return $false
}

function Get-FreePort {
    $l = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $l.Start(); $port = $l.LocalEndpoint.Port; $l.Stop(); return $port
}

function Invoke-Native([string] $Exe, [string[]] $Arguments, [switch] $AllowFailure) {
    $out = & $Exe @Arguments 2>&1
    if ($LASTEXITCODE -ne 0 -and -not $AllowFailure) {
        throw "$Exe $($Arguments -join ' ') failed ($LASTEXITCODE):`n$($out -join "`n")"
    }
    return $out
}

# ── Build ─────────────────────────────────────────────────────────────────────────────────────────
function Initialize-WorkDir {
    Write-Phase "Work directory $WorkDir"
    New-Item -ItemType Directory -Force $WorkDir | Out-Null
    $script:Feed = Join-Path $WorkDir 'feed'
    $script:NuGetPackages = Join-Path $WorkDir 'nuget-packages'
    New-Item -ItemType Directory -Force $Feed, $NuGetPackages | Out-Null
    # A NuGet.config + isolation stoppers at the work root, so the customer app restores like a
    # customer's: nuget.org plus the local 2.0 feed, no repository build policy.
    (Get-Content (Join-Path $Here 'NuGet.config.template') -Raw).Replace('__LOCAL_FEED__', $Feed) |
        Set-Content (Join-Path $WorkDir 'NuGet.config') -NoNewline
    Copy-Item (Join-Path $Here '../Directory.Build.props'), (Join-Path $Here '../Directory.Build.targets'), (Join-Path $Here '../Directory.Packages.props') $WorkDir -Force
    # Isolated package cache: a re-run always restores the freshly packed 2.0.0-rehearsal.1.
    Get-ChildItem $NuGetPackages -Directory -Filter 'floworchestrator.*' -ErrorAction SilentlyContinue |
        ForEach-Object { Remove-Item (Join-Path $_.FullName $ToVersion) -Recurse -Force -ErrorAction SilentlyContinue }
    $env:NUGET_PACKAGES = $NuGetPackages
}

function Publish-Packages {
    if ($SkipPack -and (Get-ChildItem $Feed -Filter "FlowOrchestrator.Core.$ToVersion.nupkg" -ErrorAction SilentlyContinue)) { return }
    Write-Phase "Packing this checkout as $ToVersion"
    Get-ChildItem $Feed -Filter '*.nupkg' | Remove-Item -Force
    $suffix = $ToVersion.Substring($ToVersion.IndexOf('-') + 1)
    Invoke-Native dotnet @('pack', (Join-Path $RepoRoot 'FlowOrchestrator.Libraries.slnx'), '-c', 'Release', '-o', $Feed, "-p:VersionSuffix=$suffix", '-v', 'q', '-nologo') | Out-Null
}

function Build-CustomerApp([string] $Version) {
    $dir = Join-Path $C.Root "app-$Version"
    Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
    Copy-Item (Join-Path $Here 'app') $dir -Recurse
    # LF everywhere, so upgrade-to-2.0.patch applies whatever core.autocrlf did to the checkout.
    Get-ChildItem $dir -File -Recurse | ForEach-Object {
        $text = [IO.File]::ReadAllText($_.FullName)
        if ($text.Contains("`r`n")) { [IO.File]::WriteAllText($_.FullName, $text.Replace("`r`n", "`n")) }
    }
    # Pins the combination for every command run in the folder (dotnet build, dotnet list package, ...).
    @"
<Project>
  <PropertyGroup>
    <CustomerRuntime>$($C.Runtime)</CustomerRuntime>
    <CustomerStorage>$($C.Storage)</CustomerStorage>
  </PropertyGroup>
</Project>
"@ | Set-Content (Join-Path $dir 'Directory.Build.props')
    if ($Version -eq 'v2') {
        Push-Location $dir
        try { Invoke-Native git @('apply', '-p1', '--whitespace=nowarn', (Join-Path $Here 'upgrade-to-2.0.patch')) | Out-Null }
        finally { Pop-Location }
    }
    elseif ($FromVersion -ne '1.34.7') {
        (Get-Content (Join-Path $dir 'CustomerApp.csproj') -Raw).Replace('<FlowOrchestratorVersion>1.34.7<', "<FlowOrchestratorVersion>$FromVersion<") |
            Set-Content (Join-Path $dir 'CustomerApp.csproj') -NoNewline
    }
    $out = Join-Path $C.Root "out-$Version"
    $build = Invoke-Native dotnet @('build', $dir, '-c', 'Release', '-o', $out, '-nologo', '-clp:NoSummary') -AllowFailure
    $ok = $LASTEXITCODE -eq 0
    $warnings = @($build | Where-Object { "$_" -match ': warning ' } | Sort-Object -Unique)
    $errors = @($build | Where-Object { "$_" -match ': error ' } | Sort-Object -Unique)
    return [pscustomobject]@{ Dir = $dir; Out = $out; Ok = $ok; Warnings = $warnings; Errors = $errors }
}

# ── Infrastructure ────────────────────────────────────────────────────────────────────────────────
function Remove-Containers {
    foreach ($name in 'fo-rehearsal-mssql', 'fo-rehearsal-pg', 'fo-rehearsal-sb') {
        & docker rm -f $name 2>&1 | Out-Null
    }
    & docker network rm fo-rehearsal-net 2>&1 | Out-Null
}

function Start-Infrastructure {
    Remove-Containers
    if ($C.Storage -eq 'sqlserver') {
        $netArgs = @()
        if ($C.Runtime -eq 'servicebus') {
            # One SQL Server serves both the emulator and the FlowOrchestrator database.
            Invoke-Native docker @('network', 'create', 'fo-rehearsal-net') | Out-Null
            $netArgs = @('--network', 'fo-rehearsal-net')
        }
        Invoke-Native docker (@('run', '-d', '--name', 'fo-rehearsal-mssql') + $netArgs + @(
            '-e', 'ACCEPT_EULA=Y', '-e', "MSSQL_SA_PASSWORD=$SaPassword", '-e', 'MSSQL_MEMORY_LIMIT_MB=2048',
            '-p', '127.0.0.1::1433', 'mcr.microsoft.com/mssql/server:2022-latest')) | Out-Null
        $C.DbContainer = 'fo-rehearsal-mssql'
        $port = ((Invoke-Native docker @('port', 'fo-rehearsal-mssql', '1433')) | Select-Object -First 1).Split(':')[-1]
        $ready = Wait-Until -TimeoutSec 120 -IntervalMs 2000 {
            & docker exec fo-rehearsal-mssql /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P $SaPassword -Q 'SELECT 1' 2>&1 | Out-Null
            $LASTEXITCODE -eq 0
        }
        if (-not $ready) { throw 'SQL Server did not start.' }
        Invoke-Native docker @('exec', 'fo-rehearsal-mssql', '/opt/mssql-tools18/bin/sqlcmd', '-C', '-S', 'localhost', '-U', 'sa', '-P', $SaPassword, '-Q', 'CREATE DATABASE OrderHub') | Out-Null
        $C.ConnStr = "Server=127.0.0.1,$port;Database=OrderHub;User Id=sa;Password=$SaPassword;TrustServerCertificate=true"
    }
    else {
        Invoke-Native docker @('run', '-d', '--name', 'fo-rehearsal-pg', '-e', 'POSTGRES_PASSWORD=postgres', '-e', 'POSTGRES_DB=orderhub',
            '-p', '127.0.0.1::5432', 'postgres:17-alpine') | Out-Null
        $C.DbContainer = 'fo-rehearsal-pg'
        $port = ((Invoke-Native docker @('port', 'fo-rehearsal-pg', '5432')) | Select-Object -First 1).Split(':')[-1]
        $ready = Wait-Until -TimeoutSec 60 { & docker exec fo-rehearsal-pg pg_isready -U postgres -d orderhub 2>&1 | Out-Null; $LASTEXITCODE -eq 0 }
        if (-not $ready) { throw 'PostgreSQL did not start.' }
        Start-Sleep -Seconds 2
        $C.ConnStr = "Host=127.0.0.1;Port=$port;Database=orderhub;Username=postgres;Password=postgres"
    }

    if ($C.Runtime -eq 'servicebus') {
        $subs = foreach ($id in $Flow.Values | Sort-Object) {
            [ordered]@{
                Name       = "flow-$id"
                Properties = [ordered]@{
                    DeadLetteringOnMessageExpiration = $false; DefaultMessageTimeToLive = 'PT1H'; LockDuration = 'PT1M'
                    MaxDeliveryCount = 10; ForwardDeadLetteredMessagesTo = ''; ForwardTo = ''; RequiresSession = $false
                }
                Rules      = @([ordered]@{ Name = 'flow-id'; Properties = [ordered]@{ FilterType = 'Correlation'; CorrelationFilter = [ordered]@{ Properties = [ordered]@{ FlowId = $id } } } })
            }
        }
        $config = (Get-Content (Join-Path $Here 'servicebus/Config.template.json') -Raw).Replace('"__SUBSCRIPTIONS__"', (ConvertTo-Json @($subs) -Depth 10))
        $configPath = Join-Path $C.Root 'sb-config.json'
        Set-Content $configPath $config
        Invoke-Native docker @('run', '-d', '--name', 'fo-rehearsal-sb', '--network', 'fo-rehearsal-net',
            '-e', 'ACCEPT_EULA=Y', '-e', 'SQL_SERVER=fo-rehearsal-mssql', '-e', "MSSQL_SA_PASSWORD=$SaPassword",
            '-v', "${configPath}:/ServiceBus_Emulator/ConfigFiles/Config.json",
            '-p', '127.0.0.1::5672', '-p', '127.0.0.1::5300', 'mcr.microsoft.com/azure-messaging/servicebus-emulator:2.0.1') | Out-Null
        $amqp = ((Invoke-Native docker @('port', 'fo-rehearsal-sb', '5672')) | Select-Object -First 1).Split(':')[-1]
        $health = ((Invoke-Native docker @('port', 'fo-rehearsal-sb', '5300')) | Select-Object -First 1).Split(':')[-1]
        $ready = Wait-Until -TimeoutSec 180 -IntervalMs 2000 { (Invoke-WebRequest "http://127.0.0.1:$health/health" -SkipHttpErrorCheck -TimeoutSec 5).StatusCode -eq 200 }
        if (-not $ready) { throw "Service Bus emulator did not become healthy:`n$((& docker logs fo-rehearsal-sb 2>&1 | Select-Object -Last 30) -join "`n")" }
        $C.SbConn = "Endpoint=sb://127.0.0.1:$amqp;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;"
    }
}

# ── Database access ───────────────────────────────────────────────────────────────────────────────
function Invoke-Db([string] $Sql, [switch] $AllowFailure) {
    if ($C.Storage -eq 'sqlserver') {
        $out = & docker exec $C.DbContainer /opt/mssql-tools18/bin/sqlcmd -C -b -S localhost -U sa -P $SaPassword -d OrderHub -h -1 -W -s '|' -Q "SET NOCOUNT ON; $Sql" 2>&1
    }
    else {
        $out = & docker exec $C.DbContainer psql -U postgres -d orderhub -v ON_ERROR_STOP=1 -At -F '|' -c $Sql 2>&1
    }
    if ($LASTEXITCODE -ne 0 -and -not $AllowFailure) { throw "SQL failed: $Sql`n$($out -join "`n")" }
    return @($out | ForEach-Object { "$_" } | Where-Object { $_ -ne '' })
}

function Get-SchemaSnapshot {
    if ($C.Storage -eq 'sqlserver') {
        $sql = @"
SELECT CAST('COL ' + s.name + '.' + t.name + '.' + c.name + ' ' + ty.name + '(' + CAST(c.max_length AS varchar(10)) + ') null=' + CAST(c.is_nullable AS varchar(1)) + ' computed=' + CAST(c.is_computed AS varchar(1)) AS nvarchar(400)) COLLATE DATABASE_DEFAULT AS x
FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id JOIN sys.columns c ON c.object_id = t.object_id JOIN sys.types ty ON ty.user_type_id = c.user_type_id
UNION ALL
SELECT CAST('IDX ' + s.name + '.' + t.name + '.' + i.name + ' ' + (i.type_desc COLLATE DATABASE_DEFAULT) + ISNULL(' WHERE ' + (i.filter_definition COLLATE DATABASE_DEFAULT), '') AS nvarchar(400)) COLLATE DATABASE_DEFAULT
FROM sys.indexes i JOIN sys.tables t ON t.object_id = i.object_id JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE i.name IS NOT NULL
ORDER BY 1
"@
    }
    else {
        $sql = @"
SELECT 'COL ' || table_schema || '.' || table_name || '.' || column_name || ' ' || data_type || ' null=' || is_nullable FROM information_schema.columns WHERE table_schema NOT IN ('pg_catalog', 'information_schema')
UNION ALL
SELECT 'IDX ' || schemaname || '.' || tablename || '.' || indexname || ' ' || indexdef FROM pg_indexes WHERE schemaname <> 'pg_catalog'
UNION ALL
SELECT 'EXT ' || extname FROM pg_extension
ORDER BY 1
"@
    }
    return Invoke-Db ($sql -replace "`r?`n", ' ')
}

function Get-RowCounts {
    if ($C.Storage -eq 'sqlserver') {
        $sql = "DECLARE @s nvarchar(max) = N''; SELECT @s = @s + N'SELECT ''' + s.name + N'.' + t.name + N''' AS t, COUNT_BIG(*) AS c FROM ' + QUOTENAME(s.name) + N'.' + QUOTENAME(t.name) + N' UNION ALL ' FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id; SET @s = LEFT(@s, LEN(@s) - 10) + N' ORDER BY t'; EXEC (@s);"
    }
    else {
        $sql = "SELECT table_schema || '.' || table_name, (xpath('/row/c/text()', query_to_xml(format('SELECT count(*) AS c FROM %I.%I', table_schema, table_name), false, true, '')))[1]::text FROM information_schema.tables WHERE table_schema NOT IN ('pg_catalog', 'information_schema') AND table_type = 'BASE TABLE' ORDER BY 1"
    }
    $map = [ordered]@{}
    foreach ($line in Invoke-Db $sql) { $p = $line.Split('|'); $map[$p[0]] = [long]$p[1] }
    return $map
}

function Get-EventsFingerprint {
    if ($C.Storage -eq 'sqlserver') {
        return (Invoke-Db "IF OBJECT_ID(N'FlowEvents') IS NULL SELECT 'absent' ELSE SELECT CAST(COUNT_BIG(*) AS varchar(20)) + ':' + CAST(ISNULL(CHECKSUM_AGG(CHECKSUM([Sequence], [RunId], [Timestamp], [Type], [StepKey], [Message])), 0) AS varchar(20)) FROM [FlowEvents]") -join ''
    }
    return (Invoke-Db "SELECT CASE WHEN to_regclass('flow_events') IS NULL THEN 'absent' ELSE (SELECT count(*)::text || ':' || coalesce(md5(string_agg(e::text, ',' ORDER BY e::text)), '') FROM flow_events e) END") -join ''
}

function Get-HangfireStates {
    if ($C.Runtime -ne 'hangfire') { return @{} }
    $sql = $C.Storage -eq 'sqlserver' ? 'SELECT StateName, COUNT(*) FROM HangFire.Job GROUP BY StateName' : 'SELECT statename, count(*) FROM hangfire.job GROUP BY statename'
    $map = @{}
    foreach ($line in Invoke-Db $sql) { $p = $line.Split('|'); $map[$p[0]] = [int]$p[1] }
    return $map
}

function Get-HangfireFailedJobs([string] $ArgumentsLike = '') {
    if ($C.Runtime -ne 'hangfire') { return @() }
    $filter = ''
    if ($ArgumentsLike) { $filter = $C.Storage -eq 'sqlserver' ? " AND j.Arguments LIKE '%$ArgumentsLike%'" : " AND j.arguments::text LIKE '%$ArgumentsLike%'" }
    # Every Failed state in the history: a failing job is retried (Scheduled) before it ends up Failed.
    $sql = $C.Storage -eq 'sqlserver' `
        ? "SELECT CAST(s.JobId AS varchar(20)) + ' now=' + j.StateName + ' ' + ISNULL(LEFT(CAST(s.Data AS nvarchar(max)), 500), '') FROM HangFire.State s JOIN HangFire.Job j ON j.Id = s.JobId WHERE s.Name = 'Failed'$filter ORDER BY s.Id" `
        : "SELECT s.jobid::text || ' now=' || j.statename || ' ' || coalesce(left(s.data::text, 500), '') FROM hangfire.state s JOIN hangfire.job j ON j.id = s.jobid WHERE s.name = 'Failed'$filter ORDER BY s.id"
    return Invoke-Db $sql
}

# ── App process ───────────────────────────────────────────────────────────────────────────────────
function Start-OrderHub([string] $Version, [bool] $Workers, [string] $Label) {
    $out = $Version -eq 'v1' ? $C.V1.Out : $C.V2.Out
    $envs = @{
        ASPNETCORE_URLS                     = $C.BaseUrl
        ASPNETCORE_ENVIRONMENT              = 'Production'
        DOTNET_ENVIRONMENT                  = 'Production'
        ConnectionStrings__FlowOrchestrator = $C.ConnStr
        Rehearsal__StateDir                 = $C.StateDir
        Rehearsal__Workers                  = $Workers.ToString().ToLowerInvariant()
        Rehearsal__ShutdownEndpoint         = 'true'
    }
    if ($C.SbConn) { $envs['ConnectionStrings__ServiceBus'] = $C.SbConn }
    $log = Join-Path $C.Root "$Label.log"
    $C.Log = $log
    $C.Proc = Start-Process dotnet -ArgumentList "`"$(Join-Path $out 'OrderHub.dll')`"" -WorkingDirectory $out -Environment $envs `
        -RedirectStandardOutput $log -RedirectStandardError "$log.err" -PassThru -NoNewWindow
    $healthy = Wait-Until -TimeoutSec 120 {
        if ($C.Proc.HasExited) { throw 'exited' }
        (Invoke-WebRequest "$($C.BaseUrl)/health" -SkipHttpErrorCheck -TimeoutSec 5).StatusCode -eq 200
    }
    return [pscustomobject]@{ Healthy = $healthy -and -not $C.Proc.HasExited; Log = $log }
}

function Stop-OrderHub {
    if (-not $C.Proc -or $C.Proc.HasExited) { return }
    try { Invoke-WebRequest "$($C.BaseUrl)/rehearsal/shutdown" -Method Post -SkipHttpErrorCheck -TimeoutSec 10 | Out-Null } catch { }
    if (-not $C.Proc.WaitForExit(60000)) { Stop-Process -Id $C.Proc.Id -Force }
    $C.Proc = $null
}

function Get-LogErrors([string] $Log) {
    if (-not (Test-Path $Log)) { return @() }
    $lines = Get-Content $Log
    $hits = for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^\S+ (fail|crit): ') { "$($lines[$i].Trim()) | $(($i + 1 -lt $lines.Count) ? $lines[$i + 1].Trim() : '')" }
    }
    return @($hits) + @(Get-Content "$Log.err" -ErrorAction SilentlyContinue | Where-Object { $_ })
}

# ── HTTP ──────────────────────────────────────────────────────────────────────────────────────────
function Invoke-Fo([string] $Method, [string] $Path, $Body = $null, [hashtable] $Headers = @{}, [switch] $Anonymous) {
    $h = @{} + $Headers
    if (-not $Anonymous) { $h['Authorization'] = $BasicAuth }
    $params = @{ Uri = "$($C.BaseUrl)$Path"; Method = $Method; Headers = $h; SkipHttpErrorCheck = $true; TimeoutSec = 30 }
    if ($null -ne $Body) {
        $params.Body = [Text.Encoding]::UTF8.GetBytes(($Body -is [string]) ? $Body : ($Body | ConvertTo-Json -Depth 10 -Compress))
        $params.ContentType = 'application/json'
    }
    $r = Invoke-WebRequest @params
    $text = $r.Content -is [byte[]] ? [Text.Encoding]::UTF8.GetString($r.Content) : "$($r.Content)"
    $json = $null
    try { if ($text) { $json = $text | ConvertFrom-Json -Depth 64 } } catch { }
    return [pscustomobject]@{ Status = [int]$r.StatusCode; Json = $json; Text = $text }
}

function Start-Run([string] $FlowId, $Body, [string] $IdempotencyKey) {
    $headers = $IdempotencyKey ? @{ 'Idempotency-Key' = $IdempotencyKey } : @{}
    $r = Invoke-Fo POST "/flows/api/flows/$FlowId/trigger" $Body $headers
    if ($r.Status -ne 200) { throw "trigger $FlowId -> $($r.Status) $($r.Text)" }
    return $r.Json
}

function Send-OrderWebhook([string] $Delivery, $Order, [switch] $BadSignature, [long] $Timestamp = 0) {
    $raw = $Order | ConvertTo-Json -Depth 10 -Compress
    $bytes = [Text.Encoding]::UTF8.GetBytes($raw)
    $hmac = [System.Security.Cryptography.HMACSHA256]::new([Text.Encoding]::UTF8.GetBytes($HmacKey))
    $sig = 'sha256=' + [Convert]::ToHexString($hmac.ComputeHash($bytes)).ToLowerInvariant()
    if ($BadSignature) { $sig = 'sha256=' + ('0' * 64) }
    $ts = $Timestamp -gt 0 ? $Timestamp : [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    $headers = @{ 'X-Hub-Signature-256' = $sig; 'X-GitHub-Delivery' = $Delivery; 'X-Webhook-Timestamp' = "$ts"; 'X-GitHub-Event' = 'order' }
    return Invoke-Fo POST '/flows/api/webhook/orders' $raw $headers -Anonymous
}

function Get-Run([string] $RunId) { return (Invoke-Fo GET "/flows/api/runs/$RunId").Json }

function Get-StepStatus($Run, [string] $Key) {
    return ($Run.steps | Where-Object { $_.stepKey -eq $Key } | Select-Object -First 1).status
}

function Wait-RunStatus([string] $RunId, [string[]] $Statuses, [int] $TimeoutSec = 90) {
    $script:LastRun = $null
    $ok = Wait-Until -TimeoutSec $TimeoutSec { $script:LastRun = Get-Run $RunId; $script:LastRun.status -in $Statuses }
    return $ok
}

function Wait-StepStatus([string] $RunId, [string] $Key, [string[]] $Statuses, [int] $TimeoutSec = 90) {
    $script:LastRun = $null
    return Wait-Until -TimeoutSec $TimeoutSec { $script:LastRun = Get-Run $RunId; (Get-StepStatus $script:LastRun $Key) -in $Statuses }
}

function Get-Runs([string] $Query) {
    $j = (Invoke-Fo GET "/flows/api/runs?$Query").Json
    if ($null -ne $j -and $j.PSObject.Properties['items']) { return @($j.items) }
    return @($j)
}

function Get-RunCount([string] $FlowId) { return @(Get-Runs "flowId=$FlowId&take=200").Count }

function Get-PollLines([string] $ShipmentId) {
    $f = Join-Path $C.StateDir "polls-$ShipmentId.log"
    return (Test-Path $f) ? @(Get-Content $f) : @()
}

function Set-Delivered([string] $ShipmentId) { Set-Content (Join-Path $C.StateDir "shipment-$ShipmentId.delivered") 'delivered' }

function Short([object] $Value) { $s = "$Value"; return $s.Length -gt 300 ? $s.Substring(0, 300) + '…' : $s }

# ── Phases ────────────────────────────────────────────────────────────────────────────────────────
function Invoke-Build {
    Write-Phase "[$($C.Name)] Build: $FromVersion (net8.0) and the upgraded app ($ToVersion)"
    $C.V1 = Build-CustomerApp 'v1'
    Check "build customer app on $FromVersion / net8.0" $C.V1.Ok (($C.V1.Errors + $C.V1.Warnings) -join '; ')
    $C.V2 = Build-CustomerApp 'v2'
    Check 'guide Steps 1-3 + 6 (upgrade-to-2.0.patch): app builds on 2.0 / net10.0' $C.V2.Ok (($C.V2.Errors + $C.V2.Warnings) -join '; ')
    if (-not ($C.V1.Ok -and $C.V2.Ok)) { throw 'build failed' }

    # Step 2 "Verify": a non-Hangfire host no longer carries Hangfire / Newtonsoft.Json.
    $list = Invoke-Native dotnet @('list', (Join-Path $C.V2.Dir 'CustomerApp.csproj'), 'package', '--include-transitive')
    $hits = @($list | Where-Object { "$_" -match '(?i)hangfire|newtonsoft' } | ForEach-Object { "$_".Trim() })
    if ($C.Runtime -eq 'hangfire') {
        Check 'Step 2 verify: Hangfire host still references Hangfire' ($hits.Count -gt 0) ($hits -join '; ')
    }
    else {
        Check 'Step 2 verify: no Hangfire / Newtonsoft.Json in the graph' ($hits.Count -eq 0) ($hits -join '; ')
    }
}

function Invoke-LiveStateOn1x {
    Write-Phase "[$($C.Name)] ${FromVersion}: create live state"
    $s = Start-OrderHub 'v1' $true 'v1-workers'
    Check "$FromVersion starts on a fresh database" $s.Healthy (Short ((Get-LogErrors $s.Log) -join ' / '))
    if (-not $s.Healthy) { throw "$FromVersion did not start" }
    $st = $C.State

    # Webhooks: two signed deliveries (one per When branch), a replay and a forged signature.
    $w1 = Send-OrderWebhook 'delivery-1' @{ orderId = 'ORD-1'; total = 1500; items = @('A', 'B', 'C') }
    $w2 = Send-OrderWebhook 'delivery-2' @{ orderId = 'ORD-2'; total = 200; items = @('D') }
    $st.Order1 = $w1.Json.runId; $st.Order2 = $w2.Json.runId
    Check 'signed webhook deliveries accepted' ($w1.Status -eq 200 -and $w2.Status -eq 200) "$($w1.Status)/$($w2.Status)"
    $ok1 = Wait-RunStatus $st.Order1 @('Succeeded') ; $r1 = $LastRun
    $ok2 = Wait-RunStatus $st.Order2 @('Succeeded') ; $r2 = $LastRun
    Check 'order runs: ForEach + When branches' ($ok1 -and $ok2 -and (Get-StepStatus $r1 'route_high_value') -eq 'Succeeded' -and (Get-StepStatus $r1 'route_standard') -eq 'Skipped' -and (Get-StepStatus $r2 'route_standard') -eq 'Succeeded') "ORD-1 $($r1.status), ORD-2 $($r2.status)"
    $replay = Send-OrderWebhook 'delivery-1' @{ orderId = 'ORD-1'; total = 1500; items = @('A', 'B', 'C') }
    Check 'replayed delivery rejected (409)' ($replay.Status -eq 409) "$($replay.Status) $(Short $replay.Text)"
    $forged = Send-OrderWebhook 'delivery-forged' @{ orderId = 'ORD-X'; total = 1; items = @() } -BadSignature
    Check 'forged signature rejected (401)' ($forged.Status -eq 401) "$($forged.Status)"

    # Idempotency key and a failed run.
    $p1 = Start-Run $Flow.Payment @{ amount = 50 } 'pay-001'
    $p1b = Start-Run $Flow.Payment @{ amount = 50 } 'pay-001'
    $st.Pay1 = $p1.runId
    Check 'idempotency key deduplicates' ($p1b.duplicate -eq $true -and $p1b.runId -eq $p1.runId) "duplicate=$($p1b.duplicate)"
    Check 'payment run succeeded' (Wait-RunStatus $st.Pay1 @('Succeeded')) $LastRun.status
    $st.PayFailed = (Start-Run $Flow.Payment @{ amount = -1 }).runId
    Check 'declined payment -> Failed run' (Wait-RunStatus $st.PayFailed @('Failed')) $LastRun.status

    # Approvals: A parked (signalled after the upgrade), C cancelled.
    $st.ApprovalA = (Start-Run $Flow.Approval @{ requestId = 'REQ-A'; timeoutSeconds = 3600 }).runId
    Check 'approval A parked in WaitForSignal' (Wait-StepStatus $st.ApprovalA 'wait_for_approval' @('Pending', 'Waiting')) (Get-StepStatus $LastRun 'wait_for_approval')
    $st.ApprovalC = (Start-Run $Flow.Approval @{ requestId = 'REQ-C'; timeoutSeconds = 3600 }).runId
    Wait-StepStatus $st.ApprovalC 'wait_for_approval' @('Pending', 'Waiting') | Out-Null
    $cancel = Invoke-Fo POST "/flows/api/runs/$($st.ApprovalC)/cancel" @{ reason = 'customer withdrew the request' }
    Check 'approval C cancelled' ($cancel.Status -eq 200 -and (Wait-RunStatus $st.ApprovalC @('Cancelled'))) "$($cancel.Status) $($LastRun.status)"

    # Cron: override NightlyReport to every minute and pause LegacyCleanup, from the dashboard API.
    # Not across a minute boundary: a tick already firing when the pause lands is not a paused-schedule bug.
    $null = Wait-Until -TimeoutSec 30 { $sec = [DateTime]::UtcNow.Second; $sec -ge 5 -and $sec -le 45 }
    $o = Invoke-Fo PUT "/flows/api/schedules/$NightlyJob/cron" @{ cronExpression = '* * * * *' }
    $pz = Invoke-Fo POST "/flows/api/schedules/$CleanupJob/pause"
    Check 'cron override + pause accepted' ($o.Status -eq 200 -and $pz.Status -eq 200) "$($o.Status)/$($pz.Status)"
    $st.CleanupRunsAtPause = Get-RunCount $Flow.Cleanup
    Check 'overridden cron fires on 1.34 (baseline)' (Wait-Until -TimeoutSec 100 -IntervalMs 3000 { (Get-RunCount $Flow.Nightly) -ge 1 }) "nightly runs=$(Get-RunCount $Flow.Nightly)"

    # Polling, started last so the first re-check (30 s) is still Scheduled when 1.34 stops: S1 keeps
    # polling across the upgrade; the batch stops mid-way (item 0 done, item 1 polling).
    $st.Ship1 = (Start-Run $Flow.Shipment @{ shipmentId = 'S1' }).runId
    Set-Delivered 'B1'
    $st.Batch = (Start-Run $Flow.Batch @{ shipments = @('B1', 'B2', 'B3') }).runId
    Check 'shipment S1 poll is Pending (re-check scheduled)' (Wait-StepStatus $st.Ship1 'track' @('Pending') 60) "$((Get-PollLines 'S1').Count) polls"
    $mid = Wait-StepStatus $st.Batch 'track_all.1.track' @('Pending') 60
    $br = $LastRun
    Check 'ForEach stopped mid-way (item 0 done, item 1 polling)' ($mid -and (Get-StepStatus $br 'track_all.0.track') -eq 'Succeeded') "track_all.0.track=$(Get-StepStatus $br 'track_all.0.track'), track_all.1.track=$(Get-StepStatus $br 'track_all.1.track')"

    # Approval B: short timeout so its deadline passes after the upgrade.
    $st.ApprovalB = (Start-Run $Flow.Approval @{ requestId = 'REQ-B'; timeoutSeconds = 150 }).runId
    $st.ApprovalBDeadline = [DateTime]::UtcNow.AddSeconds(150)
    Check 'approval B parked (150 s timeout)' (Wait-StepStatus $st.ApprovalB 'wait_for_approval' @('Pending', 'Waiting')) ''

    $st.Flows = (Invoke-Fo GET '/flows/api/flows').Json | ForEach-Object { "$($_.id)=$($_.isEnabled)" } | Sort-Object
    $st.Schedules = (Invoke-Fo GET '/flows/api/schedules').Json
    $st.CleanupRunsAtStop = Get-RunCount $Flow.Cleanup
    Check 'paused schedule did not fire on 1.34' ($st.CleanupRunsAtStop -eq $st.CleanupRunsAtPause) "$($st.CleanupRunsAtPause) -> $($st.CleanupRunsAtStop)"
    $errs = Get-LogErrors $s.Log
    Note "$FromVersion log errors (expected: declined card only)" (Short ($errs -join ' / '))
    Stop-OrderHub

    # A web-only node: accepted work is queued (Hangfire Enqueued / Service Bus messages) but not run.
    Write-Phase "[$($C.Name)] $FromVersion web-only node: queue work that is not executed"
    $s = Start-OrderHub 'v1' $false 'v1-webonly'
    Check "$FromVersion web-only node starts" $s.Healthy ''
    $w3 = Send-OrderWebhook 'delivery-3' @{ orderId = 'ORD-3'; total = 300; items = @('E', 'F') }
    $st.Order3 = $w3.Json.runId
    $st.ApprovalD = (Start-Run $Flow.Approval @{ requestId = 'REQ-D'; timeoutSeconds = 3600 }).runId
    $st.Pay2 = (Start-Run $Flow.Payment @{ amount = 20 } 'pay-002').runId
    Start-Sleep -Seconds 2
    $r3 = Get-Run $st.Order3
    Check 'queued runs created, entry steps not executed' ($w3.Status -eq 200 -and @($r3.steps | Where-Object { $_.status -eq 'Succeeded' }).Count -eq 0) "ORD-3 $($r3.status), steps=$(@($r3.steps).Count)"
    Stop-OrderHub

    $hf = Get-HangfireStates
    if ($C.Runtime -eq 'hangfire') {
        Check '1.34 Hangfire jobs left Enqueued + Scheduled' (($hf['Enqueued'] -ge 3) -and ($hf['Scheduled'] -ge 3)) (($hf.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ', ')
    }
}

function Invoke-GuidePreflight {
    Write-Phase "[$($C.Name)] Guide Step 0 / 4.1 / 8 pre-flight on the stopped 1.34 deployment"
    # Step 0: the inventory search, run over the customer app the way the guide prints it.
    $pattern = 'IFlowRepository|FlowRepositoryExtensions|IFlowExecutor|FlowExecutor|new FlowOrchestratorEngine\(|IFlowRunStore|IFlowRunRuntimeStore|IFlowRunControlStore|IOutputsRepository|IStepInstance|IRecurringTriggerSync|SyncTriggers\(|FlowOrchestratorOptions|AddFlowOrchestratorSqlServer|AddFlowOrchestratorPostgreSql|ScopeMoveNext|EndScopeAsync|AllKnownStepKeys|FindParentStep|FindNextStep|ShouldExecute|HangfireEnabled|FlowSignalDispatcher\(|EnableEventPersistence|IFlowEventReader|RecordEventAsync|GetRunEventsAsync|FlowEvent|FlowEvents|flow_events'
    $hits = @(Select-String -Path (Join-Path $C.V1.Dir '*.cs') -Pattern $pattern | ForEach-Object { "$($_.Filename):$($_.LineNumber)" })
    Note 'Step 0 inventory hits' ($hits -join ', ')

    if ($C.Runtime -eq 'hangfire') {
        # Step 4.1, verbatim (as the guide prints it for each storage).
        $q = $C.Storage -eq 'sqlserver' `
            ? "SELECT j.Id, j.StateName, j.CreatedAt FROM HangFire.Job AS j WHERE j.StateName IN ('Enqueued', 'Scheduled', 'Processing', 'Awaiting') AND j.InvocationData LIKE '%IFlowDefinition%';" `
            : "SELECT j.id, j.statename, j.createdat FROM hangfire.job AS j WHERE j.statename IN ('Enqueued', 'Scheduled', 'Processing', 'Awaiting') AND j.invocationdata::text LIKE '%IFlowDefinition%';"
        $rows = Invoke-Db $q -AllowFailure
        Check 'Step 4.1 query runs as written and finds no <= 1.16 job' ($LASTEXITCODE -eq 0 -and $rows.Count -eq 0) (Short ($rows -join ' / '))
    }

    $C.PreSchema = Get-SchemaSnapshot
    $C.PreCounts = Get-RowCounts
    $C.PreEvents = Get-EventsFingerprint
    $C.PreRunIds = Invoke-Db ($C.Storage -eq 'sqlserver' ? 'SELECT LOWER(CAST(Id AS char(36))) FROM FlowRuns ORDER BY 1' : 'SELECT id::text FROM flow_runs ORDER BY 1')
    Note 'run-events table before upgrade' $C.PreEvents
}

function Invoke-UpgradeTo2 {
    Write-Phase "[$($C.Name)] 2.0: migrator on the 1.34 database (web-only start, nothing consumed)"
    $s = Start-OrderHub 'v2' $false 'v2-migrate'
    Check '2.0 starts on the 1.34 database' $s.Healthy (Short ((Get-LogErrors $s.Log) -join ' / '))
    if (-not $s.Healthy) { throw '2.0 did not start' }
    # Background index builds (SQL Server online / PostgreSQL CONCURRENTLY) finish within seconds here.
    $null = Wait-Until -TimeoutSec 60 { (Select-String -Path $s.Log -Pattern '\[(7205|7212|7206|7213|7207)\]' -Quiet) }
    $migrationLines = @(Select-String -Path $s.Log -Pattern '\[(72\d\d)\]' -Context 0, 1 | ForEach-Object { "$($_.Line.Trim()) $($_.Context.PostContext -join ' ')".Trim() })
    Note 'migrator log events' (Short ($migrationLines -join ' / '))
    Check 'migrator completed without errors' (-not (Select-String -Path $s.Log -Pattern '\[7203\]|\[7206\]|\[7213\]' -Quiet)) ''
    Stop-OrderHub

    $post = Get-SchemaSnapshot
    $added = @($post | Where-Object { $_ -notin $C.PreSchema })
    $removed = @($C.PreSchema | Where-Object { $_ -notin $post })
    $C.SchemaAdded = $added
    Note 'schema objects added by the 2.0 migrator' ($added -join ' ; ')
    Check 'migrator removed / changed no existing column or index' ($removed.Count -eq 0) ($removed -join ' ; ')
    Check 'run-events table untouched' ((Get-EventsFingerprint) -eq $C.PreEvents) "$($C.PreEvents) -> $(Get-EventsFingerprint)"
    $postCounts = Get-RowCounts
    $lost = @($C.PreCounts.Keys | Where-Object { -not $postCounts.Contains($_) -or $postCounts[$_] -lt $C.PreCounts[$_] } | ForEach-Object { "$_ $($C.PreCounts[$_]) -> $($postCounts[$_])" })
    Check 'no table lost rows' ($lost.Count -eq 0) ($lost -join '; ')
    $runIds = Invoke-Db ($C.Storage -eq 'sqlserver' ? 'SELECT LOWER(CAST(Id AS char(36))) FROM FlowRuns ORDER BY 1' : 'SELECT id::text FROM flow_runs ORDER BY 1')
    Check 'every 1.34 run still present' (@($C.PreRunIds | Where-Object { $_ -notin $runIds }).Count -eq 0) "$($C.PreRunIds.Count) runs"
    $grown = @($postCounts.Keys | Where-Object { $C.PreCounts.Contains($_) -and $postCounts[$_] -ne $C.PreCounts[$_] } | ForEach-Object { "$_ $($C.PreCounts[$_]) -> $($postCounts[$_])" })
    Note 'row counts changed by the 2.0 web-only start' ($grown -join '; ')

    Write-Phase "[$($C.Name)] 2.0: workers on — verify queued 1.34 work, parked runs, polls, cron, webhooks"
    $s = Start-OrderHub 'v2' $true 'v2-workers'
    Check '2.0 starts with workers' $s.Healthy ''
    $st = $C.State
    $upgradeAt = [DateTime]::UtcNow

    # Work queued by the 1.34 web-only node.
    Check 'queued 1.34 webhook run completes on 2.0' (Wait-RunStatus $st.Order3 @('Succeeded') 120) "$($LastRun.status) $((@($LastRun.steps) | ForEach-Object { "$($_.stepKey)=$($_.status)" }) -join ',')"
    Check 'queued 1.34 payment run completes on 2.0' (Wait-RunStatus $st.Pay2 @('Succeeded') 120) $LastRun.status
    Check 'queued 1.34 approval run parks on 2.0' (Wait-StepStatus $st.ApprovalD 'wait_for_approval' @('Pending', 'Waiting') 120) (Get-StepStatus $LastRun 'wait_for_approval')

    # Parked approval A resumes when signalled.
    $sig = Invoke-Fo POST "/flows/api/runs/$($st.ApprovalA)/signals/approval" @{ approver = 'alice'; approved = $true }
    Check 'run parked on 1.34 resumes when signalled on 2.0' ($sig.Status -eq 200 -and (Wait-RunStatus $st.ApprovalA @('Succeeded') 90) -and (Get-StepStatus $LastRun 'finalize') -eq 'Succeeded') "$($sig.Status) $(Short $sig.Text) run=$($LastRun.status)"

    # Polls continue on 2.0, then complete.
    $before = (Get-PollLines 'S1').Count
    $repolled = Wait-Until -TimeoutSec 60 { @(Get-PollLines 'S1' | Where-Object { $_ -match "fo=2\." }).Count -ge 1 }
    Check 'Pending poll from 1.34 keeps polling on 2.0' $repolled "$before -> $((Get-PollLines 'S1').Count) polls; last: $((Get-PollLines 'S1') | Select-Object -Last 1)"
    $attempts = @(Get-PollLines 'S1' | ForEach-Object { if ($_ -match 'attempt=(\d+)') { [int]$Matches[1] } })
    Note 'poll attempt counter across the upgrade' "attempts: $($attempts -join ',')"
    Set-Delivered 'S1'
    Check 'shipment S1 completes on 2.0' (Wait-RunStatus $st.Ship1 @('Succeeded') 60) $LastRun.status
    Set-Delivered 'B2'; Set-Delivered 'B3'
    Check 'ForEach stopped mid-way on 1.34 finishes on 2.0' (Wait-RunStatus $st.Batch @('Succeeded') 120) "$($LastRun.status) $((@($LastRun.steps) | ForEach-Object { "$($_.stepKey)=$($_.status)" }) -join ',')"

    # Cron: the override is kept and fires; the paused schedule stays paused.
    $sched = (Invoke-Fo GET '/flows/api/schedules').Json
    $n = $sched | Where-Object { $_.jobId -eq $NightlyJob }
    $p = $sched | Where-Object { $_.jobId -eq $CleanupJob }
    Check 'dashboard: cron override kept, paused schedule still paused' ($n.cron -eq '* * * * *' -and -not $n.paused -and $p.paused) "nightly cron='$($n.cron)' paused=$($n.paused); cleanup paused=$($p.paused)"
    $nightlyBefore = Get-RunCount $Flow.Nightly
    Check 'overridden cron fires on 2.0' (Wait-Until -TimeoutSec 100 -IntervalMs 3000 { (Get-RunCount $Flow.Nightly) -gt $nightlyBefore }) "nightly runs $nightlyBefore -> $(Get-RunCount $Flow.Nightly)"
    $perMinute = @(Get-Runs "flowId=$($Flow.Nightly)&take=200" | Where-Object { [DateTime]$_.startedAt -gt $upgradeAt } | Group-Object { ([DateTime]$_.startedAt).ToString('HH:mm') } | Where-Object Count -gt 1)
    Check 'cron fires once per tick, not twice' ($perMinute.Count -eq 0) (($perMinute | ForEach-Object { "$($_.Name)x$($_.Count)" }) -join ',')
    Check 'paused schedule does not fire on 2.0' ((Get-RunCount $Flow.Cleanup) -eq $st.CleanupRunsAtStop) "$($st.CleanupRunsAtStop) -> $(Get-RunCount $Flow.Cleanup)"

    # Approval B: the deadline set on 1.34 fires on 2.0.
    $wait = [Math]::Max(0, ($st.ApprovalBDeadline - [DateTime]::UtcNow).TotalSeconds)
    Note 'approval B deadline' ("{0:N0} s after the 2.0 start" -f ($st.ApprovalBDeadline - $upgradeAt).TotalSeconds)
    $timedOut = Wait-StepStatus $st.ApprovalB 'wait_for_approval' @('Failed') ([int]$wait + 90)
    Check 'WaitForSignal timeout set on 1.34 fires on 2.0' ($timedOut -and (Wait-StepStatus $st.ApprovalB 'escalate' @('Succeeded') 60)) "wait=$(Get-StepStatus $LastRun 'wait_for_approval') escalate=$(Get-StepStatus $LastRun 'escalate') run=$($LastRun.status)"
    $late = Invoke-Fo POST "/flows/api/runs/$($st.ApprovalB)/signals/approval" @{ approver = 'late' }
    Note 'signal after the deadline' "$($late.Status) $(Short $late.Text)"

    # Webhooks keep verifying; 1.34 replay nonces still count; idempotency keys still deduplicate.
    $w4 = Send-OrderWebhook 'delivery-4' @{ orderId = 'ORD-4'; total = 2500; items = @('G') }
    Check 'new signed delivery accepted on 2.0' ($w4.Status -eq 200 -and (Wait-RunStatus $w4.Json.runId @('Succeeded'))) "$($w4.Status) $($LastRun.status)"
    $rp1 = Send-OrderWebhook 'delivery-1' @{ orderId = 'ORD-1'; total = 1500; items = @('A', 'B', 'C') }
    Check 'replay of a nonce stored by 1.34 rejected on 2.0 (409)' ($rp1.Status -eq 409) "$($rp1.Status) $(Short $rp1.Text)"
    $rp4 = Send-OrderWebhook 'delivery-4' @{ orderId = 'ORD-4'; total = 2500; items = @('G') }
    Check 'replay of a 2.0 nonce rejected (409)' ($rp4.Status -eq 409) "$($rp4.Status)"
    $forged = Send-OrderWebhook 'delivery-forged-2' @{ orderId = 'ORD-Y'; total = 1; items = @() } -BadSignature
    Check 'forged signature rejected on 2.0 (401)' ($forged.Status -eq 401) "$($forged.Status)"
    $dup = Start-Run $Flow.Payment @{ amount = 50 } 'pay-001'
    Check 'idempotency key stored by 1.34 deduplicates on 2.0' ($dup.duplicate -eq $true -and $dup.runId -eq $st.Pay1) "duplicate=$($dup.duplicate) runId=$($dup.runId)"

    # Dashboard over the 1.34 history.
    $all = Get-Runs 'take=200'
    Check 'dashboard lists every 1.34 run' (@($C.PreRunIds | Where-Object { $_ -notin @($all | ForEach-Object { "$($_.id)".ToLowerInvariant() }) }).Count -eq 0) "$(@($all).Count) runs listed"
    $prefix = $st.PayFailed.Substring(0, 8)
    $found = Get-Runs "search=$prefix&take=50"
    Check 'run-id prefix search finds a 1.34 run' (@($found | Where-Object { $_.id -eq $st.PayFailed }).Count -eq 1) "search=$prefix -> $(@($found).Count) hits"
    $failed = Get-Runs 'status=failed&take=200'
    Check 'status filter finds the 1.34 failed run' (@($failed | Where-Object { $_.id -eq $st.PayFailed }).Count -eq 1) "$(@($failed).Count) failed runs"
    $ev = Invoke-Fo GET "/flows/api/runs/$($st.Order1)/events"
    Check 'Events drawer shows a timeline for a 1.34 run' ($ev.Status -eq 200 -and @($ev.Json).Count -gt 0) "$($ev.Status), $(@($ev.Json).Count) events"
    $flowsNow = (Invoke-Fo GET '/flows/api/flows').Json | ForEach-Object { "$($_.id)=$($_.isEnabled)" } | Sort-Object
    Check 'Step 9: same flows and enabled flags' ((Compare-Object $st.Flows $flowsNow) -eq $null) (($flowsNow) -join ', ')

    # New runs end to end.
    $e = (Start-Run $Flow.Approval @{ requestId = 'REQ-E'; timeoutSeconds = 3600 }).runId
    Wait-StepStatus $e 'wait_for_approval' @('Pending', 'Waiting') | Out-Null
    $sig = Invoke-Fo POST "/flows/api/runs/$e/signals/approval" @{ approver = 'bob' }
    Check 'new approval run on 2.0 parks and resumes' ($sig.Status -eq 200 -and (Wait-RunStatus $e @('Succeeded'))) $LastRun.status
    $np = (Start-Run $Flow.Payment @{ amount = 75 } 'pay-003').runId
    Check 'new payment run on 2.0' (Wait-RunStatus $np @('Succeeded')) $LastRun.status
    $st.Pay3 = $np

    # Step 9 checklist leftovers.
    Check 'Step 9: /health green' ((Invoke-WebRequest "$($C.BaseUrl)/health" -SkipHttpErrorCheck).StatusCode -eq 200) ''
    if ($C.Runtime -eq 'hangfire') {
        $fj = Get-HangfireFailedJobs
        Check 'Step 9: no failed Hangfire jobs (JobLoadException / method not found)' ($fj.Count -eq 0) (Short ($fj -join ' / '))
    }
    $errs = @(Get-LogErrors $s.Log | Where-Object { $_ -notmatch 'Card declined' })
    Check 'Step 9: no error-level log events on 2.0' ($errs.Count -eq 0) (Short ($errs -join ' / '))
    $dashEvents = @(Select-String -Path $s.Log -Pattern '\[(4103|4104)\]' | ForEach-Object { $_.Line.Trim() })
    Check 'Step 9: no dashboard auth events 4103 / 4104' ($dashEvents.Count -eq 0) ($dashEvents -join ' / ')
    $unsent = @(Select-String -Path (Join-Path $C.Root 'v2-*.log') -Pattern 'unsent|re-deliver|redeliver' | ForEach-Object { $_.Line.Trim() } | Select-Object -First 5)
    Note 'unsent-dispatch recovery activity on 2.0' (Short ($unsent -join ' / '))
}

function Invoke-Rollback {
    Write-Phase "[$($C.Name)] Rollback 2.0 -> $FromVersion (guide: Rollback)"
    $st = $C.State
    # A run parked on 2.0, and 2.0 work queued but not executed.
    $st.ApprovalG = (Start-Run $Flow.Approval @{ requestId = 'REQ-G'; timeoutSeconds = 3600 }).runId
    Check 'approval G parked on 2.0' (Wait-StepStatus $st.ApprovalG 'wait_for_approval' @('Pending', 'Waiting')) ''
    Stop-OrderHub
    $s = Start-OrderHub 'v2' $false 'v2-webonly'
    $st.PayQueued20 = (Start-Run $Flow.Payment @{ amount = 30 } 'pay-004').runId
    Stop-OrderHub
    $hf = Get-HangfireStates
    if ($C.Runtime -eq 'hangfire') { Note '2.0 Hangfire jobs left before the rollback' (($hf.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ', ') }

    $s = Start-OrderHub 'v1' $true 'v1-rollback'
    Check "$FromVersion starts on the database 2.0 migrated" $s.Healthy (Short ((Get-LogErrors $s.Log) -join ' / '))
    if (-not $s.Healthy) { return }
    $q = Wait-RunStatus $st.PayQueued20 @('Succeeded', 'Failed') 60
    if ($C.Runtime -eq 'hangfire') {
        $fj = @(Get-HangfireFailedJobs $st.PayQueued20)
        Note 'a 2.0 job picked up by a 1.34 worker' "run $($LastRun.status); failed Hangfire jobs: $(Short ($fj -join ' / '))"
        Check 'guide claim: a 1.x worker cannot run a 2.0 Hangfire job' ((-not $q) -and $fj.Count -gt 0) "run=$($LastRun.status) failedJobs=$($fj.Count)"
    }
    else {
        # The Service Bus step envelope did not change between 1.34 and 2.0.
        Check 'a step message sent by 2.0 runs on a 1.34 Service Bus worker' ($q -and $LastRun.status -eq 'Succeeded') "run=$($LastRun.status)"
    }
    $sig = Invoke-Fo POST "/flows/api/runs/$($st.ApprovalG)/signals/approval" @{ approver = 'carol' }
    Check 'run parked on 2.0 resumes on 1.34 when signalled' ($sig.Status -eq 200 -and (Wait-RunStatus $st.ApprovalG @('Succeeded') 90)) "$($sig.Status) $($LastRun.status)"
    $sig = Invoke-Fo POST "/flows/api/runs/$($st.ApprovalD)/signals/approval" @{ approver = 'dave' }
    Check 'run queued on 1.34, parked on 2.0, resumes on 1.34' ($sig.Status -eq 200 -and (Wait-RunStatus $st.ApprovalD @('Succeeded') 90)) "$($sig.Status) $($LastRun.status)"
    $ev = Invoke-Fo GET "/flows/api/runs/$($st.Pay3)/events"
    Check 'guide claim: 2.0 runs show an empty Events drawer on 1.34' ($ev.Status -eq 200 -and @($ev.Json).Count -eq 0) "$($ev.Status), $(@($ev.Json).Count) events"
    $all = Get-Runs 'take=200'
    Check '1.34 dashboard lists runs written by 2.0' (@($all | Where-Object { $_.id -eq $st.Pay3 }).Count -eq 1) "$(@($all).Count) runs"
    $np = (Start-Run $Flow.Payment @{ amount = 90 } 'pay-005').runId
    Check 'new run on 1.34 after the rollback' (Wait-RunStatus $np @('Succeeded')) $LastRun.status
    $dup = Start-Run $Flow.Payment @{ amount = 75 } 'pay-003'
    Check 'idempotency key stored by 2.0 deduplicates on 1.34' ($dup.duplicate -eq $true -and $dup.runId -eq $st.Pay3) "duplicate=$($dup.duplicate)"
    $nightlyBefore = Get-RunCount $Flow.Nightly
    $cleanupBefore = Get-RunCount $Flow.Cleanup
    Check 'cron override still fires after the rollback' (Wait-Until -TimeoutSec 100 -IntervalMs 3000 { (Get-RunCount $Flow.Nightly) -gt $nightlyBefore }) ''
    Check 'paused schedule still paused after the rollback' ((Get-RunCount $Flow.Cleanup) -eq $cleanupBefore) "$cleanupBefore -> $(Get-RunCount $Flow.Cleanup)"
    Stop-OrderHub
}

# ── Main ──────────────────────────────────────────────────────────────────────────────────────────
Initialize-WorkDir
Publish-Packages

foreach ($name in $Combination) {
    $parts = $name.Split('-')
    $root = Join-Path $WorkDir $name
    Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $root, (Join-Path $root 'state') | Out-Null
    $C = @{
        Name = $name; Runtime = $parts[0]; Storage = $parts[1]; Root = $root; StateDir = (Join-Path $root 'state')
        BaseUrl = "http://127.0.0.1:$(Get-FreePort)"; State = @{}; Proc = $null; SbConn = $null
    }
    try {
        Invoke-Build
        Write-Phase "[$name] Infrastructure"
        Start-Infrastructure
        Invoke-LiveStateOn1x
        Invoke-GuidePreflight
        Invoke-UpgradeTo2
        if ($name -in $RollbackOn) { Invoke-Rollback }
    }
    catch {
        Check 'rehearsal aborted' $false "$($_.Exception.Message) @ $($_.InvocationInfo.ScriptLineNumber)"
    }
    finally {
        Stop-OrderHub
        if (-not $KeepContainers) { Remove-Containers }
    }
}

# ── Report ────────────────────────────────────────────────────────────────────────────────────────
$report = [System.Text.StringBuilder]::new()
[void]$report.AppendLine("# FlowOrchestrator $FromVersion -> 2.0 upgrade rehearsal")
[void]$report.AppendLine('')
[void]$report.AppendLine("Checkout: $(git -C $RepoRoot rev-parse --short HEAD) · $(Get-Date -AsUTC -Format 'yyyy-MM-dd HH:mm') UTC")
foreach ($g in $Results | Group-Object Combination) {
    [void]$report.AppendLine(''); [void]$report.AppendLine("## $($g.Name)"); [void]$report.AppendLine('')
    [void]$report.AppendLine('| Step | Result | Detail |'); [void]$report.AppendLine('|---|---|---|')
    foreach ($r in $g.Group) { [void]$report.AppendLine("| $($r.Step) | $($r.Result) | $(($r.Detail -replace '\|', '/') -replace "`r?`n", ' ') |") }
}
$reportPath = Join-Path $WorkDir 'report.md'
Set-Content $reportPath $report.ToString()

$checks = @($Results | Where-Object Result -ne 'INFO')
$passed = @($checks | Where-Object Result -eq 'PASS').Count
Write-Host ''
Write-Host "Report: $reportPath"
Write-Host "RESULT: $passed/$($checks.Count) passed" -ForegroundColor ($passed -eq $checks.Count ? 'Green' : 'Red')
exit ($passed -eq $checks.Count ? 0 : 1)
