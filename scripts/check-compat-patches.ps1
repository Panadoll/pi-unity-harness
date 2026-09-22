[CmdletBinding()]
param(
  [string]$BaseRef = 'HEAD~1'
)

$ErrorActionPreference = 'Stop'
$changed = @(git diff --name-only $BaseRef HEAD -- 'unity/com.pi.pipeline.compat')
if ($LASTEXITCODE -ne 0) { throw "Could not inspect git diff from $BaseRef" }
if ($changed.Count -gt 0 -and -not ($changed -contains 'unity/com.pi.pipeline.compat/PATCHES.md')) {
  throw 'compat files changed without updating unity/com.pi.pipeline.compat/PATCHES.md'
}
Write-Host 'compat patch metadata check passed'
