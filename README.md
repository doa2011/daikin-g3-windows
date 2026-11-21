# Daikin G3 Windows migration helper (V0.1)

Windows 10 + Docker Desktop + OpenWrt companion tool for factory G3 migration. This package does **not** flash firmware or enable OTA.

**Important:** Upstream `tools/daikin_g3_migrate/init.py` uses `os.getuid()`/`os.getgid()` and POSIX paths, so it cannot run under native Windows Python. `prepare.ps1` currently performs prerequisite validation and deliberately stops before calling the incompatible script. Linux/WSL2 integration and end-to-end testing are next milestones. Do not treat a passing CI syntax check as a working migration service.

Requirements: Windows 10 with WSL2 and Docker Desktop, Python 3, a matching verified upstream migration bundle, G3's Wi-Fi STA MAC (not BLE MAC), and OpenWrt DNS overrides. Only use compatible matching firmware and SHA256SUMS.

```powershell
.\prepare.ps1 -WindowsIP 192.168.1.10 -DeviceWifiMac AABBCCDDEEFF -BundlePath C:\g3-migration
```

The command currently stops with an explicit WSL/Linux compatibility warning. `start.ps1` and `stop.ps1` manage a previously generated `lab/compose.json` if available.

Upstream GPL-3.0 project: https://github.com/phicomm-io/iotcloud-community-firmware/tree/main/tools/daikin_g3_migrate
