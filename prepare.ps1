param([Parameter(Mandatory=$true)][string]$WindowsIP,[Parameter(Mandatory=$true)][string]$DeviceWifiMac,[Parameter(Mandatory=$true)][string]$BundlePath,[string]$LabPath="$PSScriptRoot\lab")
$ErrorActionPreference='Stop'
foreach($name in @('python','docker')) { if(-not (Get-Command $name -ErrorAction SilentlyContinue)){throw "缺少 $name"} }
$parsed=$null
if(-not [System.Net.IPAddress]::TryParse($WindowsIP,[ref]$parsed) -or $parsed.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork){throw 'WindowsIP 必须是 IPv4'}
if($DeviceWifiMac -notmatch '^[0-9a-fA-F]{12}$'){throw '请提供12位十六进制 Wi-Fi STA MAC（非蓝牙 MAC）'}
$source=Join-Path $BundlePath 'tools\daikin_g3_migrate'
if(-not (Test-Path (Join-Path $source 'init.py'))){$source=$BundlePath}
if(-not (Test-Path (Join-Path $source 'init.py'))){throw '上游迁移包中找不到 init.py'}
if(Test-Path $LabPath){throw '目标目录已存在，为避免覆盖请另选 LabPath'}
foreach($port in @(443,8883)){if(Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue){throw "端口 $port 已被占用"}}
# Upstream init.py requires Linux os.getuid/getgid/chmod; Windows Python cannot execute it directly.
throw '当前上游 init.py 仅支持 POSIX/Linux。请勿直接在 Windows Python 中运行。下一版本将通过 WSL2/Docker Linux 环境生成迁移配置；当前版本仅提供环境预检。'
