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


static string BaseDir() => AppContext.BaseDirectory;
static JsonDocument? ReadConfig() {
    var file = Path.Combine(BaseDir(), "g3-network.json");
    if (!File.Exists(file)) { Console.Error.WriteLine("请先运行 network 配置网络和 NAS。"); return null; }
    return JsonDocument.Parse(File.ReadAllText(file));
}
static string Q(string value) => "'" + value.Replace("'", "'\\''") + "'";
static int SshRun(string host,string user,int port,string remote,bool interactive=false) {
    var psi = new ProcessStartInfo("ssh") { UseShellExecute = false };
    psi.ArgumentList.Add("-p"); psi.ArgumentList.Add(port.ToString());
    psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("ConnectTimeout=12");
    psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("ServerAliveInterval=15");
    psi.ArgumentList.Add(user+"@"+host); psi.ArgumentList.Add(remote);
    try { using var proc=Process.Start(psi)!; proc.WaitForExit(); return proc.ExitCode; }
    catch(Exception ex){Console.Error.WriteLine("SSH 执行失败: "+ex.Message);return 1;}
}
static int ScpRun(string host,string user,int port,string source,string target) {
    var psi=new ProcessStartInfo("scp"){UseShellExecute=false};
    psi.ArgumentList.Add("-P");psi.ArgumentList.Add(port.ToString());
    psi.ArgumentList.Add("-r");psi.ArgumentList.Add(source);
    psi.ArgumentList.Add(user+"@"+host+":"+target);
    try{using var proc=Process.Start(psi)!;proc.WaitForExit();return proc.ExitCode;}
    catch(Exception ex){Console.Error.WriteLine("SCP 执行失败: "+ex.Message);return 1;}
}
static int NasDeploy() {
    using var cfg=ReadConfig(); if(cfg==null)return 2;
    var c=cfg.RootElement; string nas=c.GetProperty("nasIp").GetString()!,user=c.GetProperty("sshUser").GetString()!;
    int port=c.GetProperty("sshPort").GetInt32();
    Console.Write("G3 实际 Wi-Fi STA MAC（12位十六进制，不是 BLE MAC）: "); string sta=(Console.ReadLine()??"").Replace(":","").Replace("-","").ToUpperInvariant();
    if(!Regex.IsMatch(sta,"^[0-9A-F]{12}$")){Console.Error.WriteLine("STA MAC 格式错误");return 2;}
    Console.Write("G3 专用服务 IP（必须已配置在 NAS 网卡上，不能与 NAS 管理 IP 相同）: "); string ip=Console.ReadLine()?.Trim()??"";
    if(!ValidIp(ip)||ip==nas){Console.Error.WriteLine("服务 IP 无效，必须是 NAS 已配置的独立 IPv4");return 2;}
    var bundle=Path.Combine(BaseDir(),"upstream-g3-migrate");
    foreach(var name in new[]{"init.py","local_ota_server.py","local_gateway_responder.py","trigger.py"})
        if(!File.Exists(Path.Combine(bundle,name))){Console.Error.WriteLine("缺少随程序发布的上游文件: "+name);return 3;}
    Console.WriteLine("部署前检查 NAS 服务 IP 和端口。此步骤只部署无 OTA 的初始兼容服务，不配置 DNS、不发送配网和升级命令。");
    Console.Write("确认继续部署到 "+nas+"，服务 IP "+ip+"？输入 YES: ");
    if(Console.ReadLine()!="YES")return 1;
    string root="/volume1/docker/g3-migration";
    string remoteCheck=$"set -e; ip -o -4 addr show | grep -F ' {ip}/' >/dev/null || {{ echo SERVICE_IP_NOT_ASSIGNED; exit 20; }}; for p in 443 8883; do if ss -lnt 2>/dev/null | grep -E '[:.]$p[[:space:]]' | grep -F '{ip}:' >/dev/null; then echo PORT_OCCUPIED_$p; exit 21; fi; done; mkdir -p {root}/upstream-g3-migrate";
    if(SshRun(nas,user,port,"sh -lc "+Q(remoteCheck))!=0){Console.Error.WriteLine("NAS 服务 IP 未配置或端口被占用；未部署。请先为 NAS 配置独立服务 IP。");return 4;}
    if(ScpRun(nas,user,port,bundle,root)!=0)return 5;
    string prep=$"set -e; D=/usr/local/bin/docker; cd {root}; $D run --rm -v /volume1:/volume1 -w {root}/upstream-g3-migrate python:3.12-slim sh -lc 'apt-get update -qq && apt-get install -y -qq openssl >/dev/null && python init.py --address {ip} --device {sta} --output /volume1/docker/g3-migration/lab'; $D compose -f {root}/lab/compose.json up -d";
    if(SshRun(nas,user,port,"sh -lc "+Q(prep))!=0){Console.Error.WriteLine("上游配置生成或 Compose 启动失败。保留目录和日志供诊断；未修改 OpenWrt。");return 6;}
    Console.WriteLine("初始 HTTPS/MQTT 兼容服务已部署。当前为 NO-OTA 模式。下一步需验证服务健康，再配置设备专用 DNS 和 BLE 网关关联。");
    return 0;
}
static int NasStatus() {
    using var cfg=ReadConfig();if(cfg==null)return 2;var c=cfg.RootElement;
    string nas=c.GetProperty("nasIp").GetString()!,user=c.GetProperty("sshUser").GetString()!;int port=c.GetProperty("sshPort").GetInt32();
    string root="/volume1/docker/g3-migration";
    string cmd=$"set -e; D=/usr/local/bin/docker; $D compose -f {root}/lab/compose.json ps; $D ps --filter name=iotcloud-g3-migration --format 'table {{.Names}}\\t{{.Status}}'; echo '--- TCP LISTEN ---'; ss -lnt | grep -E '(:443|:8883)' || true";
    return SshRun(nas,user,port,"sh -lc "+Q(cmd));
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
    Console.WriteLine("  G3Windows.exe nas-deploy  Deploy upstream NO-OTA HTTPS/MQTT stack to Synology");
    Console.WriteLine("  G3Windows.exe nas-status  Check deployed G3 migration services");
    Console.WriteLine("  G3Windows.exe help        Show this help");
    Console.WriteLine("This tool DOES NOT trigger firmware updates.");
}
if (args.Length == 0) { Help(); return 0; }
switch (args[0].ToLowerInvariant()) {
    case "check": return Check();
    case "network": return Network(args.Skip(1).ToArray());
    case "nas-deploy": return NasDeploy();
    case "nas-status": return NasStatus();
    case "nas-check": {
        var configFile = Path.Combine(AppContext.BaseDirectory, "g3-network.json");
        if (!File.Exists(configFile)) { Console.Error.WriteLine("请先运行 G3Windows.exe network"); return 2; }
        using var cfg = JsonDocument.Parse(File.ReadAllText(configFile)); var root = cfg.RootElement;
        string nas = root.GetProperty("nasIp").GetString()!; string user = root.GetProperty("sshUser").GetString()!; int port = root.GetProperty("sshPort").GetInt32();
        Console.WriteLine("SSH 连接 " + user + "@" + nas + ":" + port + "（首次连接需确认主机指纹）");
        var psi = new ProcessStartInfo("ssh") { UseShellExecute = false };
        psi.ArgumentList.Add("-p"); psi.ArgumentList.Add(port.ToString());
        psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("ConnectTimeout=8");
        psi.ArgumentList.Add(user + "@" + nas);
        psi.ArgumentList.Add("sh -lc 'D=/usr/local/bin/docker; [ -x \"$D\" ] || D=/var/packages/ContainerManager/target/usr/bin/docker; [ -x \"$D\" ] || D=/var/packages/Docker/target/usr/bin/docker; [ -x \"$D\" ] || { echo DOCKER_NOT_FOUND; exit 127; }; echo DOCKER_BIN=$D; \"$D\" version --format \"{{.Server.Version}}\" && \"$D\" compose version && \"$D\" ps'");
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
