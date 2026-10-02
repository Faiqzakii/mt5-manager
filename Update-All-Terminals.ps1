<#
.SYNOPSIS
    Restarts your OPEN MT5 terminals ONE BY ONE so each one applies its
    pending live update, then confirms that no terminal has any update
    left. Never all at once - so the VPS is safe.

.DESCRIPTION
    - Finds every RUNNING MT5 terminal (terminal64.exe) and handles them
      strictly one at a time:
        1. remembers the current terminal64.exe version
        2. closes the terminal gracefully (EAs stop cleanly; open
           positions are safe - they live at the broker, not in the
           terminal)
        3. starts it again from its own folder -> MT5 applies any
           downloaded update during startup
        4. waits for the terminal to come back and settle, re-reads the
           version and checks the terminal journal (logs) for fresh
           update activity
        5. pauses before the next terminal, so two restarts never load
           the VPS at the same time
    - If during the wait a terminal announces an EVEN NEWER update, the
      whole sweep is simply repeated (up to $MaxPasses times). A full
      clean pass with no version change and no update activity means:
      "CONFIRMED - no more updates on any open MT5".
    - Terminals that are NOT running are only reported - they update
      themselves the next time you start them.
    - Everything is also written to Update-Terminals-<date>.txt next to
      this script.

    Useful switches (run from a console, not needed for daily use):
        -ListOnly          just show all terminals + versions, touch nothing
        -Only 0            restart only instance number 0 (-Only 3,5,7 also works)
        -NoPause           skip the final "Press Enter" pause

    This tool does NOT need administrator rights.
#>

param(
    # Only list the terminals and their versions - restart nothing.
    [switch]$ListOnly,
    # Restrict to instance numbers, e.g. -Only 0 or -Only 3,5,7.
    [int[]]$Only,
    # Testing overrides - leave empty on the VPS.
    [string]$ProgramFilesOverride,
    [string]$AppDataOverride,
    # Skip the final "Press Enter" pause.
    [switch]$NoPause
)

# =========================== CONFIGURATION ===========================
$ProgramFilesRoot      = "C:\Program Files"
$FolderRegex           = '^MetaTrader 5 - (\d+)$'
$TerminalExe           = 'terminal64.exe'
$TerminalDataRelative  = 'MetaQuotes\Terminal'   # under AppData (Roaming)

$GapSeconds            = 30     # pause BETWEEN terminals (protects the VPS)
$SettleSeconds         = 90     # wait after a restart: update applies + terminal reconnects
$CloseTimeoutSeconds   = 90     # how long to wait for a graceful close
$StartTimeoutSeconds   = 120    # how long to wait for the terminal window to come back
$ForceKillAfterTimeout = $true  # force-close if a terminal refuses to close
$LockReleaseSeconds    = 5      # small pause after exit before starting it again
$MaxPasses             = 3      # full sweeps before giving up on "no more updates"

# Journal lines containing any of these (after a restart) mean the
# terminal has seen an update - either applied or still downloading.
$UpdateLogPatterns     = @('live update', 'new version', 'update to build', 'update package')
# =====================================================================

# ============================== BRANDING ==============================
$BrandName    = 'MT5 VPS Setup Kit'
$BrandVersion = 'v2.2'
$BrandOwner   = 't.me/pintoil'
$BrandYear    = '2026'

$script:LogFile = $null

function Write-Log {
    param([string]$Message, [string]$Color)
    if ($Color) { Write-Host $Message -ForegroundColor $Color }
    else        { Write-Host $Message }
    if ($script:LogFile) {
        Add-Content -LiteralPath $script:LogFile -Value $Message -ErrorAction SilentlyContinue
    }
}

function Show-Brand {
    Write-Log "==================================================" Cyan
    Write-Log ("   {0} {1} (TM)" -f $BrandName, $BrandVersion) Cyan
    Write-Log "   Your MetaTrader assistant" DarkCyan
    Write-Log ("   Trademark & Copyright (C) {0} {1} - All rights reserved" -f $BrandYear, $BrandOwner) Cyan
    Write-Log "==================================================" Cyan
}

function Show-BrandFooter {
    Write-Log "--------------------------------------------------" Cyan
    Write-Log ("   {0} {1} (TM)  -  (C) {2} {3}  -  Thank you!" -f $BrandName, $BrandVersion, $BrandYear, $BrandOwner) Cyan
    Write-Log "--------------------------------------------------" Cyan
}

try { $Host.UI.RawUI.WindowTitle = "{0} {1} - Update All - by {2}" -f $BrandName, $BrandVersion, $BrandOwner } catch { }
# ======================================================================

function Pause-End { if (-not $NoPause) { Read-Host "Press Enter to close" | Out-Null } }

# ============================== HELPERS ==============================

function Get-ExeVersion([string]$Path) {
    try {
        $v = (Get-Item -LiteralPath $Path -ErrorAction Stop).VersionInfo.FileVersion
        if ($v) { return $v }
    } catch { }
    return '(unknown)'
}

function Get-RunningTerminals([string]$AppDataRoot) {
    $list = @()
    $procs = @(Get-CimInstance -ClassName Win32_Process -Filter "Name = 'terminal64.exe'" -ErrorAction SilentlyContinue)
    foreach ($p in $procs) {
        if (-not $p.ExecutablePath) {
            Write-Log ("[WARN] A terminal64.exe process (PID {0}) is closing down or its path cannot be read - skipped." -f $p.ProcessId) Yellow
            continue
        }
        $installDir = $null
        try { $installDir = Split-Path -Parent $p.ExecutablePath } catch { }
        if (-not $installDir) {
            Write-Log ("[WARN] A terminal64.exe process (PID {0}) reports an unusable path ({1}) - skipped." -f $p.ProcessId, $p.ExecutablePath) Yellow
            continue
        }
        $portable   = ($p.CommandLine -match '(?i)[/-]\s*portable\b')
        $dataFolder = $null
        if ($portable) {
            $dataFolder = $installDir
        }
        else {
            # normal mode: the hidden data folder in AppData points back to
            # the install folder through origin.txt (same trick as Push-EAs)
            $terminalDataPath = Join-Path $AppDataRoot $TerminalDataRelative
            if (Test-Path -LiteralPath $terminalDataPath) {
                $want = $p.ExecutablePath.TrimEnd('\')
                foreach ($df in @(Get-ChildItem -LiteralPath $terminalDataPath -Directory -ErrorAction SilentlyContinue)) {
                    $originFile = Join-Path $df.FullName 'origin.txt'
                    if (-not (Test-Path -LiteralPath $originFile)) { continue }
                    $origin = ((Get-Content -LiteralPath $originFile -Raw -ErrorAction SilentlyContinue) -replace "`0", '').Trim()
                    if ($origin -and ($origin.TrimEnd('\') -ieq $want)) { $dataFolder = $df.FullName; break }
                }
            }
        }
        $list += [pscustomobject]@{
            Pid        = [int]$p.ProcessId
            ExePath    = $p.ExecutablePath
            InstallDir = $installDir
            Portable   = $portable
            DataFolder = $dataFolder
        }
    }
    return $list
}

function Select-TargetTerminals {
    param([object[]]$Running, [int[]]$OnlyNumbers, [switch]$ReportSkips)
    $targets = @()
    foreach ($t in @($Running)) {
        $leaf = Split-Path -Leaf $t.InstallDir
        $num  = $null
        if ($leaf -match $FolderRegex) { $num = [int]$Matches[1] }
        if ($OnlyNumbers) {
            if ($null -eq $num -or ($OnlyNumbers -notcontains $num)) {
                if ($ReportSkips) { Write-Log ("[INFO] Skipping {0} (-Only {1} was given)." -f $leaf, ($OnlyNumbers -join ',')) }
                continue
            }
        }
        $targets += $t
    }
    return $targets
}

# MT5 journal files are UTF-16 or UTF-8 - read bytes and decode properly.
function Get-JournalLines([string]$LogFilePath) {
    try {
        $bytes = [System.IO.File]::ReadAllBytes($LogFilePath)
        if ($bytes.Length -eq 0) { return @() }
        if ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE) {
            $text = [System.Text.Encoding]::Unicode.GetString($bytes, 2, $bytes.Length - 2)
        }
        else {
            $text = [System.Text.Encoding]::UTF8.GetString($bytes)
        }
        return @($text -split "`r?`n")
    }
    catch {
        return @(Get-Content -LiteralPath $LogFilePath -ErrorAction SilentlyContinue)
    }
}

# Returns the journal lines written AFTER $Since (the restart moment).
function Get-FreshJournalLines([string]$DataFolder, [datetime]$Since) {
    $result = @{ Available = $false; Lines = @() }
    if (-not $DataFolder) { return $result }
    $logsDir = Join-Path $DataFolder 'logs'
    if (-not (Test-Path -LiteralPath $logsDir)) { return $result }

    $candidates = @()
    foreach ($offset in 0, -1) {   # today + yesterday, in case midnight passed
        $p = Join-Path $logsDir ((Get-Date).AddDays($offset).ToString('yyyyMMdd') + '.log')
        if (Test-Path -LiteralPath $p) { $candidates += $p }
    }
    if ($candidates.Count -eq 0) { return $result }

    foreach ($file in $candidates) {
        $after = $false
        foreach ($line in @(Get-JournalLines $file)) {
            if ($line -match '^(\d{4}\.\d{2}\.\d{2} \d{2}:\d{2}:\d{2})') {
                $ts  = [datetime]::MinValue
                $ok  = [datetime]::TryParseExact($Matches[1], 'yyyy.MM.dd HH:mm:ss',
                            [System.Globalization.CultureInfo]::InvariantCulture,
                            [System.Globalization.DateTimeStyles]::None, [ref]$ts)
                if ($ok -and $ts -ge $Since) { $after = $true }
            }
            if ($after -and $line.Trim()) { $result.Lines += $line }
        }
    }
    $result.Available = $true
    return $result
}

function Find-UpdateMention([string[]]$Lines) {
    foreach ($line in @($Lines)) {
        foreach ($pat in $UpdateLogPatterns) {
            if ($line -and $line.ToLower().Contains($pat)) { return $line.Trim() }
        }
    }
    return $null
}

# ====================== RESTART ONE TERMINAL =========================

function Restart-OneTerminal {
    param([object]$Terminal)

    $name = Split-Path -Leaf $Terminal.InstallDir
    $exe  = Join-Path $Terminal.InstallDir $TerminalExe
    $versionBefore = Get-ExeVersion $exe

    Write-Log ""
    Write-Log ("[{0}] ==================================================" -f $name)
    Write-Log ("[{0}] Version now: {1}" -f $name, $versionBefore)

    # 1) close gracefully - EAs stop cleanly, positions stay at the broker
    $proc = Get-Process -Id $Terminal.Pid -ErrorAction SilentlyContinue
    if ($proc) {
        Write-Log ("[{0}] Closing gracefully (EAs stop cleanly, positions are safe)..." -f $name)
        try { $null = $proc.CloseMainWindow() } catch { }
        if (-not $proc.WaitForExit($CloseTimeoutSeconds * 1000)) {
            if ($ForceKillAfterTimeout) {
                Write-Log ("[{0}] Still open after {1}s - force closing it now." -f $name, $CloseTimeoutSeconds) Yellow
                try { Stop-Process -Id $Terminal.Pid -Force -ErrorAction Stop } catch { }
            }
            else {
                Write-Log ("[{0}] Still open after {1}s - SKIPPED (ForceKillAfterTimeout is off)." -f $name, $CloseTimeoutSeconds) Yellow
                return $null
            }
        }
        # make sure it is really gone before starting again
        $deadline = (Get-Date).AddSeconds(15)
        while ((Get-Date) -lt $deadline -and (Get-Process -Id $Terminal.Pid -ErrorAction SilentlyContinue)) {
            Start-Sleep -Seconds 1
        }
    }
    Start-Sleep -Seconds $LockReleaseSeconds

    # 2) start it again from its own folder (MT5 applies the update on startup)
    Write-Log ("[{0}] Starting it again..." -f $name)
    $since = (Get-Date).AddSeconds(-5)   # journal clock may differ slightly
    try {
        if ($Terminal.Portable) { Start-Process -FilePath $exe -WorkingDirectory $Terminal.InstallDir -ArgumentList '/portable' | Out-Null }
        else                    { Start-Process -FilePath $exe -WorkingDirectory $Terminal.InstallDir | Out-Null }
    }
    catch {
        Write-Log ("[{0}] [WARN] Could not start again: {1}" -f $name, $_.Exception.Message) Yellow
        return [pscustomobject]@{ Name = $name; DataFolder = $Terminal.DataFolder; Before = $versionBefore;
                                  After = '(not running)'; Applied = $false; Pending = $true; JournalOK = $false }
    }

    # 3) wait for the terminal window to come back
    $windowBack = $false
    $deadline   = (Get-Date).AddSeconds($StartTimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 3
        $cand = @(Get-CimInstance -ClassName Win32_Process -Filter "Name = 'terminal64.exe'" -ErrorAction SilentlyContinue |
                  Where-Object { $_.ExecutablePath -ieq $exe })
        if ($cand.Count -gt 0) {
            $gp = Get-Process -Id ([int]$cand[0].ProcessId) -ErrorAction SilentlyContinue
            if ($gp) {
                $gp.Refresh()
                if ($gp.MainWindowHandle -ne 0) { $windowBack = $true; break }
            }
        }
    }
    $cand = @(Get-CimInstance -ClassName Win32_Process -Filter "Name = 'terminal64.exe'" -ErrorAction SilentlyContinue |
              Where-Object { $_.ExecutablePath -ieq $exe })
    if ($windowBack)      { Write-Log ("[{0}] Terminal window is back up." -f $name) Green }
    elseif ($cand.Count -gt 0) { Write-Log ("[{0}] Process is running (window not detected yet) - continuing anyway." -f $name) Yellow }
    else {
        Write-Log ("[{0}] [WARN] Terminal did NOT come back within {1}s!" -f $name, $StartTimeoutSeconds) Red
        Write-Log ("[{0}]        Start it by hand, then run this tool again." -f $name) Red
        return [pscustomobject]@{ Name = $name; DataFolder = $Terminal.DataFolder; Before = $versionBefore;
                                  After = '(not running)'; Applied = $false; Pending = $true; JournalOK = $false }
    }

    # 4) let it settle: update applies, terminal reconnects, journal fills
    Write-Log ("[{0}] Waiting {1}s so the update can apply and the terminal can reconnect..." -f $name, $SettleSeconds)
    Start-Sleep -Seconds $SettleSeconds

    $versionAfter = Get-ExeVersion $exe
    $applied      = ($versionAfter -ne $versionBefore)
    $stillAlive   = @(Get-CimInstance -ClassName Win32_Process -Filter "Name = 'terminal64.exe'" -ErrorAction SilentlyContinue |
                      Where-Object { $_.ExecutablePath -ieq $exe }).Count -gt 0

    $journal    = Get-FreshJournalLines $Terminal.DataFolder $since
    $updateLine = $null
    if ($journal.Available) { $updateLine = Find-UpdateMention $journal.Lines }
    $pending = $stillAlive -and $journal.Available -and ($null -ne $updateLine)

    # report
    if (-not $stillAlive) {
        Write-Log ("[{0}] [WARN] Terminal is not running anymore after the restart!" -f $name) Red
        $versionAfter = '(not running)'
        $pending = $true
    }
    elseif ($applied) {
        Write-Log ("[{0}] [OK] Update applied:  {1}  ->  {2}" -f $name, $versionBefore, $versionAfter) Green
    }
    else {
        Write-Log ("[{0}] Version unchanged ({1}) - nothing new was waiting." -f $name, $versionAfter)
    }

    if (-not $journal.Available) {
        Write-Log ("[{0}] [INFO] Journal (logs folder) not found - update check by log is not possible here." -f $name) Yellow
    }
    elseif ($stillAlive -and $updateLine) {
        Write-Log ("[{0}] [NOTE] Journal mentions an update after the restart:" -f $name) Yellow
        Write-Log ("         {0}" -f $updateLine) Yellow
    }
    elseif ($stillAlive) {
        Write-Log ("[{0}] Journal shows no new update activity." -f $name)
    }

    return [pscustomobject]@{ Name = $name; DataFolder = $Terminal.DataFolder; Before = $versionBefore;
                              After = $versionAfter; Applied = $applied; Pending = $pending; JournalOK = $journal.Available }
}

# ============================== SUMMARY ==============================

function Show-Summary([System.Collections.IDictionary]$Summary) {
    Write-Log ""
    Write-Log "=== Update summary (open MT5 terminals) ==="
    $header = ("{0}  {1}  {2}  {3}  {4}" -f "Instance".PadRight(28), "Before".PadRight(14),
               "After".PadRight(16), "Applied".PadRight(8), "Pending")
    Write-Log $header
    Write-Log ("-" * ($header.Length + 20))
    foreach ($s in $Summary.Values) {
        $appliedTxt = "no";   if ($s.Applied) { $appliedTxt = "YES" }
        $pendingTxt = "no"
        if (-not $s.JournalOK) { $pendingTxt = "n/a (no journal)" }
        if ($s.Pending)        { $pendingTxt = "YES  <- needs attention" }
        $line = ("{0}  {1}  {2}  {3}  {4}" -f $s.Name.PadRight(28), $s.Before.PadRight(14),
                 $s.After.PadRight(16), $appliedTxt.PadRight(8), $pendingTxt)
        if ($s.Pending) { Write-Log $line Red } else { Write-Log $line }
    }
}

# =============================== MAIN ================================

Write-Log ""
Show-Brand

$appDataRoot = if ($AppDataOverride)      { $AppDataOverride }      else { [Environment]::GetFolderPath('ApplicationData') }
$pfRoot      = if ($ProgramFilesOverride) { $ProgramFilesOverride } else { $ProgramFilesRoot }
$scriptDir   = if ($PSScriptRoot)         { $PSScriptRoot }         else { (Get-Location).Path }

if (-not $ListOnly) {
    $script:LogFile = Join-Path $scriptDir ("Update-Terminals-{0}.txt" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
}

# all installed instance folders (for the overview + "not running" report)
$instances = @(Get-ChildItem -LiteralPath $pfRoot -Directory -ErrorAction SilentlyContinue |
    Where-Object { ($_.Name -match $FolderRegex) -and (Test-Path -LiteralPath (Join-Path $_.FullName $TerminalExe)) } |
    Sort-Object { if ($_.Name -match $FolderRegex) { [int]$Matches[1] } else { 0 } })

# what is open right now?
$running = @(Get-RunningTerminals -AppDataRoot $appDataRoot)
$runningDirs = @($running | ForEach-Object { $_.InstallDir.TrimEnd('\') })

# ---------------------------- LIST ONLY ------------------------------
if ($ListOnly) {
    Write-Log ""
    Write-Log ("Running MT5 terminals : {0}" -f $running.Count)
    foreach ($t in $running) {
        $ver = Get-ExeVersion (Join-Path $t.InstallDir $TerminalExe)
        $journalTxt = "journal found"; if (-not $t.DataFolder) { $journalTxt = "journal NOT FOUND" }
        Write-Log ("   {0}  v{1}  (PID {2}, {3}, portable: {4})" -f `
            (Split-Path -Leaf $t.InstallDir), $ver, $t.Pid, $journalTxt, $t.Portable)
    }
    Write-Log ""
    Write-Log ("Instance folders in {0} : {1}" -f $pfRoot, $instances.Count)
    foreach ($inst in $instances) {
        $ver = Get-ExeVersion (Join-Path $inst.FullName $TerminalExe)
        $state = "not running"
        foreach ($d in $runningDirs) { if ($d -ieq $inst.FullName.TrimEnd('\')) { $state = "RUNNING"; break } }
        Write-Log ("   {0}  v{1}  {2}" -f $inst.Name, $ver, $state)
    }
    Write-Log ""
    Write-Log "[INFO] List only - nothing was touched."
    Show-BrandFooter
    Pause-End
    exit 0
}

# --------------------------- NOTHING OPEN ----------------------------
if ($running.Count -eq 0) {
    Write-Log ""
    Write-Log "[INFO] No MT5 terminal is running right now - nothing to update."
    if ($instances.Count -gt 0) {
        Write-Log "       Terminals pick up their update automatically the next"
        Write-Log "       time you start them (Start-All-Terminals.bat)."
    }
    else {
        Write-Log ("       No instance folders found in {0} either." -f $pfRoot)
        Write-Log "       Run Setup-MT5.bat first to create them."
    }
    Show-BrandFooter
    Pause-End
    exit 0
}

# ----------------------------- CONFIRM -------------------------------
$targets = @(Select-TargetTerminals -Running $running -OnlyNumbers $Only -ReportSkips)
if ($targets.Count -eq 0) {
    Write-Log "[INFO] No running terminal matches -Only $($Only -join ','). Nothing to do."
    Show-BrandFooter
    Pause-End
    exit 0
}

Write-Log ""
Write-Log ("MT5 terminals to restart ONE BY ONE: {0}" -f $targets.Count)
foreach ($t in $targets) {
    $ver = Get-ExeVersion (Join-Path $t.InstallDir $TerminalExe)
    Write-Log ("   {0}  (v{1})" -f (Split-Path -Leaf $t.InstallDir), $ver)
}
Write-Log ""
Write-Log ("Each terminal is closed, started again and given {0}s to settle -" -f $SettleSeconds)
Write-Log ("with a {0}s pause between terminals, so the VPS stays comfortable." -f $GapSeconds)
$answer = Read-Host ("Restart these {0} terminal(s) now? (Y/N)" -f $targets.Count)
if (-not ($answer -match '^[yY]' -or $answer -eq '')) {
    Write-Log "Cancelled - nothing was touched." Yellow
    Show-BrandFooter
    Pause-End
    exit 0
}

# --------------------- THE ONE-BY-ONE SWEEPS -------------------------
$summary   = [ordered]@{}
$confirmed = $false
$pass      = 0

while ($pass -lt $MaxPasses) {
    $pass++
    Write-Log ""
    Write-Log ("================= PASS {0} of {1} =================" -f $pass, $MaxPasses) Cyan

    # re-discover: PIDs changed after every restart
    $running = @(Get-RunningTerminals -AppDataRoot $appDataRoot)
    $targets = @(Select-TargetTerminals -Running $running -OnlyNumbers $Only)
    if ($targets.Count -eq 0) {
        Write-Log "[INFO] No MT5 terminal is running anymore - nothing left to update."
        break
    }

    $anyActivity = $false
    for ($i = 0; $i -lt $targets.Count; $i++) {
        if ($i -gt 0) {
            Write-Log ""
            Write-Log ("[WAIT] Pausing {0}s before the next terminal (go easy on the VPS)..." -f $GapSeconds)
            Start-Sleep -Seconds $GapSeconds
        }
        $r = Restart-OneTerminal -Terminal $targets[$i]
        if ($r) {
            $anyActivity = $anyActivity -or $r.Applied -or $r.Pending
            if (-not $summary.Contains($r.Name)) {
                $summary[$r.Name] = [pscustomobject]@{ Name = $r.Name; Before = $r.Before; After = $r.After;
                                                       Applied = $false; Pending = $false; JournalOK = $r.JournalOK }
            }
            $s = $summary[$r.Name]
            $s.After     = $r.After
            $s.Pending   = $r.Pending
            $s.JournalOK = $r.JournalOK
            if ($r.Applied) { $s.Applied = $true }
        }
    }

    if (-not $anyActivity) {
        # a full pass with zero updates applied and zero update announcements
        $confirmed = $true
        break
    }

    if ($pass -lt $MaxPasses) {
        Write-Log ""
        Write-Log ("[NOTE] Something still updated or announced a new version - running another pass (max {0})." -f $MaxPasses) Yellow
    }
}

# --------------------------- FINAL REPORT ----------------------------
Show-Summary $summary

$stillPending = @($summary.Values | Where-Object { $_.Pending })
Write-Log ""
if ($confirmed -and $stillPending.Count -eq 0) {
    Write-Log ("[OK] CONFIRMED: all {0} open MT5 terminal(s) restarted fine and" -f $summary.Count) Green
    Write-Log "     NO MORE UPDATES are waiting. You are up to date." Green
}
else {
    Write-Log "[WARN] Could not confirm 'no more updates' for every terminal:" Yellow
    foreach ($s in $stillPending) {
        $why = "journal reports update activity"
        if (-not $s.JournalOK)      { $why = "journal not readable - check by hand" }
        if ($s.After -eq '(not running)') { $why = "terminal did not come back - start it by hand" }
        Write-Log ("       {0}: {1}" -f $s.Name, $why) Yellow
    }
    Write-Log "       Run this tool again in a while (updates arrive in waves),"
    Write-Log "       or restart the listed terminals once more by hand."
}

if ($instances.Count -gt $summary.Count) {
    $idleNames = @()
    foreach ($inst in $instances) {
        $wasRunning = $false
        foreach ($d in $runningDirs) { if ($d -ieq $inst.FullName.TrimEnd('\')) { $wasRunning = $true; break } }
        if (-not $wasRunning -and -not $summary.Contains($inst.Name)) { $idleNames += $inst.Name }
    }
    if ($idleNames.Count -gt 0) {
        Write-Log ""
        Write-Log ("[INFO] Not running (they update themselves on their next start): {0}" -f ($idleNames -join ', '))
    }
}

if ($script:LogFile) {
    Write-Log ""
    Write-Log ("[INFO] Full report saved to: {0}" -f $script:LogFile)
}

Write-Log ""
Show-BrandFooter
Pause-End

if ($confirmed -and $stillPending.Count -eq 0) { exit 0 } else { exit 1 }
