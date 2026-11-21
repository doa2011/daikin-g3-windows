# G3 Windows Migration Manager — HTTPS on NAS, external MQTT

Windows x64 self-contained EXE. Windows does not need Docker Desktop or WSL2. The NAS runs the G3 HTTPS compatibility/OTA service plus a small **TCP passthrough proxy** on port 8883. The proxy forwards encrypted bytes to the user-supplied existing MQTT broker; it is not an MQTT Broker and does not terminate MQTT/TLS. The program does **not** deploy, start, stop, or modify an MQTT broker.

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

## Runtime configuration

Run `network` and enter:
- OpenWrt router IPv4
- NAS management IPv4, SSH user and port
- HTTPS service IPv4 (defaults to NAS management IPv4; no extra NAS IP is required if TCP 443 is free)
- Existing MQTT host/IP and port
- MQTT TLS confirmation (TLS is required by the stock G3 migration flow)
- MQTT username (optional)
- Public MQTT CA PEM file path

The MQTT password is requested only when running `responder` or `ota-trigger`; it is not saved in `g3-network.json`. The MQTT CA is copied to the NAS migration directory because the G3 HTTPS compatibility service returns it through the stock broker-CA endpoint.

The Windows program uses the configured external MQTT endpoint for gateway acknowledgements and the one-shot OTA publish. The NAS exposes HTTPS on TCP 443 and a raw TCP passthrough on TCP 8883, forwarding to the configured broker host/port. All three per-device factory DNS overrides point to the NAS IP, so HTTPS is served locally and MQTT TLS is forwarded without inspection.

## Important protocol compatibility boundary

The upstream G3 migration flow obtains broker CA information and a device token through HTTPS, then the G3 itself connects to the broker using its factory device identity. A user-supplied MQTT host/port/CA and credentials let the Windows-side responder/OTA publisher connect to that broker. **This alone cannot make an arbitrary broker accept the G3's factory device identity.** The existing broker must already be configured to accept the G3 device connection/authentication pattern and required topics. This tool does not rewrite broker ACLs, users, authentication plugins, or TLS listeners.

For TLS, supply the broker's public CA certificate in PEM format. Do not supply a private key. TLS is enforced for the stock G3 migration workflow; non-TLS MQTT is not supported by this mode.

## Deployment and validation

1. Verify model BRY88BBA15G / SENSOR3_ESP32 and actual Wi-Fi STA MAC (not BLE MAC).
2. Obtain the matching upstream migration bundle and G3 raw ESP32 application from the same compatible release; verify the upstream published SHA256SUMS.
3. Confirm NAS TCP 443 is free on the chosen HTTPS address. The existing NAS management IP can be used; no additional IP is required if port 443 is available.
4. Run `network`; enter all HTTPS and external MQTT settings. Run `nas-check`.
5. Run `nas-deploy`. It creates an HTTPS service and a TCP passthrough proxy under `/volume1/docker/g3-migration`, removes the generated local MQTT Broker service before Compose starts, and injects the external broker CA into the HTTPS service. Verify with `nas-status` and `nas-logs`.
6. Configure device-specific DNS in OpenWrt for the three Daikin HTTPS hostnames to the NAS HTTPS IP. Do not redirect the whole LAN. The three factory names all resolve to the NAS HTTPS IP; its TCP 8883 proxy forwards the TLS stream to the user-supplied external broker.
7. Install the companion Android APK. Short-press the recessed G3 network button, scan/select the BLE device, enter 2.4 GHz Wi-Fi credentials and the HTTPS compatibility service IP, keep virtual gateway MAC `020000000001`, and send provisioning. The Android app does not flash firmware.
8. Verify the actual Wi-Fi STA MAC in DHCP and run `responder`. It connects to the configured external MQTT broker and sends bounded gateway acknowledgements. Confirm the expected request/response topics.
9. Only after confirming device identity, release consistency, SHA256, broker CA, broker device-auth compatibility and gateway acknowledgements, run `ota-prepare`. Confirm HTTPS logs.
10. Run `ota-trigger` only after the explicit confirmation prompt. It publishes exactly one ESP32-only OTA command to the configured external MQTT broker. Keep power connected; verify artifact-start/artifact transfer logs, reboot, WebUI firmware version and sensors.
11. Restore original device-specific DNS and run `nas-stop`. This stops only the HTTPS Compose service; it does not touch the external MQTT broker.

## What is not claimed as verified

CI compilation does not prove live G3 compatibility. Real-device acceptance requires BLE provisioning, HTTPS login, G3 connection to the external MQTT broker, gateway acknowledgements, full firmware artifact transfer, reboot, and post-upgrade version/sensor checks. Screen firmware is separate from the community ESP32 application.

Upstream source: https://github.com/phicomm-io/iotcloud-community-firmware/tree/main/tools/daikin_g3_migrate

Never publish Wi-Fi passwords, MQTT passwords, factory credentials, private CA keys, or SSH keys.
