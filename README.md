# Daikin G3 Migration Manager — Windows

Windows x64 self-contained .NET 8 application for managing the Daikin G3 migration workflow through a Synology NAS Docker host.

## Current implemented commands

- `network`: auto-detect Windows IPv4/default gateway; manually enter OpenWrt, NAS management IP, SSH user/port and optional G3 service IP.
- `nas-check`: SSH to Synology and verify Docker Engine/Compose.
- `nas-deploy`: deploy the pinned upstream initial NO-OTA HTTPS/MQTT compatibility stack to Synology.
- `nas-status`: inspect the deployed Compose stack and listener state.

Windows does not require Docker Desktop or WSL2. Windows OpenSSH Client (ssh/scp) is required.

## First use

```bat
G3Windows.exe network
G3Windows.exe nas-check
```

Assign a dedicated service IPv4 to the NAS interface before deployment. It must be different from the NAS management IP and must be reachable by the G3. The NAS management address in this user's tested LAN is 192.168.99.130 and OpenWrt is 192.168.99.1, but the EXE asks for values instead of hardcoding them.

Then run:

```bat
G3Windows.exe nas-deploy
G3Windows.exe nas-status
```

The deploy command asks for the actual Wi-Fi STA MAC (not BLE MAC) and dedicated service IP, verifies that the service IP is assigned to the NAS, checks TCP 443/8883 on that address, uploads the bundled upstream scripts, generates the upstream lab and starts Docker Compose.

## Safety boundaries

This build only deploys the upstream initial compatibility stack with NO OTA enabled. It does not change OpenWrt DNS, send BLE provisioning data, enable a firmware offer, or publish an OTA command. Those require explicit later steps and successful G3 gateway acknowledgements.

Never use the BLE address as the Wi-Fi STA MAC. The upstream instructions target BRY88BBA15G / SENSOR3_ESP32 and require a matching release bundle and firmware. Verify the actual hardware model and release checksums before any firmware action.

## Upstream source

Repository: https://github.com/phicomm-io/iotcloud-community-firmware
Migration directory: tools/daikin_g3_migrate
Pinned commit: 1c9d2f373f0a9248d2360e03cf2a385c28532571

Upstream README requires:
- a dedicated LAN service host with TCP 443 and 8883 available;
- the real device Wi-Fi STA MAC;
- per-device DNS overrides for three Daikin domains;
- the gateway responder to acknowledge snapshot, weather, gateway login, filter and error requests;
- explicit separation between initial no-update service and later ESP32 OTA enable/trigger.

See the bundled upstream README and license/source notices.

## Not yet claimed as complete

The Windows remote deployment and service inspection are implemented. Actual OpenWrt DNS automation, Android BLE handoff, live G3 gateway acknowledgement monitoring, OTA artifact validation/offer/trigger, and post-flash verification remain gated implementation and hardware-validation stages. A green Windows CI build is not evidence that a G3 device has migrated successfully.
