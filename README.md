# G3 Windows Migration Manager — Docker/NAS mode

Self-contained Windows x64 executable. Windows does not need Docker Desktop or WSL2. NAS SSH access, Docker Engine/Compose, and a separately assigned G3 service IP are required.

## Commands

```bat
G3Windows.exe network
G3Windows.exe nas-check
G3Windows.exe nas-deploy
G3Windows.exe nas-status
G3Windows.exe dns-guide
G3Windows.exe responder
G3Windows.exe nas-logs
G3Windows.exe ota-prepare
G3Windows.exe ota-trigger
G3Windows.exe nas-stop
```

**Current implementation:** network settings (NAS address manually entered), remote NAS diagnostics, Docker no-update service deployment, status/log inspection, timed upstream gateway responder, SHA256-verified G3 ESP32 raw application OTA offer, single-shot ESP32-only OTA trigger and isolated Compose stop.

**Not yet automated:** per-device OpenWrt DNS configuration/rollback and Android BLE app handoff. The program provides DNS guidance but does not make unsafe global DNS changes. BLE provisioning uses the separate Android app. Firmware migration is not confirmed until real G3 acknowledgements, HTTPS artifact transfer and post-reboot version checks succeed.

## Preparation

1. Verify model BRY88BBA15G / SENSOR3_ESP32 and actual Wi-Fi STA MAC (not BLE address).
2. Download matching upstream migration bundle and G3 raw ESP32 firmware from the same compatible release; verify its published SHA256SUMS.
3. On the NAS, assign a **dedicated** LAN IPv4 distinct from the management address. Ensure TCP 443 and 8883 are free on this IP.
4. Run `network`, manually enter NAS management IPv4, SSH user and port; run `nas-check`.
5. Run `nas-deploy` to create an initial no-update stack in `/volume1/docker/g3-migration`; then `nas-status` and `nas-logs`.
6. Use `dns-guide` to configure **device-specific** DNS for the three Daikin hostnames. Do not redirect all LAN clients. Ensure the G3 uses the correct DNS via DHCP.
7. Short-press the recessed G3 network button and use the Android BLE tool to configure Wi-Fi and virtual gateway 020000000001, pointing to the G3 service IP.
8. Run `responder` and confirm acknowledgements for snapshot_upload, get_weather, login_ipbox, get_filter_sensor3 and get_error_sensor3.
9. Only after confirming identity, release consistency, SHA256 and gateway acknowledgements, run `ota-prepare`. Confirm HTTPS logs.
10. `ota-trigger` requires a second explicit confirmation and sends one non-retained ESP32-only command. Keep power connected and verify HTTPS artifact-start/artifact full transfer, reboot, WebUI firmware version and sensors.
11. Restore original per-device DNS, run `nas-stop`, and clean up temporary credentials/files. Screen firmware installation is a separate operation from the community WebUI.

**Important:** The program currently pins the upstream migration scripts to commit `1c9d2f373f0a9248d2360e03cf2a385c28532571`. Check compatibility with the firmware release before upgrading; a successful SHA256 comparison against a user-provided digest alone does not establish that it matches the pinned script release.

Upstream reference: https://github.com/phicomm-io/iotcloud-community-firmware/tree/main/tools/daikin_g3_migrate

Do not publish Wi-Fi passwords, factory credentials, private lab certificates, or SSH keys.
