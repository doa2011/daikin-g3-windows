# Daikin G3 Windows migration helper (v0.2)

This project now builds a **real Windows x64 EXE** (self-contained .NET 8 CLI) on GitHub Actions.

Download the `g3-windows-exe-win-x64` artifact from the latest successful Actions run, unzip and run:

```powershell
.\G3Windows.exe help
.\G3Windows.exe check
.\G3Windows.exe guide
```

**Current scope:** executable host prerequisite diagnostics and migration instructions; no GUI, no gateway implementation, no OTA commands. The PowerShell scripts are included but `prepare.ps1` deliberately stops before the upstream Linux-only `init.py` invocation. Thus a successful EXE build does not mean the complete wireless migration is ready. WSL2/Docker host orchestration and a GUI are next.

For G3 model BRY88BBA15G / SENSOR3_ESP32 only. Upstream migration procedure requires per-device OpenWrt DNS redirects, MQTT/HTTPS, a running gateway responder, the real Wi-Fi STA MAC and matching firmware. **Do not flash before gateway association and OTA validation.**

Upstream GPL-3.0 documentation: https://github.com/phicomm-io/iotcloud-community-firmware/tree/main/tools/daikin_g3_migrate
