using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.RegularExpressions;

static int Run(string file, params string[] args) {
    using var p = new Process { StartInfo = new ProcessStartInfo(file) { UseShellExecute = false } };
    foreach (var arg in args) p.StartInfo.ArgumentList.Add(arg);
    try { p.Start(); p.WaitForExit(); return p.ExitCode; }
    catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
}

static IPAddress? DetectDefaultGateway() {
    return NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up)
        .SelectMany(n => n.GetIPProperties().GatewayAddresses)
        .Select(g => g.Address)
        .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
}
static IPAddress? DetectLanAddress(IPAddress? gateway) {
    return NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
        .OrderByDescending(a => gateway != null && a.Address.ToString().StartsWith(string.Join(".", gateway.ToString().Split('.').Take(3)) + "."))
        .Select(a => a.Address).FirstOrDefault();
}
static bool ValidIp(string value) => IPAddress.TryParse(value, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork;
static int Network(string[] options) {
    var gateway = DetectDefaultGateway();
    var local = DetectLanAddress(gateway);
    string? Option(string key) { int i = Array.IndexOf(options, key); return i >= 0 && i + 1 < options.Length ? options[i+1] : null; }
    string router = Option("--router-ip") ?? gateway?.ToString() ?? "";
    string nas = Option("--nas-ip") ?? "";
    Console.WriteLine("G3 LAN network configuration (discovery only; no device/DNS changes)");
    Console.WriteLine("Detected Windows LAN IPv4: " + (local?.ToString() ?? "not found"));
    Console.WriteLine("Detected default gateway: " + (gateway?.ToString() ?? "not found"));
    if (string.IsNullOrWhiteSpace(router)) { Console.Write("OpenWrt router LAN IPv4: "); router = Console.ReadLine()?.Trim() ?? ""; }
    if (string.IsNullOrWhiteSpace(nas)) { Console.Write("请手动输入群晖 NAS 管理 IPv4（例如 192.168.99.130）: "); nas = Console.ReadLine()?.Trim() ?? ""; }
    if (!ValidIp(router) || !ValidIp(nas)) { Console.Error.WriteLine("Invalid IPv4. Use dotted decimal addresses."); return 2; }
    Console.Write("SSH 用户名 [root]: "); string sshUser = Console.ReadLine()?.Trim() ?? ""; if (string.IsNullOrWhiteSpace(sshUser)) sshUser = "root";
    Console.Write("SSH 端口 [22]: "); string portText = Console.ReadLine()?.Trim() ?? ""; int sshPort = string.IsNullOrWhiteSpace(portText) ? 22 : int.TryParse(portText, out var parsedPort) ? parsedPort : 0; if (sshPort < 1 || sshPort > 65535) { Console.Error.WriteLine("SSH 端口无效"); return 2; }
    Console.Write("G3 专用服务 IP（未配置可留空）: "); string serviceIp = Console.ReadLine()?.Trim() ?? ""; if (!string.IsNullOrWhiteSpace(serviceIp) && !ValidIp(serviceIp)) { Console.Error.WriteLine("服务 IP 无效"); return 2; }
    var data = new { routerIp = router, nasIp = nas, sshUser, sshPort, serviceIp, windowsLanIp = local?.ToString(), detectedGateway = gateway?.ToString(), note = "NAS management IP is manual. G3 service IP must be separately assigned; do not reuse an occupied NAS address." };
    var path = Path.Combine(AppContext.BaseDirectory, "g3-network.json");
    File.WriteAllText(path, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine("Saved: " + path);
    Console.WriteLine("OpenWrt router: " + router);
    Console.WriteLine("Synology NAS: " + nas);
    Console.WriteLine("No DNS, firewall, Docker or G3 settings were changed.");
    return 0;
}

static int Check() {
    Console.WriteLine("Daikin G3 Windows host preflight (NO OTA)");
    int errors = 0;
    foreach (var (name, cmd, args) in new[] {
        ("Docker Desktop", "docker", new[]{"info", "--format", "{{.ServerVersion}}"}),
        ("WSL2", "wsl.exe", new[]{"--status"}),
        ("PowerShell", "powershell.exe", new[]{"-NoProfile", "-Command", "$PSVersionTable.PSVersion.ToString()"}) }) {
        Console.WriteLine("--- " + name);
        if (Run(cmd, args) != 0) errors++;
    }
    foreach (int port in new[] {443, 8883}) {
        try {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start(); listener.Stop();
            Console.WriteLine("TCP " + port + ": available on loopback (other interfaces not tested)");
        } catch { Console.WriteLine("TCP " + port + ": occupied or inaccessible"); errors++; }
    }
    Console.WriteLine(errors == 0 ? "Preflight passed (not a migration readiness guarantee)." : "Preflight has " + errors + " warning(s).");
    return errors == 0 ? 0 : 1;
}
static void Help() {
    Console.WriteLine("G3Windows v0.3 - factory G3 migration host helper");
    Console.WriteLine("  G3Windows.exe check       Check Docker, WSL, PowerShell and ports");
    Console.WriteLine("  G3Windows.exe guide       Print safe setup steps");
    Console.WriteLine("  G3Windows.exe network [--router-ip IP] [--nas-ip IP]  Detect router; manually enter NAS/SSH");
    Console.WriteLine("  G3Windows.exe nas-check  Test SSH and remote Docker/Compose on Synology");
    Console.WriteLine("  G3Windows.exe help        Show this help");
    Console.WriteLine("This tool DOES NOT trigger firmware updates.");
}
if (args.Length == 0) { Help(); return 0; }
switch (args[0].ToLowerInvariant()) {
    case "check": return Check();
    case "network": return Network(args.Skip(1).ToArray());
    case "nas-check": {
        var configFile = Path.Combine(AppContext.BaseDirectory, "g3-network.json");
        if (!File.Exists(configFile)) { Console.Error.WriteLine("请先运行 G3Windows.exe network"); return 2; }
        using var cfg = JsonDocument.Parse(File.ReadAllText(configFile)); var root = cfg.RootElement;
        string nas = root.GetProperty("nasIp").GetString()!; string user = root.GetProperty("sshUser").GetString()!; int port = root.GetProperty("sshPort").GetInt32();
        Console.WriteLine("SSH 连接 " + user + "@" + nas + ":" + port + "（首次连接需确认主机指纹）");
        psi.ArgumentList.Add("sh -lc 'export PATH=\\$PATH:/usr/local/bin:/var/packages/ContainerManager/target/usr/bin:/var/packages/Docker/target/usr/bin; echo DOCKER_BIN=\\$(command -v docker); docker version --format \\\"{{.Server.Version}}\\\" && docker compose version && docker ps'");
        try { using var proc = Process.Start(psi)!; proc.WaitForExit(); return proc.ExitCode; } catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }
    case "guide":
        Console.WriteLine("1. Obtain one matching verified upstream G3 migration bundle and firmware release.");
        Console.WriteLine("2. Use a LAN Docker host (Synology NAS); Windows Docker Desktop/WSL2 is not required.");
        Console.WriteLine("   Run G3Windows.exe network to detect the router and enter/confirm NAS IPv4.");
        Console.WriteLine("3. On the NAS Docker host, run upstream init.py --address <G3-SERVICE-IP> --device <WIFI-STA-MAC> --output ./lab");
        Console.WriteLine("4. Run docker compose -f ./lab/compose.json up -d; start gateway responder with --weather-code 1.");
        Console.WriteLine("5. Set OpenWrt DNS overrides for this G3 only, then provision via Android BLE.");
        Console.WriteLine("6. Confirm gateway acknowledgements before separately considering any OTA.");
        Console.WriteLine("See https://github.com/phicomm-io/iotcloud-community-firmware/tree/main/tools/daikin_g3_migrate");
        return 0;
    case "help": case "--help": case "-h": Help(); return 0;
    default: Console.Error.WriteLine("Unknown command"); Help(); return 2;
}
