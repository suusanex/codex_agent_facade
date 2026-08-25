[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$packageRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$apmCommand = @(Get-Command apm -CommandType Application -ErrorAction Stop)[0]
$tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar
)
$scratchPath = Join-Path $tempRoot ('github-copilot-apm-' + [guid]::NewGuid().ToString('N'))
$safeToDelete = $false
$locationPushed = $false

try {
    $null = New-Item -ItemType Directory -Path $scratchPath
    $resolvedScratch = (Resolve-Path -LiteralPath $scratchPath).Path
    $requiredPrefix = $tempRoot + [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolvedScratch.StartsWith($requiredPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to use scratch path outside the system temporary directory: $resolvedScratch"
    }
    $safeToDelete = $true

    Push-Location -LiteralPath $resolvedScratch
    $locationPushed = $true
    try {
        & $apmCommand.Source install $packageRoot --target 'codex,agent-skills'
        if ($LASTEXITCODE -ne 0) { throw "apm install failed with exit code $LASTEXITCODE" }
    }
    finally {
        Pop-Location
        $locationPushed = $false
    }

    $sourceSkill = Join-Path $packageRoot '.apm/skills/github-copilot/SKILL.md'
    $installedSkill = Join-Path $resolvedScratch '.agents/skills/github-copilot/SKILL.md'
    if (-not (Test-Path -LiteralPath $installedSkill -PathType Leaf)) {
        throw 'APM installation did not deploy github-copilot/SKILL.md.'
    }
    if ((Get-FileHash -Algorithm SHA256 -LiteralPath $sourceSkill).Hash -ne (Get-FileHash -Algorithm SHA256 -LiteralPath $installedSkill).Hash) {
        throw 'Installed Skill does not match the package source.'
    }
    $unexpectedBinary = Get-ChildItem -LiteralPath (Split-Path $installedSkill) -Recurse -File |
        Where-Object { $_.Extension -in '.exe', '.dll' -or $_.Name -like 'CodexAgentFacade*' }
    if ($unexpectedBinary) { throw 'APM package unexpectedly contains Facade binaries.' }

    Write-Output 'GitHub Copilot package-root APM install smoke test: PASS'
}
finally {
    if ($locationPushed) { Pop-Location }
    if ($safeToDelete -and (Test-Path -LiteralPath $scratchPath)) {
        Remove-Item -LiteralPath $scratchPath -Recurse -Force
    }
}
