using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.RegularExpressions;

static IPAddress? DetectGateway() => NetworkInterface.GetAllNetworkInterfaces()
    .Where(n => n.OperationalStatus == OperationalStatus.Up)
    .SelectMany(n => n.GetIPProperties().GatewayAddresses).Select(x => x.Address)
    .FirstOrDefault(x => x.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !x.Equals(IPAddress.Any));
static IPAddress? DetectLan(IPAddress? gw) => NetworkInterface.GetAllNetworkInterfaces()
    .Where(n => n.OperationalStatus == OperationalStatus.Up)
    .SelectMany(n => n.GetIPProperties().UnicastAddresses)
    .Where(x => x.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(x.Address))
    .OrderByDescending(x => gw != null && x.Address.ToString().StartsWith(string.Join(".",gw.ToString().Split('.').Take(3))+"."))
    .Select(x => x.Address).FirstOrDefault();
static bool ValidIp(string s) => IPAddress.TryParse(s,out var a) && a.AddressFamily==System.Net.Sockets.AddressFamily.InterNetwork;
static string Q(string s) => "'"+s.Replace("'","'\\''")+"'";
static string ConfigPath() => Path.Combine(AppContext.BaseDirectory,"g3-linux.json");
static JsonDocument? ReadConfig(){if(!File.Exists(ConfigPath())){Console.Error.WriteLine("请先运行 network 配置 Linux 服务端。");return null;}return JsonDocument.Parse(File.ReadAllText(ConfigPath));}
static int Run(string exe,params string[] args){try{var p=new Process{StartInfo=new ProcessStartInfo(exe){UseShellExecute=false}};foreach(var a in args)p.StartInfo.ArgumentList.Add(a);p.Start();p.WaitForExit();return p.ExitCode;}catch(Exception e){Console.Error.WriteLine(e.Message);return 1;}}
static int Ssh(string host,string user,int port,string command){var psi=new ProcessStartInfo("ssh"){UseShellExecute=false};psi.ArgumentList.Add("-p");psi.ArgumentList.Add(port.ToString());psi.ArgumentList.Add("-o");psi.ArgumentList.Add("ConnectTimeout=12");psi.ArgumentList.Add("-o");psi.ArgumentList.Add("ServerAliveInterval=15");psi.ArgumentList.Add(user+"@"+host);psi.ArgumentList.Add("sh -lc "+Q(command));try{using var p=Process.Start(psi)!;p.WaitForExit();return p.ExitCode;}catch(Exception e){Console.Error.WriteLine("SSH 失败："+e.Message);return 1;}}
static int Scp(string host,string user,int port,string source,string target){var psi=new ProcessStartInfo("scp"){UseShellExecute=false};psi.ArgumentList.Add("-P");psi.ArgumentList.Add(port.ToString());psi.ArgumentList.Add("-r");psi.ArgumentList.Add(source);psi.ArgumentList.Add(user+"@"+host+":"+target);try{using var p=Process.Start(psi)!;p.WaitForExit();return p.ExitCode;}catch(Exception e){Console.Error.WriteLine("SCP 失败："+e.Message);return 1;}}
static (string host,string user,int port)? Server(){using var d=ReadConfig();if(d==null)return null;var x=d.RootElement;return(x.GetProperty("linuxIp").GetString()!,x.GetProperty("sshUser").GetString()!,x.GetProperty("sshPort").GetInt32());}
static string Root() => "$HOME/g3-migration";
static int Network(string[] args){
    var gw=DetectGateway();var lan=DetectLan(gw);
    Console.WriteLine("G3 Linux service host configuration (no DNS/device changes)");
    Console.WriteLine("Windows LAN IPv4: "+(lan?.ToString()??"not detected"));
    Console.WriteLine("Default gateway: "+(gw?.ToString()??"not detected"));
    Console.Write("OpenWrt router IPv4 [detected]: ");var router=Console.ReadLine()?.Trim()??"";if(router=="")router=gw?.ToString()??"";
    Console.Write("Linux service host IPv4 (reachable by G3) [same LAN as Windows]: ");var ip=Console.ReadLine()?.Trim()??"";
    Console.Write("SSH username [linux user]: ");var user=Console.ReadLine()?.Trim()??"";
    Console.Write("SSH port [22]: ");var pt=Console.ReadLine()?.Trim()??"";int port=pt==""?22:int.TryParse(pt,out var n)?n:0;
    if(!ValidIp(router)||!ValidIp(ip)||string.IsNullOrWhiteSpace(user)||port<1||port>65535){Console.Error.WriteLine("IP/SSH 参数无效");return 2;}
    var obj=new{routerIp=router,linuxIp=ip,sshUser=user,sshPort=port,linuxRoot="~/g3-migration",note="Temporary upstream-compatible HTTPS + anonymous MQTT migration services; no external MQTT required."};
    File.WriteAllText(ConfigPath(),JsonSerializer.Serialize(obj,new JsonSerializerOptions{WriteIndented=true}));
    Console.WriteLine("Saved "+ConfigPath);Console.WriteLine("Linux service host: "+ip);Console.WriteLine("MQTT Broker will run on Linux TCP 8883; HTTPS on TCP 443.");
    Console.WriteLine("No DNS, firewall, Docker or G3 settings changed.");return 0;
}
static string AskMac(){Console.Write("G3 实际 Wi-Fi STA MAC（12位十六进制，不是 BLE MAC）: ");return(Console.ReadLine()??"").Replace(":","").Replace("-","").ToUpperInvariant();}
static int LinuxCheck(){
    var s=Server();if(s==null)return 2;
    string cmd="set -e; echo '--- OS ---'; . /etc/os-release 2>/dev/null && echo \"$PRETTY_NAME\" || uname -a; echo '--- Docker ---'; docker version --format '{{.Server.Version}}'; echo '--- Compose ---'; docker compose version; echo '--- Python ---'; python3 --version; echo '--- ports ---'; (ss -lnt 2>/dev/null | grep -E ':(443|8883)[[:space:]]' || true); echo '--- docker permission ---'; docker ps >/dev/null";
    return Ssh(s.Value.host,s.Value.user,s.Value.port,cmd);
}
static int Deploy(){
    var s=Server();if(s==null)return 2;var mac=AskMac();if(!Regex.IsMatch(mac,"^[0-9A-F]{12}$"))return 2;
    var bundle=Path.Combine(AppContext.BaseDirectory,"upstream-g3-migrate");
    foreach(var n in new[]{"init.py","local_ota_server.py","local_gateway_responder.py","enable.py","trigger.py"})if(!File.Exists(Path.Combine(bundle,n))){Console.Error.WriteLine("缺少上游迁移文件："+n);return 3;}
    Console.WriteLine("将在 Linux 上部署上游原生临时 HTTPS + MQTT 兼容服务：TCP 443/8883。MQTT Broker 与 HTTPS 均运行在 Linux Docker Compose 中。");
    Console.WriteLine("Broker 使用上游配置的临时 LAN 匿名 TLS，仅用于 G3 迁移；禁止公网暴露，完成后删除。");
    Console.Write("确认设备 STA MAC、Linux IP、型号 BRY88BBA15G / SENSOR3_ESP32 正确？输入 DEPLOY: ");if(Console.ReadLine()!="DEPLOY")return 1;
    string setup="set -e; mkdir -p $HOME/g3-migration/upstream-g3-migrate; if ss -lnt 2>/dev/null | grep -E ':(443|8883)[[:space:]]'; then echo PORT_443_OR_8883_OCCUPIED; exit 20; fi; docker version --format '{{.Server.Version}}' >/dev/null; docker compose version";
    if(Ssh(s.Value.host,s.Value.user,s.Value.port,setup)!=0)return 4;
    if(Scp(s.Value.host,s.Value.user,s.Value.port,bundle, "~/g3-migration/")!=0)return 5;
    string cmd="set -e; cd $HOME/g3-migration/upstream-g3-migrate; python3 init.py --address "+s.Value.host+" --device "+mac+" --output $HOME/g3-migration/lab; docker compose -f $HOME/g3-migration/lab/compose.json up -d; docker compose -f $HOME/g3-migration/lab/compose.json ps";
    if(Ssh(s.Value.host,s.Value.user,s.Value.port,cmd)!=0){Console.Error.WriteLine("Linux Compose 部署失败；保留日志与目录供排查。");return 6;}
    Console.WriteLine("Linux HTTPS + MQTT 临时服务已启动，初始 NO-OTA。尚未修改 DNS、配网或发送 OTA。");return 0;
}
static int Status(){var s=Server();if(s==null)return 2;return Ssh(s.Value.host,s.Value.user,s.Value.port,"docker compose -f $HOME/g3-migration/lab/compose.json ps; echo '--- listeners ---'; ss -lnt | grep -E ':(443|8883)[[:space:]]' || true");}
static int Logs(){var s=Server();if(s==null)return 2;return Ssh(s.Value.host,s.Value.user,s.Value.port,"docker compose -f $HOME/g3-migration/lab/compose.json logs --tail=120 https mqtt");}
static int Responder(){
    var s=Server();if(s==null)return 2;var mac=AskMac();if(!Regex.IsMatch(mac,"^[0-9A-F]{12}$"))return 2;
    Console.Write("应答运行秒数 [600]（60-3600）: ");var t=Console.ReadLine()?.Trim()??"";int seconds=t==""?600:int.TryParse(t,out var n)?n:0;if(seconds<60||seconds>3600)return 2;
    string cmd="cd $HOME/g3-migration/lab && python3 local_gateway_responder.py --device "+mac+" --gateway 020000000001 --host "+s.Value.host+" --empty-gateway --weather-code 1 --seconds "+seconds;
    Console.WriteLine("启动限时网关应答。保持此窗口运行，等待五项应答。");return Ssh(s.Value.host,s.Value.user,s.Value.port,cmd);
}
static int OtaPrepare(){
    var s=Server();if(s==null)return 2;var mac=AskMac();if(!Regex.IsMatch(mac,"^[0-9A-F]{12}$"))return 2;
    Console.Write("配套发布的 G3 raw ESP32 文件路径（.ota.bin.raw）: ");var file=(Console.ReadLine()??"").Trim().Trim('"');if(!File.Exists(file)||!file.EndsWith(".ota.bin.raw",StringComparison.OrdinalIgnoreCase))return 2;
    Console.Write("该文件对应 SHA256（64位）: ");var sha=(Console.ReadLine()??"").Trim();if(!Regex.IsMatch(sha,"^[A-Fa-f0-9]{64}$"))return 2;
    using var fs=File.OpenRead(file);var actual=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fs));if(!actual.Equals(sha,StringComparison.OrdinalIgnoreCase)){Console.Error.WriteLine("SHA256 不匹配，拒绝继续");return 3;}
    Console.Write("OTA 目标标签 [V99.00.00]: ");var version=Console.ReadLine()?.Trim()??"";if(version=="")version="V99.00.00";if(!Regex.IsMatch(version,"^[A-Za-z0-9._-]{1,31}$"))return 2;
    Console.Write("确认固件与迁移包来自同一发布、型号正确、五项应答成功？输入 ENABLE: ");if(Console.ReadLine()!="ENABLE")return 1;
    if(Scp(s.Value.host,s.Value.user,s.Value.port,file,"~/g3-migration/")!=0)return 4;
    string name=Path.GetFileName(file);
    string cmd="set -e; cd $HOME/g3-migration/upstream-g3-migrate; python3 enable.py --directory $HOME/g3-migration/lab --device "+mac+" --firmware $HOME/g3-migration/"+name+" --version "+version+"; docker compose -f $HOME/g3-migration/lab/compose.json up -d --no-deps --force-recreate https";
    return Ssh(s.Value.host,s.Value.user,s.Value.port,cmd);
}
static int OtaTrigger(){
    var s=Server();if(s==null)return 2;var mac=AskMac();if(!Regex.IsMatch(mac,"^[0-9A-F]{12}$"))return 2;
    Console.Write("OTA 目标标签 [V99.00.00]: ");var v=Console.ReadLine()?.Trim()??"";if(v=="")v="V99.00.00";if(!Regex.IsMatch(v,"^[A-Za-z0-9._-]{1,31}$"))return 2;
    Console.WriteLine("仅发布一次 ESP32 OTA 命令；确认 HTTPS artifact 服务正常、STA MAC 正确、供电稳定。");
    Console.Write("最后确认，输入 TRIGGER: ");if(Console.ReadLine()!="TRIGGER")return 1;
    string cmd="cd $HOME/g3-migration/upstream-g3-migrate && python3 trigger.py --device "+mac+" --host "+s.Value.host+" --version "+v+" --trigger-esp32";
    return Ssh(s.Value.host,s.Value.user,s.Value.port,cmd);
}
static int Stop(){var s=Server();if(s==null)return 2;Console.Write("停止并移除本次 G3 临时 HTTPS/MQTT 容器？不会修改 DNS。输入 STOP: ");if(Console.ReadLine()!="STOP")return 1;return Ssh(s.Value.host,s.Value.user,s.Value.port,"docker compose -f $HOME/g3-migration/lab/compose.json down");}
static int DnsGuide(){var s=Server();if(s==null)return 2;Console.WriteLine("仅为目标 G3 配置以下域名指向 Linux 服务端 "+s.Value.host+"，不要全局覆盖：");foreach(var d in new[]{"newlifemulti.daikin-china.com.cn","newlifemulti-cdh.daikin-china.com.cn","newlifemulti-cdh-core.daikin-china.com.cn"})Console.WriteLine(d);Console.WriteLine("Linux 服务端需允许目标设备访问 TCP 443/8883。确认 DHCP/DNS 仅对该 G3 生效。");return 0;}
static void Help(){Console.WriteLine("G3Windows v0.5 — Windows manager / Linux Docker host");Console.WriteLine("  network       Configure Linux host SSH and LAN IP");Console.WriteLine("  linux-check   Check Linux Docker Engine, Compose, Python and ports");Console.WriteLine("  linux-deploy  Deploy upstream temporary HTTPS + MQTT broker (NO OTA)");Console.WriteLine("  linux-status  Check Linux HTTPS/MQTT containers");Console.WriteLine("  linux-logs    Read service logs");Console.WriteLine("  dns-guide     Show per-device OpenWrt DNS overrides");Console.WriteLine("  responder     Run five bounded G3 gateway acknowledgements");Console.WriteLine("  ota-prepare   Verify SHA256 and enable matching ESP32 raw firmware");Console.WriteLine("  ota-trigger   Send one explicit ESP32-only OTA command");Console.WriteLine("  linux-stop    Stop/remove temporary migration containers");Console.WriteLine("MQTT broker is the upstream temporary Mosquitto service on Linux, not an external broker.");}
if(args.Length==0){Help();return 0;}
switch(args[0].ToLowerInvariant()){
 case "help":Help();return 0;
 case "check":Console.WriteLine("Windows OpenSSH/SCP");return Run("ssh","-V");
 case "network":return Network(args.Skip(1).ToArray());
 case "linux-check":return LinuxCheck();
 case "linux-deploy":return Deploy();
 case "linux-status":return Status();
 case "linux-logs":return Logs();
 case "responder":return Responder();
 case "ota-prepare":return OtaPrepare();
 case "ota-trigger":return OtaTrigger();
 case "dns-guide":return DnsGuide();
 case "linux-stop":return Stop();
 default:Help();return 2;
}
