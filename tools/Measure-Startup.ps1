<#
.SYNOPSIS
    Measures how long Memos takes to show its window and its first tab.

.DESCRIPTION
    Starts the unpackaged build several times, each with its own throw-away data folder, and
    reports the time from process start to the window appearing and to the first tab being
    available (read with UI Automation, as a user would see it).

    Use it before and after a change that could affect start-up:

      powershell tools/Measure-Startup.ps1 -Runs 10

    Run it on an idle machine: another build, a virus scan or a debugger changes the numbers.

.PARAMETER Exe
    Memos.exe to measure. By default the newest unpackaged build under Memos\Memos\bin.

.PARAMETER Runs
    Number of launches. The median of the runs is reported.
#>
param(
    [string]$Exe,
    [int]$Runs = 5
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Find-App {
    if ($Exe) {
        if (-not (Test-Path $Exe)) { throw "MEMOS_EXE candidate does not exist: $Exe" }
        return (Resolve-Path $Exe).Path
    }

    $bin = Join-Path (Split-Path $PSScriptRoot -Parent) 'Memos\Memos\bin'
    $candidate = Get-ChildItem $bin -Filter Memos.exe -Recurse -ErrorAction SilentlyContinue |
        Where-Object { Test-Path (Join-Path $_.DirectoryName 'Microsoft.WindowsAppRuntime.dll') } |
        Sort-Object { (Get-Item (Join-Path $_.DirectoryName 'Memos.dll')).LastWriteTimeUtc } -Descending |
        Select-Object -First 1
    if (-not $candidate) {
        throw 'No unpackaged build found. Build it first with: dotnet build Memos/Memos/Memos.csproj -c Release -p:Platform=x64 -p:MemosUnpackaged=true'
    }

    return $candidate.FullName
}

function Measure-Start([string]$path) {
    $root = Join-Path ([IO.Path]::GetTempPath()) ('MemosStartup_' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path (Join-Path $root 'data') -Force | Out-Null
    # Own data folder: the user's settings and session are untouched, and the single-instance
    # key is different, so an open Memos window does not receive these launches.
    [IO.File]::WriteAllText((Join-Path $root 'data\settings.json'), '{ "Language": "en-US" }')
    $env:MEMOS_DATA_DIR = Join-Path $root 'data'

    $watch = [Diagnostics.Stopwatch]::StartNew()
    $process = [Diagnostics.Process]::Start($path)
    $window = [IntPtr]::Zero
    while ($watch.ElapsedMilliseconds -lt 30000) {
        $process.Refresh()
        if ($process.HasExited) { break }
        if ($process.MainWindowHandle -ne [IntPtr]::Zero) { $window = $process.MainWindowHandle; break }
        Start-Sleep -Milliseconds 10
    }
    $windowMs = $watch.ElapsedMilliseconds

    $tabMs = -1
    if ($window -ne [IntPtr]::Zero) {
        try {
            $element = [System.Windows.Automation.AutomationElement]::FromHandle($window)
            $tab = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::TabItem)
            while ($watch.ElapsedMilliseconds -lt 30000) {
                if ($element.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $tab)) {
                    $tabMs = $watch.ElapsedMilliseconds
                    break
                }
                Start-Sleep -Milliseconds 15
            }
        } catch {
            # The window closed or changed while it was being read; report the window time alone.
        }
    }

    try { $process.Kill($true); $process.WaitForExit(5000) } catch { }
    $process.Dispose()
    Remove-Item -Recurse -Force $root -ErrorAction SilentlyContinue

    [pscustomobject]@{ WindowMs = $windowMs; FirstTabMs = $tabMs }
}

function Get-Median($values) {
    $sorted = @($values | Where-Object { $_ -gt 0 } | Sort-Object)
    if ($sorted.Count -eq 0) { return -1 }
    return $sorted[[int][math]::Floor($sorted.Count / 2)]
}

$path = Find-App
Write-Host "Measuring $path ($Runs launches)" -ForegroundColor Cyan

$results = @()
for ($i = 1; $i -le $Runs; $i++) {
    $result = Measure-Start $path
    $results += $result
    '{0,3}. window {1,6:n0} ms    first tab {2,6:n0} ms' -f $i, $result.WindowMs, $result.FirstTabMs | Write-Host
    Start-Sleep -Milliseconds 300
}

Write-Host ''
'Median: window {0:n0} ms, first tab {1:n0} ms' -f `
    (Get-Median ($results.WindowMs)), (Get-Median ($results.FirstTabMs)) | Write-Host -ForegroundColor Green
