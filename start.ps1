param([string]$LabPath="$PSScriptRoot\lab")
$ErrorActionPreference='Stop'
$compose=Join-Path $LabPath 'compose.json'
if(-not (Test-Path $compose)){throw '请先在兼容的 Linux/WSL 环境生成 compose.json'}
docker info | Out-Null
if($LASTEXITCODE -ne 0){throw 'Docker Desktop 未启动'}
docker compose -f $compose up -d
if($LASTEXITCODE -ne 0){throw 'Docker 启动失败'}
docker compose -f $compose ps
Write-Host '服务已启动。默认无 OTA 更新。请检查 OpenWrt DNS 和服务日志。'
