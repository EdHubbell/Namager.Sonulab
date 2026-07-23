<#
.SYNOPSIS
Summarises NAMager usage telemetry from the namager-usage D1 database.

.DESCRIPTION
Answers the question the telemetry exists to answer: does this app have users, and
do they come back? Prints active counts, a retention figure, engagement spread, and
the version/transport breakdowns.

Every number here counts only installs that SUCCESSFULLY CONNECTED A PEDAL. People who
downloaded the app and never plugged anything in are invisible to it by design - that
top of the funnel is the release-asset download count on GitHub Releases, a separate
number. Dev builds never ping, so your own bench work is excluded.

.PARAMETER IncludeTestRows
Include the validation script's synthetic install ID. Off by default - it would
otherwise inflate every count by one.

.PARAMETER Days
Window for the "active" figures. Default 30.

.PARAMETER Raw
Also dump the full installs table.

.EXAMPLE
pwsh tools/Get-TelemetryReport.ps1

.EXAMPLE
pwsh tools/Get-TelemetryReport.ps1 -Days 7 -Raw
#>
[CmdletBinding()]
param(
    [switch] $IncludeTestRows,
    [int]    $Days = 30,
    [switch] $Raw
)

$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw "PowerShell 7+ required (this is $($PSVersionTable.PSVersion)). Run with 'pwsh'."
}

$DbName   = 'namager-usage'
$TestGuid = '8f3c1e64-0000-4000-8000-000000000001'   # from tools/Validate-Telemetry.ps1

# Applied to every query so validation rows don't masquerade as a user.
$NotTest = if ($IncludeTestRows) { '1=1' } else { "install_id <> '$TestGuid'" }

function Invoke-D1Query {
    <# Runs one SELECT and returns its rows as objects. #>
    param([Parameter(Mandatory)] [string] $Sql)

    # Newlines in the --command argument get truncated before they reach D1 (it fails with
    # "incomplete input"), so flatten to one line. Only line breaks are replaced, never runs
    # of spaces, so string literals like 'now','-30 days' survive intact.
    $Sql = ($Sql -replace '\r?\n', ' ').Trim()

    $raw = npx wrangler d1 execute $DbName --remote --json --command $Sql 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) {
        throw "wrangler failed (exit $LASTEXITCODE). Are you logged in, and is the database created?`n$raw"
    }
    # wrangler can print a banner before the JSON payload, so start at the first bracket
    # rather than assuming the whole stream is JSON.
    $start = $raw.IndexOf('[')
    if ($start -lt 0) { throw "no JSON found in wrangler output:`n$raw" }

    try   { @((($raw.Substring($start) | ConvertFrom-Json))[0].results) }
    catch { throw "could not parse wrangler output as JSON:`n$raw" }
}

function Get-Scalar {
    <# First column of the first row, or 0 when the query returned nothing. #>
    param([Parameter(Mandatory)] [string] $Sql, $Default = 0)
    $rows = Invoke-D1Query $Sql
    if (-not $rows -or $rows.Count -eq 0) { return $Default }
    $v = $rows[0].PSObject.Properties | Select-Object -First 1 | ForEach-Object { $_.Value }
    if ($null -eq $v) { $Default } else { $v }
}

function Write-Bar {
    <# label, count, and a bar scaled to the largest value in the set. #>
    param([string] $Label, [int] $Count, [int] $Max, [int] $Width = 28)
    $bar = if ($Max -gt 0) { '#' * [Math]::Max(1, [int](($Count / $Max) * $Width)) } else { '' }
    "{0,-16} {1,5}  {2}" -f $Label, $Count, $bar
}

Write-Host ""
Write-Host "NAMager usage telemetry" -ForegroundColor Cyan
Write-Host ("  database : {0}" -f $DbName)
Write-Host ("  window   : last {0} days" -f $Days)
if (-not $IncludeTestRows) {
    Write-Host "  note     : validation test row excluded (-IncludeTestRows to keep it)" -ForegroundColor DarkGray
}
Write-Host ""

# ------------------------------------------------------------------ headline
$total     = [int](Get-Scalar "SELECT COUNT(*) FROM installs WHERE $NotTest")
$active    = [int](Get-Scalar "SELECT COUNT(*) FROM installs WHERE $NotTest AND last_seen >= date('now','-$Days days')")
$active7   = [int](Get-Scalar "SELECT COUNT(*) FROM installs WHERE $NotTest AND last_seen >= date('now','-7 days')")
$newIn     = [int](Get-Scalar "SELECT COUNT(*) FROM installs WHERE $NotTest AND first_seen >= date('now','-$Days days')")
$repeat    = [int](Get-Scalar "SELECT COUNT(*) FROM installs WHERE $NotTest AND active_days > 1")
$oneAndDone= $total - $repeat

if ($total -eq 0) {
    Write-Host "No installs recorded yet." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "That is expected until a tagged release ships: dev builds never ping"
    Write-Host "(any version containing '-'), so nothing appears until real users are"
    Write-Host "running a released build and connecting a pedal."
    Write-Host ""
    exit 0
}

Write-Host "Headline" -ForegroundColor Cyan
"  Installs (all time)          {0,5}" -f $total
"  Active in last $Days days       {0,5}" -f $active
"  Active in last 7 days        {0,5}" -f $active7
"  New in last $Days days          {0,5}" -f $newIn
"  Came back (2+ days)          {0,5}   {1}" -f $repeat, $(
    if ($total -gt 0) { "({0:P0} of all installs)" -f ($repeat / $total) } else { '' })
"  Connected once, never again  {0,5}" -f $oneAndDone
Write-Host ""

# ---------------------------------------------------------------- retention
$coh = Invoke-D1Query @"
SELECT COUNT(*) AS cohort,
       SUM(CASE WHEN last_seen >= date('now','-14 days') THEN 1 ELSE 0 END) AS retained
FROM installs
WHERE $NotTest AND first_seen BETWEEN date('now','-60 days') AND date('now','-30 days')
"@
$cohort   = [int]$coh[0].cohort
$retained = [int]$coh[0].retained

Write-Host "Retention" -ForegroundColor Cyan
if ($cohort -eq 0) {
    Write-Host "  Not enough history yet - needs installs first seen 30-60 days ago." -ForegroundColor DarkGray
    Write-Host "  Come back once the app has been out that long." -ForegroundColor DarkGray
}
else {
    "  Of {0} installs first seen 30-60 days ago, {1} were still connecting in the last 14 days." -f $cohort, $retained
    "  Retention: {0:P0}" -f ($retained / $cohort)
}
Write-Host ""

# --------------------------------------------------------------- engagement
$eng = Invoke-D1Query @"
SELECT CASE WHEN active_days = 1 THEN '1 day'
            WHEN active_days BETWEEN 2 AND 3  THEN '2-3 days'
            WHEN active_days BETWEEN 4 AND 7  THEN '4-7 days'
            WHEN active_days BETWEEN 8 AND 30 THEN '8-30 days'
            ELSE '30+ days' END AS bucket,
       COUNT(*) AS n, MIN(active_days) AS sort
FROM installs WHERE $NotTest
GROUP BY bucket ORDER BY sort
"@
if ($eng.Count) {
    Write-Host "Engagement (distinct days each install connected)" -ForegroundColor Cyan
    $max = ($eng | Measure-Object -Property n -Maximum).Maximum
    $eng | ForEach-Object { "  " + (Write-Bar $_.bucket ([int]$_.n) $max) }
    Write-Host ""
}

# ---------------------------------------------------------------- transport
$tr = Invoke-D1Query @"
SELECT transport, COUNT(DISTINCT install_id) AS installs, COUNT(*) AS days
FROM pings WHERE $NotTest AND day >= date('now','-60 days')
GROUP BY transport ORDER BY installs DESC
"@
Write-Host "Transport (last 60 days)" -ForegroundColor Cyan
if ($tr.Count) {
    $max = ($tr | Measure-Object -Property installs -Maximum).Maximum
    $tr | ForEach-Object { "  " + (Write-Bar $_.transport ([int]$_.installs) $max) + "   $($_.days) active days" }
    if (-not ($tr | Where-Object { $_.transport -eq 'wifi' })) {
        Write-Host "  No WiFi use at all - the buggy WiFi transport may not be worth more work." -ForegroundColor DarkGray
    }
}
else { Write-Host "  no pings in the window" -ForegroundColor DarkGray }
Write-Host ""

# ----------------------------------------------------------------- versions
foreach ($spec in @(
    @{ Col = 'app_version'; Title = 'App versions in the wild' },
    @{ Col = 'fw_version';  Title = 'Pedal firmware in the wild' })) {

    $rows = Invoke-D1Query "SELECT $($spec.Col) AS v, COUNT(*) AS n FROM installs WHERE $NotTest GROUP BY v ORDER BY n DESC, v DESC"
    Write-Host $spec.Title -ForegroundColor Cyan
    if ($rows.Count) {
        $max = ($rows | Measure-Object -Property n -Maximum).Maximum
        $rows | ForEach-Object { "  " + (Write-Bar $_.v ([int]$_.n) $max) }
    }
    else { Write-Host "  none" -ForegroundColor DarkGray }
    Write-Host ""
}

# ------------------------------------------------------------ recent by day
$daily = Invoke-D1Query @"
SELECT day, COUNT(DISTINCT install_id) AS installs
FROM pings WHERE $NotTest AND day >= date('now','-14 days')
GROUP BY day ORDER BY day DESC
"@
Write-Host "Daily actives (last 14 days)" -ForegroundColor Cyan
if ($daily.Count) {
    $max = ($daily | Measure-Object -Property installs -Maximum).Maximum
    $daily | ForEach-Object { "  " + (Write-Bar $_.day ([int]$_.installs) $max) }
}
else { Write-Host "  no activity in the last 14 days" -ForegroundColor DarkGray }
Write-Host ""

# ---------------------------------------------------------------------- raw
if ($Raw) {
    Write-Host "All installs" -ForegroundColor Cyan
    Invoke-D1Query "SELECT * FROM installs WHERE $NotTest ORDER BY last_seen DESC" |
        Format-Table -AutoSize | Out-String | Write-Host
}

Write-Host "Reading these honestly:" -ForegroundColor DarkGray
Write-Host "  These counts include only people who CONNECTED A PEDAL. Anyone who downloaded" -ForegroundColor DarkGray
Write-Host "  the app and never plugged in is invisible here - that is the release-asset" -ForegroundColor DarkGray
Write-Host "  download count on GitHub Releases, a separate number. Dev builds never ping," -ForegroundColor DarkGray
Write-Host "  so your own testing is excluded." -ForegroundColor DarkGray
Write-Host ""
