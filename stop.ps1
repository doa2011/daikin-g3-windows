param([string]$LabPath="$PSScriptRoot\lab")
$ErrorActionPreference='Stop'
$compose=Join-Path $LabPath 'compose.json'
if(-not (Test-Path $compose)){throw '没有找到 compose.json'}
docker compose -f $compose down
