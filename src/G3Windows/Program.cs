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
static bool ValidHost(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 253 && Uri.CheckHostName(value) != UriHostNameType.Unknown;
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
    Console.Write("G3 HTTPS 服务 IPv4（默认使用 NAS 管理 IP）: "); string serviceIp = Console.ReadLine()?.Trim() ?? ""; if (string.IsNullOrWhiteSpace(serviceIp)) serviceIp = nas; if (!ValidIp(serviceIp)) { Console.Error.WriteLine("HTTPS 服务 IP 无效"); return 2; }
    Console.Write("现有 MQTT 服务器地址（IP 或域名）: "); string mqttHost = Console.ReadLine()?.Trim() ?? ""; if (!ValidHost(mqttHost)) { Console.Error.WriteLine("MQTT 主机地址无效"); return 2; }
    Console.Write("现有 MQTT 端口 [8883]: "); string mqttPortText = Console.ReadLine()?.Trim() ?? ""; int mqttPort = string.IsNullOrWhiteSpace(mqttPortText) ? 8883 : int.TryParse(mqttPortText,out var mp) ? mp : 0; if (mqttPort < 1 || mqttPort > 65535) { Console.Error.WriteLine("MQTT 端口无效"); return 2; }
    Console.Write("MQTT TLS（原厂 G3 协议要求 TLS，输入 Y 确认）: "); string tlsText = Console.ReadLine()?.Trim() ?? ""; bool mqttTls = tlsText.Equals("Y",StringComparison.OrdinalIgnoreCase); if(!mqttTls){Console.Error.WriteLine("G3 原厂 MQTT TLS 为必需项");return 2;}
    Console.Write("MQTT 用户名（可留空）: "); string mqttUser = Console.ReadLine() ?? "";
    Console.Write("MQTT CA PEM 文件路径（可留空使用系统信任链）: "); string mqttCa = (Console.ReadLine() ?? "").Trim();
    if (!string.IsNullOrWhiteSpace(mqttCa) && !File.Exists(mqttCa)) { Console.Error.WriteLine("找不到 MQTT CA 文件"); return 2; }
    var data = new { routerIp = router, nasIp = nas, sshUser, sshPort, serviceIp, mqttHost, mqttPort, mqttTls, mqttUser, mqttCa, windowsLanIp = local?.ToString(), detectedGateway = gateway?.ToString(), note = "NAS runs HTTPS only; MQTT is a user-supplied external broker. Password is never persisted." };
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
    string ip=c.GetProperty("serviceIp").GetString()??nas;
    string mqttHost=c.GetProperty("mqttHost").GetString()??"";
    int mqttPort=c.GetProperty("mqttPort").GetInt32();
    bool mqttTls=c.GetProperty("mqttTls").GetBoolean();
    string mqttCa=c.GetProperty("mqttCa").GetString()??"";
    Console.WriteLine("NAS 只运行 HTTPS 服务和 MQTT TCP 转发器；实际 MQTT Broker 为外部服务器 "+mqttHost+":"+mqttPort+"，不会在 NAS 安装 Broker。");
    if(!mqttTls){Console.Error.WriteLine("原厂 G3 MQTT 链路要求 TLS；本迁移模式不支持关闭 TLS。");return 2;}
    if(string.IsNullOrWhiteSpace(mqttCa) || !File.Exists(mqttCa)){Console.Error.WriteLine("必须提供现有 MQTT Broker 的 CA PEM 文件，供 G3 HTTPS 证书接口下发。");return 2;}
    var bundle=Path.Combine(BaseDir(),"upstream-g3-migrate");
    foreach(var name in new[]{"init.py","enable.py","local_ota_server.py","local_gateway_responder.py","trigger.py"})
        if(!File.Exists(Path.Combine(bundle,name))){Console.Error.WriteLine("缺少随程序发布的上游文件: "+name);return 3;}
    Console.WriteLine("部署前检查 NAS 服务 IP 和端口。此步骤只部署无 OTA 的初始兼容服务，不配置 DNS、不发送配网和升级命令。");
    Console.Write("确认继续部署到 "+nas+"，服务 IP "+ip+"？输入 YES: ");
    if(Console.ReadLine()!="YES")return 1;
    string root="/volume1/docker/g3-migration";
    string remoteCheck=$"set -e; ip -o -4 addr show | grep -F ' {ip}/' >/dev/null || {{ echo HTTPS_SERVICE_IP_NOT_ASSIGNED; exit 20; }}; for p in 443 8883; do if ss -lnt 2>/dev/null | grep -E '[:.]$p[[:space:]]' | grep -F '{ip}:' >/dev/null; then echo PORT_OCCUPIED_$p; exit 21; fi; done; mkdir -p {root}/upstream-g3-migrate";
    if(SshRun(nas,user,port,"sh -lc "+Q(remoteCheck))!=0){Console.Error.WriteLine("HTTPS 服务 IP 未配置或 443 端口被占用；未部署。可直接使用 NAS 管理 IP，无需额外 IP。");return 4;}
    if(ScpRun(nas,user,port,bundle,root)!=0)return 5;
    if(ScpRun(nas,user,port,mqttCa,root+"/mqtt-ca.pem")!=0)return 5;
    string prep=$"set -e; D=/usr/local/bin/docker; cd {root}; $D run --rm -v {root}:/workspace -w /workspace/upstream-g3-migrate python:3.12-slim sh -lc 'apt-get update -qq && apt-get install -y -qq openssl >/dev/null && python init.py --address {ip} --device {sta} --output /workspace/lab'; cp {root}/mqtt-ca.pem {root}/lab/mqtt-ca.pem; $D run --rm -v {root}:/workspace python:3.12-slim python -c \"import json; p='/workspace/lab/compose.json'; d=json.load(open(p)); d['services'].pop('mqtt',None); c=d['services']['https']['command']; i=c.index('--broker-ca'); c[i+1]='/lab/mqtt-ca.pem'; d['services']['mqtt-tcp-proxy']={{'image':'haproxy:3.0-alpine','ports':['{ip}:8883:8883'],'volumes':['{root}/lab/haproxy.cfg:/usr/local/etc/haproxy/haproxy.cfg:ro'],'command':['haproxy','-W','-db','-f','/usr/local/etc/haproxy/haproxy.cfg']}}; open(p,'w').write(json.dumps(d,indent=2))\"; printf 'global\\n  log stdout format raw local0\\ndefaults\\n  mode tcp\\n  timeout connect 10s\\n  timeout client 1h\\n  timeout server 1h\\nfrontend g3_mqtt\\n  bind *:8883\\n  default_backend external_mqtt\\nbackend external_mqtt\\n  server broker {mqttHost}:{mqttPort} check\\n' > {root}/lab/haproxy.cfg; sed -i 's@/workspace/lab@{root}/lab@g' {root}/lab/compose.json; $D compose -f {root}/lab/compose.json up -d";

    if(SshRun(nas,user,port,"sh -lc "+Q(prep))!=0){Console.Error.WriteLine("上游配置生成或 Compose 启动失败。保留目录和日志供诊断；未修改 OpenWrt。");return 6;}
    Console.WriteLine("初始 HTTPS 服务已部署（NO-OTA）。未在 NAS 部署 MQTT Broker；设备 MQTT 由你填写的外部服务器提供。仍需验证外部 Broker 的 G3 TLS/身份认证兼容性。");
    return 0;
}
static int NasStatus() {
    using var cfg=ReadConfig();if(cfg==null)return 2;var c=cfg.RootElement;
    string nas=c.GetProperty("nasIp").GetString()!,user=c.GetProperty("sshUser").GetString()!;int port=c.GetProperty("sshPort").GetInt32();
    string root="/volume1/docker/g3-migration";
    string cmd=$"set -e; D=/usr/local/bin/docker; $D compose -f {root}/lab/compose.json ps; $D ps --filter name=iotcloud-g3-migration --format 'table {{.Names}}\\t{{.Status}}'; echo '--- TCP LISTEN ---'; ss -lnt | grep -E '(:443|:8883)' || true";
    return SshRun(nas,user,port,"sh -lc "+Q(cmd));
}


static (string nas,string user,int port)? Nas() {
    using var cfg=ReadConfig(); if(cfg==null)return null;
    var c=cfg.RootElement;
    return (c.GetProperty("nasIp").GetString()!,c.GetProperty("sshUser").GetString()!,c.GetProperty("sshPort").GetInt32());
}
static string AskMac() {
    Console.Write("G3 Wi-Fi STA MAC（不是 BLE MAC）: ");
    return (Console.ReadLine()??"").Replace(":","").Replace("-","").ToUpperInvariant();
}
static int Responder() {
    var n=Nas();if(n==null)return 2;
    using var cfg=ReadConfig();if(cfg==null)return 2;var c=cfg.RootElement;
    string host=c.GetProperty("mqttHost").GetString()??"";
    int mqttPort=c.GetProperty("mqttPort").GetInt32();
    string user=c.GetProperty("mqttUser").GetString()??"";
    string ca=c.GetProperty("mqttCa").GetString()??"";
    string password="";
    if(!string.IsNullOrWhiteSpace(user)){Console.Write("现有 MQTT 用户密码（仅本次使用）: ");password=Console.ReadLine()??"";}
    string mac=AskMac();if(!Regex.IsMatch(mac,"^[0-9A-F]{12}$"))return 2;
    Console.Write("应答时长（秒，60-3600，默认 600）: ");string v=Console.ReadLine()?.Trim()??"";
    int seconds=string.IsNullOrEmpty(v)?600:int.TryParse(v,out int parsed)?parsed:0;
    if(seconds<60||seconds>3600)return 2;
    const string root="/volume1/docker/g3-migration";
    string cmd="/usr/local/bin/docker run --rm --network host -v "+root+"/upstream-g3-migrate:/scripts:ro -v "+root+"/lab:/lab:ro python:3.12-slim sh -lc "+Q("pip install -q paho-mqtt==2.1.0 && python /scripts/local_gateway_responder.py --device "+mac+" --gateway 020000000001 --host "+host+" --port "+mqttPort+" --cafile /lab/mqtt-ca.pem --seconds "+seconds+" --weather-code 1 --empty-gateway"+(string.IsNullOrWhiteSpace(user)?"":" --username "+Q(user)+" --password "+Q(password)));
    Console.WriteLine("通过外部 MQTT "+host+":"+mqttPort+" 运行限时 G3 网关应答；NAS 不运行 Broker。");
    return SshRun(n.Value.nas,n.Value.user,n.Value.port,"sh -lc "+Q(cmd));
}
static int OtaPrepare() {
    var n=Nas();if(n==null)return 2;
    string mac=AskMac();if(!Regex.IsMatch(mac,"^[0-9A-F]{12}$"))return 2;
    Console.Write("本地 G3 raw ESP32 应用完整路径: ");string file=(Console.ReadLine()??"").Trim().Trim('"');
    if(!File.Exists(file)||!file.EndsWith(".ota.bin.raw",StringComparison.OrdinalIgnoreCase)){Console.Error.WriteLine("仅接受 .ota.bin.raw 文件");return 2;}
    Console.Write("请输入匹配发布的 SHA256（64位）: ");string expected=(Console.ReadLine()??"").Trim();
    if(!Regex.IsMatch(expected,"^[a-fA-F0-9]{64}$"))return 2;
    using var stream=File.OpenRead(file);
    string actual=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    if(!actual.Equals(expected,StringComparison.OrdinalIgnoreCase)){Console.Error.WriteLine("SHA256 不匹配，拒绝升级");return 3;}
    Console.Write("目标 OTA 版本标签 [V99.00.00]: ");string version=Console.ReadLine()?.Trim()??"";
    if(version.Length==0)version="V99.00.00";
    if(!Regex.IsMatch(version,"^[A-Za-z0-9_.-]{1,31}$"))return 2;
    Console.WriteLine("必须确认同一发布的迁移包与固件、G3 型号、五项网关应答和设备供电稳定。");
    Console.Write("已逐项确认？输入 ENABLE: ");if(Console.ReadLine()!="ENABLE")return 1;
    const string root="/volume1/docker/g3-migration";
    string target=root+"/g3-verified.ota.bin.raw";
    if(ScpRun(n.Value.nas,n.Value.user,n.Value.port,file,target)!=0)return 4;
    string command=$"set -e; D=/usr/local/bin/docker; $D run --rm -v {root}:/workspace -w /workspace/upstream-g3-migrate python:3.12-slim python enable.py --directory /workspace/lab --device {mac} --firmware /workspace/g3-verified.ota.bin.raw --version {version}; $D compose -f {root}/lab/compose.json up -d --no-deps --force-recreate https";
    return SshRun(n.Value.nas,n.Value.user,n.Value.port,"sh -lc "+Q(command));
}
static int OtaTrigger() {
    var n=Nas();if(n==null)return 2;
    using var cfg=ReadConfig();if(cfg==null)return 2;var c=cfg.RootElement;
    string mqttHost=c.GetProperty("mqttHost").GetString()??"";
    int mqttPort=c.GetProperty("mqttPort").GetInt32();
    string mqttUser=c.GetProperty("mqttUser").GetString()??"";
    string mqttCa=c.GetProperty("mqttCa").GetString()??"";
    string password="";
    if(!string.IsNullOrWhiteSpace(mqttUser)){Console.Write("现有 MQTT 用户密码（仅本次使用，不写入配置）: ");password=Console.ReadLine()??"";}
    string mac=AskMac();if(!Regex.IsMatch(mac,"^[0-9A-F]{12}$"))return 2;
    Console.Write("OTA 目标版本 [V99.00.00]: ");string version=Console.ReadLine()?.Trim()??"";
    if(version.Length==0)version="V99.00.00";
    if(!Regex.IsMatch(version,"^[A-Za-z0-9_.-]{1,31}$"))return 2;
    Console.Write("确认五项网关应答、HTTPS 服务正常且已完成 OTA Prepare。发送一次不可撤销的升级命令请输入 TRIGGER: ");
    if(Console.ReadLine()!="TRIGGER")return 1;
    string token="iotcloud-"+Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
    var body=JsonSerializer.Serialize(new {client_token=token,data=new {upgrade_method=1,upgrade_info=new[]{new{device_type="SENSOR3_ESP32",to_version=version}}}});
    string topic="SENSOR/"+mac+"/mqtt/service/ota";
    string args=" --rm --network host -v /volume1/docker/g3-migration/lab:/lab:ro eclipse-mosquitto:2.0.22 mosquitto_pub -h "+Q(mqttHost)+" -p "+mqttPort+" --cafile /lab/mqtt-ca.pem -q 1 -t "+Q(topic)+" -m "+Q(body);
    if(!string.IsNullOrWhiteSpace(mqttUser))args+=" -u "+Q(mqttUser)+" -P "+Q(password);
    string cmd="/usr/local/bin/docker run"+args;
    Console.WriteLine("通过外部 MQTT "+mqttHost+":"+mqttPort+" 单次发布 ESP32 OTA 命令；NAS 不运行 MQTT Broker。");
    return SshRun(n.Value.nas,n.Value.user,n.Value.port,"sh -lc "+Q(cmd));
}
static int NasLogs() {
    var n=Nas();if(n==null)return 2;
    const string root="/volume1/docker/g3-migration";
    return SshRun(n.Value.nas,n.Value.user,n.Value.port,"/usr/local/bin/docker compose -f "+root+"/lab/compose.json logs --tail=100 https mqtt-tcp-proxy");
}
static int NasStop() {
    var n=Nas();if(n==null)return 2;
    Console.Write("只停止 G3 专用容器，不删除数据、不改 DNS。输入 STOP: ");
    if(Console.ReadLine()!="STOP")return 1;
    const string root="/volume1/docker/g3-migration";
    return SshRun(n.Value.nas,n.Value.user,n.Value.port,"/usr/local/bin/docker compose -f "+root+"/lab/compose.json down");
}
static int DnsGuide() {
    Console.WriteLine("必须仅针对 G3 配置以下三个 DNS 域名到独立服务 IP，不能使用全局 dnsmasq address=/domain/IP 覆盖其他设备：");
    foreach(var d in new[]{"newlifemulti.daikin-china.com.cn","newlifemulti-cdh.daikin-china.com.cn","newlifemulti-cdh-core.daikin-china.com.cn"})Console.WriteLine("  "+d);
    Console.WriteLine("请在 OpenWrt 为 G3 配置专用 DNS 解析器及按设备 MAC 区分的 DHCP DNS，并验证 G3 实际解析结果。当前版本不自动修改 DNS，以免影响全网。");
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
    Console.WriteLine("G3Windows v0.4 - factory G3 migration host helper");
    Console.WriteLine("  G3Windows.exe check       Check Docker, WSL, PowerShell and ports");
    Console.WriteLine("  G3Windows.exe guide       Print safe setup steps");
    Console.WriteLine("  G3Windows.exe network [--router-ip IP] [--nas-ip IP]  Detect router; manually enter NAS/SSH");
    Console.WriteLine("  G3Windows.exe nas-check  Test SSH and remote Docker/Compose on Synology");
    Console.WriteLine("  G3Windows.exe nas-deploy  Deploy HTTPS-only G3 service to Synology; use external MQTT");
    Console.WriteLine("  G3Windows.exe nas-status  Check deployed G3 migration services");
    Console.WriteLine("  MQTT endpoint is user-configured (host/port/TLS/CA); no MQTT Broker is installed on NAS.");
    Console.WriteLine("  G3Windows.exe nas-logs    Inspect G3 HTTPS logs (external MQTT is not hosted here)");
    Console.WriteLine("  G3Windows.exe responder   Run timed G3 gateway acknowledgements");
    Console.WriteLine("  G3Windows.exe ota-prepare Verify raw firmware SHA256 and enable OTA offer");
    Console.WriteLine("  G3Windows.exe ota-trigger Guided OTA safety confirmation (not automated)");
    Console.WriteLine("  G3Windows.exe dns-guide   Show per-device DNS requirements");
    Console.WriteLine("  G3Windows.exe nas-stop    Stop only the G3 Compose stack");
    Console.WriteLine("  G3Windows.exe help        Show this help");
    Console.WriteLine("External MQTT parameters are user-configured; NAS deployment is HTTPS-only.");
}
if (args.Length == 0) { Help(); return 0; }
switch (args[0].ToLowerInvariant()) {
    case "check": return Check();
    case "network": return Network(args.Skip(1).ToArray());
    case "nas-deploy": return NasDeploy();
    case "nas-logs": return NasLogs();
    case "nas-stop": return NasStop();
    case "responder": return Responder();
    case "ota-prepare": return OtaPrepare();
    case "ota-trigger": return OtaTrigger();
    case "dns-guide": return DnsGuide();
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
