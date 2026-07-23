<#
.SYNOPSIS
Runs the usage-telemetry /ping validation checklist against the deployed worker.

.DESCRIPTION
Replaces the hand-run curl rows in docs/VALIDATION-usage-telemetry.md. Sends each
validation case, compares the actual HTTP status to the expected one, and prints a
PASS/FAIL summary. Exits 1 if anything failed, so it can gate a deploy.

Requires PowerShell 7+ (uses -SkipHttpErrorCheck).

.PARAMETER Url
Worker base URL. Defaults to the deployed endpoint.

.PARAMETER TestGuid
Install ID used for the test rows. Must be canonical 8-4-4-4-12 form, or the worker
rejects it by design.

.PARAMETER IncludeRateLimit
Also run the rate-limit case. OFF by default on purpose: it deliberately trips the
20/hour per-IP limit, and that limit is per IP, not per install — so running it before
the real-pedal end-to-end check makes a genuine ping return 429 and land no row.
Run it LAST, or accept an hour's wait.

.PARAMETER SkipDb
Skip the D1 queries (rows 12 and the cleanup). Use when wrangler isn't set up yet.

.PARAMETER Cleanup
Delete this script's test rows from D1 when finished.

.EXAMPLE
pwsh tools/Validate-Telemetry.ps1
Runs rows 1-10 and the D1 double-count check.

.EXAMPLE
pwsh tools/Validate-Telemetry.ps1 -Cleanup -IncludeRateLimit
Full run including the rate-limit case, then removes the test rows.
#>
[CmdletBinding()]
param(
    [string] $Url = 'https://namager-sonulab-feedback.ed-eed.workers.dev',
    [string] $TestGuid = '8f3c1e64-0000-4000-8000-000000000001',
    [switch] $IncludeRateLimit,
    [switch] $SkipDb,
    [switch] $Cleanup
)

$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw "PowerShell 7+ required (this is $($PSVersionTable.PSVersion)). Run with 'pwsh', not 'powershell'."
}

$script:Results = [System.Collections.Generic.List[object]]::new()
$DbName = 'namager-usage'

function Invoke-Ping {
    <# Sends one request and returns its status code, or 0 if the connection itself failed. #>
    param(
        [string] $Method = 'POST',
        [string] $Path = '/ping',
        [string] $Body,
        [string] $ContentType = 'application/json'
    )
    # NB: not named $args - that is an automatic variable in PowerShell.
    $req = @{
        Uri                = "$Url$Path"
        Method             = $Method
        SkipHttpErrorCheck = $true          # non-2xx must be data, not an exception
        TimeoutSec         = 20
    }
    if ($PSBoundParameters.ContainsKey('Body')) {
        $req.Body        = $Body
        $req.ContentType = $ContentType
    }
    try {
        (Invoke-WebRequest @req).StatusCode
    }
    catch {
        # DNS failure, TLS failure, connection refused - not an HTTP status at all.
        Write-Host "      connection error: $($_.Exception.Message)" -ForegroundColor DarkYellow
        0
    }
}

function Test-Case {
    param(
        [Parameter(Mandatory)] [int]    $Number,
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [int]    $Expected,
        [Parameter(Mandatory)] [hashtable] $Request
    )
    Write-Host ("{0,3}. {1,-28}" -f $Number, $Name) -NoNewline
    $actual = Invoke-Ping @Request
    $ok = $actual -eq $Expected
    if ($ok) {
        Write-Host " PASS" -ForegroundColor Green -NoNewline
        Write-Host " ($actual)"
    }
    else {
        Write-Host " FAIL" -ForegroundColor Red -NoNewline
        Write-Host " (expected $Expected, got $(if ($actual -eq 0) { 'no response' } else { $actual }))"
    }
    $script:Results.Add([pscustomobject]@{ Number = $Number; Name = $Name; Passed = $ok })
}

function Get-PingBody {
    param([string] $Id = $TestGuid, [string] $AppVersion = '1.2.0',
          [string] $Fw = '2.5.1', [string] $Transport = 'usb')
    # Built as an object so PowerShell does the escaping - no backslash soup.
    [ordered]@{ installId = $Id; appVersion = $AppVersion; fw = $Fw; transport = $Transport } |
        ConvertTo-Json -Compress
}

function Invoke-D1 {
    <# Runs one SQL statement against the remote D1 database. Returns raw stdout. #>
    param([Parameter(Mandatory)] [string] $Sql)
    $out = npx wrangler d1 execute $DbName --remote --command $Sql 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "wrangler failed (exit $LASTEXITCODE):`n$out" }
    $out
}

Write-Host ""
Write-Host "Usage-telemetry validation" -ForegroundColor Cyan
Write-Host "  endpoint : $Url"
Write-Host "  test id  : $TestGuid"
Write-Host ""

# ---------------------------------------------------------------- HTTP cases
$valid = Get-PingBody

Test-Case 1 'Happy path'          204 @{ Body = $valid }
Test-Case 2 'Duplicate same day'  204 @{ Body = $valid }   # accepted, but must not add a row (row 12)
Test-Case 3 'Not POST'            405 @{ Method = 'GET' }
Test-Case 4 'Wrong content type'  415 @{ Body = 'x'; ContentType = 'text/plain' }
Test-Case 5 'Bad JSON'            400 @{ Body = 'not json' }
Test-Case 6 'Null JSON'           400 @{ Body = 'null' }
Test-Case 7 'Bad GUID'            400 @{ Body = (Get-PingBody -Id 'nope') }
Test-Case 8 'Oversized appVersion' 400 @{ Body = (Get-PingBody -AppVersion ('1' * 21)) }
Test-Case 9 'Blank fw'            400 @{ Body = (Get-PingBody -Fw '') }
Test-Case 10 'Bad transport'      400 @{ Body = (Get-PingBody -Transport 'ble') }

# Extra leaves the hand-written checklist never covered: wrong JSON types.
Test-Case 11 'Non-string appVersion' 400 @{ Body = '{"installId":"' + $TestGuid + '","appVersion":123,"fw":"2.5.1","transport":"usb"}' }
Test-Case 12 'Missing transport'     400 @{ Body = '{"installId":"' + $TestGuid + '","appVersion":"1.2.0","fw":"2.5.1"}' }

# ---------------------------------------------------------------- D1 checks
if (-not $SkipDb) {
    Write-Host ""
    Write-Host "D1 checks" -ForegroundColor Cyan

    Write-Host ("{0,3}. {1,-28}" -f 13, 'Duplicate did not count twice') -NoNewline
    try {
        $days  = Invoke-D1 "SELECT active_days FROM installs WHERE install_id='$TestGuid'"
        $rows  = Invoke-D1 "SELECT COUNT(*) AS n FROM pings WHERE install_id='$TestGuid'"
        # wrangler's table output is parsed loosely on purpose: we only need the integers,
        # and its exact formatting is not a contract we control.
        $dayVal = ([regex]::Matches($days, '\b\d+\b') | Select-Object -Last 1).Value
        $rowVal = ([regex]::Matches($rows, '\b\d+\b') | Select-Object -Last 1).Value
        $ok = ($dayVal -eq '1' -and $rowVal -eq '1')
        if ($ok) { Write-Host " PASS" -ForegroundColor Green -NoNewline; Write-Host " (active_days=1, pings=1)" }
        else {
            Write-Host " FAIL" -ForegroundColor Red -NoNewline
            Write-Host " (active_days=$dayVal, pings=$rowVal - both must be 1)"
            Write-Host "      two pings on one day must produce ONE pings row and ONE active day." -ForegroundColor DarkYellow
        }
        $script:Results.Add([pscustomobject]@{ Number = 13; Name = 'Duplicate did not count twice'; Passed = $ok })
    }
    catch {
        Write-Host " ERROR" -ForegroundColor Red
        Write-Host "      $($_.Exception.Message)" -ForegroundColor DarkYellow
        Write-Host "      If this is 'database not found', run the setup steps in infra/feedback-worker/README.md first." -ForegroundColor DarkYellow
        $script:Results.Add([pscustomobject]@{ Number = 13; Name = 'Duplicate did not count twice'; Passed = $false })
    }
}

# ---------------------------------------------------------------- rate limit
if ($IncludeRateLimit) {
    Write-Host ""
    Write-Host "Rate limit (this burns your IP's budget for an hour)" -ForegroundColor Cyan
    Write-Host ("{0,3}. {1,-28}" -f 14, 'Trips at the hourly cap') -NoNewline

    $tripped = $false
    for ($i = 1; $i -le 30 -and -not $tripped; $i++) {
        if ((Invoke-Ping -Body $valid) -eq 429) { $tripped = $true }
    }
    if ($tripped) { Write-Host " PASS" -ForegroundColor Green -NoNewline; Write-Host " (429 after $i requests)" }
    else          { Write-Host " FAIL" -ForegroundColor Red -NoNewline; Write-Host " (no 429 within 30 requests)" }
    $script:Results.Add([pscustomobject]@{ Number = 14; Name = 'Rate limit'; Passed = $tripped })
}

# ---------------------------------------------------------------- cleanup
if ($Cleanup -and -not $SkipDb) {
    Write-Host ""
    Write-Host "Removing test rows..." -ForegroundColor Cyan
    try {
        Invoke-D1 "DELETE FROM pings WHERE install_id='$TestGuid'"    | Out-Null
        Invoke-D1 "DELETE FROM installs WHERE install_id='$TestGuid'" | Out-Null
        Write-Host "  done - test id $TestGuid removed from both tables."
    }
    catch {
        Write-Host "  cleanup failed: $($_.Exception.Message)" -ForegroundColor Red
        Write-Host "  remove them by hand before trusting the numbers." -ForegroundColor DarkYellow
    }
}

# ---------------------------------------------------------------- summary
$failed = @($script:Results | Where-Object { -not $_.Passed })
Write-Host ""
if ($failed.Count -eq 0) {
    Write-Host "All $($script:Results.Count) checks passed." -ForegroundColor Green
}
else {
    Write-Host "$($failed.Count) of $($script:Results.Count) checks FAILED:" -ForegroundColor Red
    $failed | ForEach-Object { Write-Host "  - $($_.Number). $($_.Name)" -ForegroundColor Red }
}

Write-Host ""
Write-Host "Still manual (this script cannot do them):" -ForegroundColor Cyan
Write-Host "  * Feedback-route regression: use Send Feedback in the app and confirm a"
Write-Host "    'user-feedback' issue appears on EdHubbell/Namager.Sonulab. The router change"
Write-Host "    touched a live endpoint, so do not skip this."
Write-Host "  * Real end-to-end: needs a RELEASE-versioned build and a physical pedal."
Write-Host "      dotnet build src/Namager.App -c Release -p:Version=9.9.9"
Write-Host "    Run the exe from src/Namager.App/bin/Release/net10.0/ and connect the pedal."
Write-Host "    A -dev version will not ping, by design. Then:"
Write-Host "      pwsh tools/Validate-Telemetry.ps1 -TestGuid <the real install id> -SkipDb:`$false"
Write-Host "    or query directly:"
Write-Host "      npx wrangler d1 execute $DbName --remote --command `"SELECT * FROM installs`""
Write-Host ""

exit $(if ($failed.Count -eq 0) { 0 } else { 1 })
