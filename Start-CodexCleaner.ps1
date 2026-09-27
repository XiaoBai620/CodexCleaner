$ErrorActionPreference = 'Stop'
$cleanerBase = Split-Path -Parent $MyInvocation.MyCommand.Path
try {
    $nodeCandidates = @()
    $nodeCommand = Get-Command node.exe -ErrorAction SilentlyContinue
    if ($nodeCommand) { $nodeCandidates += $nodeCommand.Source }
    $nodeCandidates += (Join-Path $env:USERPROFILE '.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe')
    if ($env:ProgramFiles) { $nodeCandidates += (Join-Path $env:ProgramFiles 'nodejs\node.exe') }
    if (${env:ProgramFiles(x86)}) { $nodeCandidates += (Join-Path ${env:ProgramFiles(x86)} 'nodejs\node.exe') }
    $cleanerNode = $null
    foreach ($candidate in ($nodeCandidates | Select-Object -Unique)) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            & $candidate --no-warnings -e "if (Number(process.versions.node.split('.')[0]) < 24) process.exit(1); require('node:sqlite');" 2>$null
            if ($LASTEXITCODE -eq 0) { $cleanerNode = $candidate; break }
        }
    }
    if (-not $cleanerNode) { throw 'Node.js 24 or newer with node:sqlite is required. Install it from the official Node.js website, then retry.' }
    $cleanerServer = Join-Path $cleanerBase 'server.cjs'
    if (-not (Test-Path -LiteralPath $cleanerServer -PathType Leaf)) { throw 'server.cjs is missing. Extract the complete tool folder first.' }
    $serverArgs = @('--no-warnings', ('"' + $cleanerServer + '"'))
    $cleanerLogRoot = Join-Path $env:LOCALAPPDATA 'CodexCleaner'
    New-Item -ItemType Directory -Force -Path $cleanerLogRoot | Out-Null
    $cleanerLogName = 'startup-' + [Guid]::NewGuid().ToString('N')
    $cleanerOut = Join-Path $cleanerLogRoot ($cleanerLogName + '.log')
    $cleanerErr = Join-Path $cleanerLogRoot ($cleanerLogName + '.error.log')
    $cleanerProcess = Start-Process -FilePath $cleanerNode -ArgumentList $serverArgs -WorkingDirectory $cleanerBase -WindowStyle Hidden -RedirectStandardOutput $cleanerOut -RedirectStandardError $cleanerErr -PassThru
    $cleanerDeadline = [DateTime]::UtcNow.AddSeconds(15)
    $cleanerReady = $false
    do {
        Start-Sleep -Milliseconds 100
        $cleanerProcess.Refresh()
        if ($cleanerProcess.HasExited) { break }
        $cleanerOutput = Get-Content -LiteralPath $cleanerOut -Raw -ErrorAction SilentlyContinue
        $cleanerReady = $cleanerOutput -match '(?m)^Codex Cleaner ready: http://127\.0\.0\.1:\d+/'
    } until ($cleanerReady -or [DateTime]::UtcNow -ge $cleanerDeadline)
    if ($cleanerProcess.HasExited -or -not $cleanerReady) {
        $cleanerError = Get-Content -LiteralPath $cleanerErr -Raw -ErrorAction SilentlyContinue
        throw ('The tool did not finish starting. See ' + $cleanerErr + '. ' + $cleanerError)
    }
    Write-Host 'Codex Cleaner is opening in your default browser. No data is deleted automatically.'
    Write-Host 'Close Codex and its command-line sessions before confirming a cleanup.'
} catch {
    Write-Host ('Unable to launch Codex Cleaner: ' + $_.Exception.Message) -ForegroundColor Red
    exit 1
}
