# G3 Windows Manager + Linux migration host / G3 配网与 OTA 管理工具

Windows x64 self-contained EXE. Windows does not need Docker, WSL2 or Bluetooth. The Linux computer is the always-on LAN service host and runs the **upstream-compatible temporary HTTPS service and Mosquitto MQTT broker** in Docker Compose. The Android phone is the only BLE provisioning client.

> Scope: stock-screen BRY88BBA15G / SENSOR3_ESP32 wireless migration. The first OTA updates the ESP32 application only and preserves the factory screen MCU. Do not use G1/G2, signed OTA, full-flash images or screen MCU images for this migration step.

## Device roles

- **Android:** BLE scan/connect; short-press the G3 rear network button, then send 2.4 GHz Wi-Fi credentials and virtual gateway identity.
- **Windows:** configure the Linux SSH target, deploy/check services, run the bounded gateway responder, verify firmware SHA256, enable OTA and send the one-shot OTA command.
- **Ordinary Linux computer:** Docker Engine + Compose, Python 3 and OpenSSL. Runs the upstream-generated HTTPS service on TCP 443 and temporary Mosquitto TLS broker on TCP 8883. No Bluetooth adapter is required.

## Windows commands

```bat
G3Windows.exe network
G3Windows.exe linux-check
G3Windows.exe linux-deploy
G3Windows.exe linux-status
G3Windows.exe dns-guide
G3Windows.exe responder
G3Windows.exe linux-logs
G3Windows.exe ota-prepare
G3Windows.exe ota-trigger
G3Windows.exe linux-stop
```

Run `network` once. Enter the Linux host LAN IPv4, SSH username and port (usually 22). The OpenWrt router IPv4 is detected as a hint. No MQTT host, username or password is needed: the migration bundle creates the temporary Mosquitto service required by the stock G3 migration protocol.

On Debian/Ubuntu Linux, install `openssh-server`, `python3`, `openssl`, Docker Engine and the Docker Compose plugin. Example base packages: `sudo apt update && sudo apt install -y openssh-server python3 openssl`. Install Docker Engine + Compose from Docker's official repository. Add the SSH user to the `docker` group (`sudo usermod -aG docker USER`) and log out/in so the group applies. The SSH user must run `docker`, `docker compose` and bind TCP 443/8883 without an interactive sudo prompt. Keep the Linux host wired, awake and on the same LAN; reserve its IPv4 in DHCP.

## Deployment flow

1. Verify the device is **BRY88BBA15G / SENSOR3_ESP32**. Obtain its actual Wi-Fi STA MAC from the DHCP lease/device label. Do not use its BLE address or the virtual gateway MAC.
2. Download the complete current `daikin_G3_migration.tar.gz` and matching `daikin_BRY88BBA15G.ota.bin.raw` from the [upstream G3 downloads](https://phicomm-io.github.io/#daikin-g3-downloads). Verify the release `SHA256SUMS`. Never mix scripts and firmware from different releases.
3. Confirm Linux host TCP 443 and 8883 are free and reachable from the G3. On Windows run `G3Windows.exe network`, then `linux-check`.
4. Run `linux-deploy`. This runs the pinned upstream `init.py` on Linux and starts its generated Compose stack. Initial state is **NO-OTA**. It runs both HTTPS and Mosquitto locally on Linux; it does not use an external Broker.
5. Configure OpenWrt DNS overrides **for this G3 only** for all three factory names, resolving them to the Linux host IP:
   - `newlifemulti.daikin-china.com.cn`
   - `newlifemulti-cdh.daikin-china.com.cn`
   - `newlifemulti-cdh-core.daikin-china.com.cn`
   Do not use a global DNS rewrite or redirect other LAN devices.
6. In Android, short-press the recessed rear network button (do not long-press/reset), scan for the exact `SENSOR3_…` BLE device, and send the 2.4 GHz SSID/password plus Linux host IP as `gateway_ip` and `020000000001` as the virtual `gateway_mac`. The STA MAC remains the real device identity. Bluetooth closes after provisioning; short-press again before retrying.
7. Confirm the device obtained Wi-Fi/DHCP and its STA MAC. Run `responder` in Windows and leave it active. It resolves the actual MQTT Compose container ID (`docker compose ps -q mqtt`) and passes it explicitly to the upstream responder. This is important: upstream `init.py` does not assign a fixed `container_name`, while the responder's standalone default is `iotcloud-g3-migration`; relying on that default can silently fail against Compose's generated container name. Confirm all five events: `snapshot_upload`, `get_weather`, `login_ipbox`, `get_filter_sensor3`, `get_error_sensor3`. Run only one responder for this device. If running it manually, pass `--container "$(docker compose -f compose.json ps -q mqtt)"` because Compose may generate a container name different from the responder's default. The temporary weather response is synthetic, not a real weather reading.
8. Inspect `linux-logs`. Do not proceed if the five gateway acknowledgements or HTTPS login are missing.
9. Only then run `ota-prepare`: select the matching raw G3 ESP32 app, enter its SHA256 and the tested temporary target label `V99.00.00`. The tool independently verifies the SHA256 and the upstream `enable.py` validates the ESP32/G3 application header before enabling the offer. The broker CA/key generated by `init.py` is preserved.
10. Run `ota-trigger` only after the explicit confirmation. It invokes the upstream `trigger.py` to publish exactly one non-retained ESP32-only OTA command. Keep power connected. Inspect HTTPS logs for `artifact-start` and `artifact` (HTTP 200 and full byte count), then verify reboot, DHCP, device WebUI, firmware version and sensor readings.
11. Restore the original per-device DNS settings. Run `linux-stop` to stop/remove only the temporary migration containers. Keep backups separately.

## Security boundary

The upstream migration Mosquitto config intentionally uses anonymous TLS for a temporary compatibility broker. It is suitable only on a trusted, isolated LAN for the migration window. Do not port-forward 443/8883 to the public Internet; firewall the services to the LAN/G3 as appropriate; do not reuse this broker as a permanent IoTCloud broker. Stop the stack and remove the temporary lab directory after migration. The generated CA/key are per-lab files.

## What is and is not verified

CI verifies the Windows EXE build, bundled upstream Python syntax and packaging. It does not prove live G3 BLE provisioning, Wi-Fi association, gateway state-machine progression, firmware download or reboot. Those require the physical G3. The screen MCU is not updated by this first ESP32 OTA; after successful migration, use the device WebUI and the matching release's screen resource procedure.

Upstream migration implementation and license are pinned in the bundled `upstream-g3-migrate/UPSTREAM-COMMIT.txt` and `UPSTREAM-LICENSE`. See the [upstream migration guide](https://github.com/phicomm-io/iotcloud-community-firmware/tree/main/tools/daikin_g3_migrate).
