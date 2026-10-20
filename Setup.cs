using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace UeBridge;

/// <summary>Параметры, которые пользователь задаёт в окне. Сохраняются в %APPDATA%\UeBridge\settings.json.</summary>
public sealed class Options
{
    public string UProject { get; set; } = "";
    public string ShareName { get; set; } = "";
    public int Port { get; set; } = 8000;
    public string UrlPath { get; set; } = "/mcp";
    public string Host { get; set; } = "";
    public string Drive { get; set; } = "Z:";

    public string ProjectDir => Path.GetDirectoryName(UProject) ?? "";
    public string McpUrl => $"http://127.0.0.1:{Port}{UrlPath}";
    public string SshUser => Environment.UserName;
    public string HostOrName => string.IsNullOrWhiteSpace(Host) ? Environment.MachineName : Host;

    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UeBridge", "settings.json");

    public static Options Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Options>(File.ReadAllText(FilePath)) ?? new Options();
        }
        catch { }
        return new Options();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

public sealed class Status
{
    public string Sshd = "";
    public string SshdStart = "";
    public string SharePath = "";
    public bool Fw22;
    public bool EditorRunning;
    public bool McpPort;
    public bool PluginsOn;
    public bool IniOk;
    public bool McpJsonOk;
}

/// <summary>Запуск PowerShell-скриптов (Windows PowerShell 5.1, с правами администратора).</summary>
public static class Ps
{
    public static (int Code, string Out) Run(string script, int timeoutMs = 900_000)
    {
        var full = "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8\n$ErrorActionPreference='Stop'\ntry {\n"
                   + script + "\n} catch { Write-Output ('ОШИБКА: ' + $_.Exception.Message); exit 1 }";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(full));
        var psi = new ProcessStartInfo("powershell.exe",
            "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(psi) ?? throw new Exception("не удалось запустить powershell.exe");
        var so = p.StandardOutput.ReadToEndAsync();
        var se = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(true); } catch { }
            return (-1, "таймаут выполнения PowerShell");
        }
        var text = (so.Result + se.Result).Trim();
        return (p.ExitCode, text);
    }

    /// <summary>Строка в одинарных кавычках PowerShell.</summary>
    public static string Q(string s) => "'" + s.Replace("'", "''") + "'";
}

public static class Setup
{
    public const string PluginMcp = "ModelContextProtocol";
    public const string PluginTools = "AllToolsets";
    const string IniSection = "[/Script/ModelContextProtocolEngine.ModelContextProtocolSettings]";

    static string IniPath(Options o) => Path.Combine(o.ProjectDir, "Saved", "Config", "WindowsEditor", "EditorPerProjectUserSettings.ini");
    static string McpJsonPath(Options o) => Path.Combine(o.ProjectDir, ".mcp.json");

    // ---------- 1. OpenSSH Server (стандартный компонент Windows) ----------
    public static string EnsureOpenSsh()
    {
        var (code, output) = Ps.Run(@"
$svc = Get-Service sshd -ErrorAction SilentlyContinue
if (-not $svc) {
    $cap = Get-WindowsCapability -Online | Where-Object Name -like 'OpenSSH.Server*' | Select-Object -First 1
    if (-not $cap) { throw 'компонент OpenSSH.Server не найден в этой версии Windows' }
    if ($cap.State -ne 'Installed') {
        Write-Output 'Устанавливаю компонент Windows OpenSSH Server (нужен интернет, может занять минуту)...'
        Add-WindowsCapability -Online -Name $cap.Name | Out-Null
    }
    $svc = Get-Service sshd -ErrorAction SilentlyContinue
    if (-not $svc) { throw 'служба sshd не появилась после установки компонента' }
}
Set-Service sshd -StartupType Automatic
if ((Get-Service sshd).Status -ne 'Running') { Start-Service sshd }
$rule = Get-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -ErrorAction SilentlyContinue
if (-not $rule) {
    New-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -DisplayName 'OpenSSH Server (sshd)' -Enabled True -Direction Inbound -Protocol TCP -Action Allow -LocalPort 22 -Profile Private,Domain | Out-Null
} else {
    Enable-NetFirewallRule -Name 'OpenSSH-Server-In-TCP'
}
Write-Output ('OK: служба sshd ' + (Get-Service sshd).Status + ', автозапуск, порт 22 открыт для частной сети')
");
        if (code != 0) throw new Exception(output);
        return output;
    }

    // ---------- 2. Сетевая папка проекта ----------
    public static string EnsureShare(Options o)
    {
        if (string.IsNullOrWhiteSpace(o.ShareName)) throw new Exception("не задано имя сетевой папки");
        if (!Directory.Exists(o.ProjectDir)) throw new Exception("папка проекта не найдена: " + o.ProjectDir);
        var account = Environment.UserDomainName + "\\" + Environment.UserName;
        var script = @"
$name = __NAME__
$path = __PATH__
$acct = __ACCT__
$s = Get-SmbShare -Name $name -ErrorAction SilentlyContinue
if ($s -and ($s.Path.TrimEnd('\') -ne $path.TrimEnd('\'))) {
    Write-Output ('Папка ' + $name + ' уже указывала на ' + $s.Path + ', пересоздаю')
    Remove-SmbShare -Name $name -Force
    $s = $null
}
if (-not $s) {
    New-SmbShare -Name $name -Path $path -FullAccess $acct | Out-Null
} else {
    Grant-SmbShareAccess -Name $name -AccountName $acct -AccessRight Full -Force | Out-Null
}
# Группа правил 'Общий доступ к файлам и принтерам' (идентификатор не зависит от языка Windows), только частная сеть
Get-NetFirewallRule -Group '@FirewallAPI.dll,-28502' -ErrorAction SilentlyContinue | Where-Object { $_.Profile -match 'Private|Domain|Any' } | Enable-NetFirewallRule
Write-Output ('OK: \\' + $env:COMPUTERNAME + '\' + $name + '  ->  ' + $path + '  (доступ только для ' + $acct + ')')
"
            .Replace("__NAME__", Ps.Q(o.ShareName))
            .Replace("__PATH__", Ps.Q(o.ProjectDir))
            .Replace("__ACCT__", Ps.Q(account));
        var (code, output) = Ps.Run(script);
        if (code != 0) throw new Exception(output);
        return output;
    }

    // ---------- 3. Плагины в .uproject ----------
    public static string EnableProjectPlugins(Options o)
    {
        if (!File.Exists(o.UProject)) throw new Exception("файл не найден: " + o.UProject);
        var root = JsonNode.Parse(File.ReadAllText(o.UProject))?.AsObject()
                   ?? throw new Exception("не удалось прочитать .uproject как JSON");
        if (root["Plugins"] is not JsonArray plugins)
        {
            plugins = new JsonArray();
            root["Plugins"] = plugins;
        }
        var changed = new List<string>();
        foreach (var name in new[] { PluginMcp, PluginTools })
        {
            var obj = plugins.OfType<JsonObject>().FirstOrDefault(p =>
                string.Equals(p["Name"]?.ToString(), name, StringComparison.OrdinalIgnoreCase));
            if (obj == null)
            {
                plugins.Add(new JsonObject { ["Name"] = name, ["Enabled"] = true });
                changed.Add(name);
            }
            else if (!string.Equals(obj["Enabled"]?.ToString(), "true", StringComparison.OrdinalIgnoreCase))
            {
                obj["Enabled"] = true;
                changed.Add(name);
            }
        }
        if (changed.Count == 0) return "OK: плагины уже включены в .uproject";

        var bak = o.UProject + ".bak";
        if (!File.Exists(bak)) File.Copy(o.UProject, bak);
        var json = root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        File.WriteAllText(o.UProject, json, new UTF8Encoding(false));
        return "OK: включены плагины " + string.Join(", ", changed)
               + " (копия оригинала: .uproject.bak). Перезапустите Unreal Editor, чтобы плагины загрузились.";
    }

    // ---------- 4. INI с настройками MCP-сервера ----------
    public static string WriteIni(Options o)
    {
        var file = IniPath(o);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var wanted = new (string Key, string Value)[]
        {
            ("ServerPortNumber", o.Port.ToString()),
            ("ServerUrlPath", o.UrlPath),
            ("bAutoStartServer", "True"),
        };
        var lines = File.Exists(file) ? File.ReadAllLines(file).ToList() : new List<string>();
        int start = lines.FindIndex(l => l.Trim().Equals(IniSection, StringComparison.OrdinalIgnoreCase));
        if (start < 0)
        {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add("");
            lines.Add(IniSection);
            foreach (var (k, v) in wanted) lines.Add($"{k}={v}");
        }
        else
        {
            int end = start + 1;
            while (end < lines.Count && !lines[end].TrimStart().StartsWith('[')) end++;
            for (int i = end - 1; i > start; i--)
            {
                var key = lines[i].Split('=', 2)[0].Trim();
                if (wanted.Any(w => w.Key.Equals(key, StringComparison.OrdinalIgnoreCase))) lines.RemoveAt(i);
            }
            int ins = start + 1;
            foreach (var (k, v) in wanted) lines.Insert(ins++, $"{k}={v}");
        }
        File.WriteAllLines(file, lines, new UTF8Encoding(false));
        return "OK: " + file + $"  (порт {o.Port}, путь {o.UrlPath}, автозапуск сервера)";
    }

    // ---------- 5. .mcp.json ----------
    public static string WriteMcpJson(Options o)
    {
        var file = McpJsonPath(o);
        JsonObject root = new();
        if (File.Exists(file))
        {
            try { root = JsonNode.Parse(File.ReadAllText(file))?.AsObject() ?? new JsonObject(); }
            catch { File.Copy(file, file + ".bak", true); root = new JsonObject(); }
        }
        if (root["mcpServers"] is not JsonObject servers)
        {
            servers = new JsonObject();
            root["mcpServers"] = servers;
        }
        servers["unreal-mcp"] = new JsonObject { ["type"] = "http", ["url"] = o.McpUrl };
        File.WriteAllText(file, root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }), new UTF8Encoding(false));
        return "OK: " + file + "  (unreal-mcp -> " + o.McpUrl + ")";
    }

    // ---------- 6. Проверка, что в движке есть плагин ModelContextProtocol ----------
    public static string CheckEngine(Options o)
    {
        string? assoc = null;
        try { assoc = JsonNode.Parse(File.ReadAllText(o.UProject))?["EngineAssociation"]?.ToString(); } catch { }
        string? engineDir = null;
        if (!string.IsNullOrEmpty(assoc))
        {
            using var k = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\EpicGames\Unreal Engine\{assoc}");
            engineDir = k?.GetValue("InstalledDirectory") as string;
            if (string.IsNullOrEmpty(engineDir))
            {
                using var b = Registry.CurrentUser.OpenSubKey(@"Software\Epic Games\Unreal Engine\Builds");
                engineDir = b?.GetValue(assoc) as string;
            }
        }
        if (string.IsNullOrEmpty(engineDir) || !Directory.Exists(engineDir))
            return $"ВНИМАНИЕ: папка движка не найдена (EngineAssociation = {(string.IsNullOrEmpty(assoc) ? "не задан" : assoc)}). "
                   + "Проверьте вручную, что в движке есть плагин ModelContextProtocol (Edit -> Plugins).";

        var plugins = Path.Combine(engineDir, "Engine", "Plugins");
        bool found = Directory.Exists(Path.Combine(plugins, "Experimental", PluginMcp));
        if (!found)
        {
            try { found = Directory.EnumerateFiles(plugins, PluginMcp + ".uplugin", SearchOption.AllDirectories).Any(); }
            catch { }
        }
        return found
            ? $"OK: движок {engineDir}, плагин {PluginMcp} найден"
            : $"ВНИМАНИЕ: в движке {engineDir} плагин {PluginMcp} не найден. Нужна версия Unreal Engine, в которой он есть.";
    }

    // ---------- Состояние ----------
    public static Status GetStatus(Options o)
    {
        var s = new Status();
        var script = @"
$r = [ordered]@{}
$svc = Get-Service sshd -ErrorAction SilentlyContinue
$r.sshd = if ($svc) { [string]$svc.Status } else { '' }
$r.start = if ($svc) { [string]$svc.StartType } else { '' }
$name = __NAME__
$sh = if ($name) { Get-SmbShare -Name $name -ErrorAction SilentlyContinue } else { $null }
$r.share = if ($sh) { [string]$sh.Path } else { '' }
$r.fw = [bool]((Get-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -ErrorAction SilentlyContinue).Enabled -eq 'True')
$r | ConvertTo-Json -Compress
".Replace("__NAME__", Ps.Q(o.ShareName));
        var (code, output) = Ps.Run(script, 60_000);
        if (code == 0)
        {
            try
            {
                var j = JsonNode.Parse(output)!;
                s.Sshd = j["sshd"]?.ToString() ?? "";
                s.SshdStart = j["start"]?.ToString() ?? "";
                s.SharePath = j["share"]?.ToString() ?? "";
                s.Fw22 = j["fw"]?.GetValue<bool>() ?? false;
            }
            catch { }
        }
        s.EditorRunning = Process.GetProcessesByName("UnrealEditor").Length > 0
                          || Process.GetProcessesByName("UnrealEditor-Cmd").Length > 0;
        s.McpPort = PortOpen(o.Port);
        try
        {
            if (File.Exists(o.UProject))
            {
                var plugins = JsonNode.Parse(File.ReadAllText(o.UProject))?["Plugins"] as JsonArray;
                s.PluginsOn = plugins != null && new[] { PluginMcp, PluginTools }.All(n =>
                    plugins.OfType<JsonObject>().Any(p => p["Name"]?.ToString() == n
                        && string.Equals(p["Enabled"]?.ToString(), "true", StringComparison.OrdinalIgnoreCase)));
            }
            s.IniOk = File.Exists(IniPath(o)) && File.ReadAllText(IniPath(o)).Contains(IniSection, StringComparison.OrdinalIgnoreCase);
            s.McpJsonOk = File.Exists(McpJsonPath(o)) && File.ReadAllText(McpJsonPath(o)).Contains("unreal-mcp");
        }
        catch { }
        return s;
    }

    static bool PortOpen(int port)
    {
        try
        {
            using var t = new TcpClient();
            var task = t.ConnectAsync("127.0.0.1", port);
            return task.Wait(800) && t.Connected;
        }
        catch { return false; }
    }

    public static List<(string Ip, string Iface)> LocalIPv4()
    {
        var list = new List<(string, string)>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var ip = ua.Address.ToString();
                    if (ip.StartsWith("169.254.")) continue;
                    list.Add((ip, ni.Name));
                }
            }
        }
        catch { }
        return list;
    }

    // ---------- Тексты для ПК1 ----------
    public static string Pc1Info(Options o, Status s)
    {
        var sb = new StringBuilder();
        var host = o.HostOrName;
        sb.AppendLine("ДЛЯ ПК1 (где установлен Claude Code)");
        sb.AppendLine();
        sb.AppendLine($"Имя этого компьютера (ПК2):  {Environment.MachineName}");
        var ips = LocalIPv4();
        sb.AppendLine($"IP-адреса ПК2:               {(ips.Count == 0 ? "не найдены" : string.Join(", ", ips.Select(x => $"{x.Ip} ({x.Iface})")))}");
        sb.AppendLine($"Пользователь для SSH:        {o.SshUser}   (пароль = пароль входа в Windows на ПК2)");
        sb.AppendLine($"Сетевая папка проекта:       \\\\{host}\\{o.ShareName}");
        sb.AppendLine($"MCP-сервер Unreal:           {o.McpUrl}   (через SSH-туннель)");
        sb.AppendLine();
        sb.AppendLine("Команды на ПК1 (cmd или PowerShell), по порядку:");
        sb.AppendLine($"  net use {o.Drive} \\\\{host}\\{o.ShareName} /persistent:yes");
        sb.AppendLine($"  ssh -N -L {o.Port}:127.0.0.1:{o.Port} {o.SshUser}@{host}        (окно не закрывать)");
        sb.AppendLine($"  cd /d {o.Drive}\\");
        sb.AppendLine("  claude");
        sb.AppendLine();
        sb.AppendLine("В Claude Code: /plugin install unreal-engine-skills-for-claude-code@claude-plugins-official");
        sb.AppendLine("Проверка: команда /mcp должна показать unreal-mcp как подключённый.");
        sb.AppendLine("Если имя ПК2 не находится по сети, подставьте вместо него IP-адрес из списка выше.");
        sb.AppendLine();
        sb.AppendLine("Состояние ПК2 сейчас:");
        sb.AppendLine($"  sshd:               {(string.IsNullOrEmpty(s.Sshd) ? "не установлен" : s.Sshd + " / " + s.SshdStart)}");
        sb.AppendLine($"  порт 22 открыт:     {(s.Fw22 ? "да" : "нет")}");
        sb.AppendLine($"  сетевая папка:      {(string.IsNullOrEmpty(s.SharePath) ? "нет" : s.SharePath)}");
        sb.AppendLine($"  плагины в .uproject:{(s.PluginsOn ? " включены" : " не включены")}");
        sb.AppendLine($"  ini / .mcp.json:    {(s.IniOk ? "да" : "нет")} / {(s.McpJsonOk ? "да" : "нет")}");
        sb.AppendLine($"  Unreal Editor:      {(s.EditorRunning ? "запущен" : "не запущен")}");
        sb.AppendLine($"  MCP порт {o.Port}:      {(s.McpPort ? "отвечает" : "не отвечает (запустите редактор с этим проектом)")}");
        return sb.ToString();
    }

    public static string Pc1Script(Options o)
    {
        var host = o.HostOrName;
        var sb = new StringBuilder();
        sb.Append("@echo off\r\n");
        sb.Append("chcp 65001 >nul\r\n");
        sb.Append("title UE Bridge: подключение ПК1 к ПК2\r\n");
        sb.Append($"set HOST={host}\r\n");
        sb.Append($"set SHARE={o.ShareName}\r\n");
        sb.Append($"set SSHUSER={o.SshUser}\r\n");
        sb.Append($"set PORT={o.Port}\r\n");
        sb.Append($"set DRIVE={o.Drive}\r\n");
        sb.Append("\r\n");
        sb.Append("echo [1/3] Сетевая папка %DRIVE% = \\\\%HOST%\\%SHARE%\r\n");
        sb.Append("if not exist %DRIVE%\\ net use %DRIVE% \\\\%HOST%\\%SHARE% /persistent:yes\r\n");
        sb.Append("if not exist %DRIVE%\\ ( echo Не удалось подключить папку. Проверьте имя/IP ПК2 и пароль. & pause & exit /b 1 )\r\n");
        sb.Append("\r\n");
        sb.Append("echo [2/3] SSH-туннель на порт %PORT% (окно \"UE MCP tunnel\" не закрывать)\r\n");
        sb.Append("start \"UE MCP tunnel\" ssh -N -L %PORT%:127.0.0.1:%PORT% %SSHUSER%@%HOST%\r\n");
        sb.Append("\r\n");
        sb.Append("echo [3/3] Запускаю Claude Code в %DRIVE%\\\r\n");
        sb.Append("cd /d %DRIVE%\\\r\n");
        sb.Append("claude\r\n");
        return sb.ToString();
    }
}
