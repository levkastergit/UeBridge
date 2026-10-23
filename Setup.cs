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
    /// <summary>"claude" или "unreal"; пусто — определить по наличию Unreal Engine.</summary>
    public string Role { get; set; } = "";

    // ПК Unreal: связь с ПК Claude через обратный SSH-туннель (заполняется из кода подключения)
    public string ClaudeHost { get; set; } = "";
    public int ClaudePort { get; set; } = 22;
    public string ClaudeUser { get; set; } = "";
    public int SmbTunnelPort { get; set; } = 44445;

    // ПК Claude: адрес и порт, по которым к нему подключается ПК Unreal
    public string PublicHost { get; set; } = "";
    public int SshPort { get; set; } = 22;

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
    public bool McpPort;
    public bool McpOk;
    public string McpInfo = "";
    public bool PluginsOn;
    public bool IniOk;
    public bool McpJsonOk;
    public DateTime ConfigChanged;
    public List<EditorProc> Editors = new();
    public List<Listener> Listeners = new();
    public List<string> EditorLog = new();
    public string TunnelState = "";
}

/// <summary>Запуск PowerShell-скриптов (Windows PowerShell 5.1, с правами администратора).</summary>
public static class Ps
{
    public static (int Code, string Out) Run(string script, int timeoutMs = 900_000)
    {
        var full = "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8\n$ErrorActionPreference='Stop'\n$ProgressPreference='SilentlyContinue'\ntry {\n"
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
        var buf = new StringBuilder();
        var outEof = new TaskCompletionSource();
        var errEof = new TaskCompletionSource();
        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (e.Data == null) outEof.TrySetResult(); else lock (buf) buf.AppendLine(e.Data); };
        // PowerShell 5.1 с -EncodedCommand пишет в перенаправленный stderr служебные записи (прогресс) в формате CLIXML — это не ошибки
        p.ErrorDataReceived += (_, e) => { if (e.Data == null) errEof.TrySetResult(); else if (!IsClixml(e.Data)) lock (buf) buf.AppendLine(e.Data); };
        if (!p.Start()) throw new Exception("не удалось запустить powershell.exe");
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(true); } catch { }
            lock (buf) return (-1, $"таймаут выполнения PowerShell ({timeoutMs / 1000} с)" + (buf.Length > 0 ? "\n" + buf.ToString().Trim() : ""));
        }
        // Вывод может держать открытым процесс, порождённый скриптом, — не ждём его конца бесконечно
        Task.WaitAll(new[] { outEof.Task, errEof.Task }, 5_000);
        lock (buf) return (p.ExitCode, buf.ToString().Trim());
    }

    static bool IsClixml(string line) => line.StartsWith("#< CLIXML") || line.StartsWith("<Objs ");

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
    // Имя компонента — из документации Microsoft: learn.microsoft.com/windows-server/administration/openssh/openssh_install_firstuse
    const string SshCapability = "OpenSSH.Server~~~~0.0.1.0";

    /// <summary>Ставит компонент Windows OpenSSH Server, если службы sshd ещё нет.</summary>
    public static void InstallOpenSshServer(Action<string> progress)
    {
        var (code, output) = Ps.Run("if (Get-Service sshd -ErrorAction SilentlyContinue) { 'yes' } else { 'no' }", 60_000);
        if (code != 0) throw new Exception(output);
        if (output == "yes") return;
        progress("Служба sshd не найдена. Устанавливаю компонент Windows " + SshCapability + " через Windows Update.");
        progress("Нужен интернет; установка может идти 20 минут и дольше без вывода (ограничение — 45 мин). Программа не зависла.");
        (code, output) = Ps.Run(@"
$r = Add-WindowsCapability -Online -Name __CAP__
if (-not (Get-Service sshd -ErrorAction SilentlyContinue)) { throw 'служба sshd не появилась после установки компонента' }
if ($r.RestartNeeded) { Write-Output 'Компонент установлен, Windows просит перезагрузку' } else { Write-Output 'Компонент установлен' }
".Replace("__CAP__", Ps.Q(SshCapability)), 45 * 60_000);
        if (code != 0) throw new Exception(output + "\n" + SshInstallHelp);
        progress(output);
    }

    public static string EnsureOpenSsh(Action<string> progress)
    {
        InstallOpenSshServer(progress);
        var (code, output) = Ps.Run(@"
Set-Service sshd -StartupType Automatic
if ((Get-Service sshd).Status -ne 'Running') { Start-Service sshd }
$rule = Get-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -ErrorAction SilentlyContinue
if (-not $rule) {
    New-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -DisplayName 'OpenSSH Server (sshd)' -Enabled True -Direction Inbound -Protocol TCP -Action Allow -LocalPort 22 -Profile Private,Domain | Out-Null
} else {
    Enable-NetFirewallRule -Name 'OpenSSH-Server-In-TCP'
}
Write-Output ('OK: служба sshd ' + (Get-Service sshd).Status + ', автозапуск, порт 22 открыт для частной сети')
", 300_000);
        if (code != 0) throw new Exception(output);
        return output;
    }

    // Коды ошибок и ручная установка — по статье learn.microsoft.com/troubleshoot/windows-server/system-management-components/cant-install-openssh-features
    const string SshInstallHelp =
        "OpenSSH Server не установился автоматически. Что можно сделать:\n" +
        "  • Установить вручную: Пуск → «Дополнительные компоненты» (Optional features) → Просмотреть компоненты → OpenSSH Server → Добавить. Затем снова «Настроить всё».\n" +
        "  • Коды 0x800F0954, 0x8024402C, 0x80240438 означают, что Windows не может скачать компонент: нет интернета, либо обновления идут через WSUS/групповую политику.\n" +
        "  • Проверить состояние компонента (PowerShell от администратора): Get-WindowsCapability -Online -Name " + SshCapability + "\n" +
        "  • Windows может закончить установку в фоне уже после таймаута — через несколько минут нажмите «Проверить состояние»: если sshd уже есть, снова «Настроить всё».";

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
               + " (копия оригинала: .uproject.bak). Если редактор открыт, его перезапустит шаг «Unreal Editor и MCP-сервер».";
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
        var before = lines.ToList();
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
        // Без изменений файл не трогаем: по времени его изменения диагностика понимает, загрузил ли его редактор
        if (lines.SequenceEqual(before)) return "OK: " + file + "  (уже настроено)";
        File.WriteAllLines(file, lines, new UTF8Encoding(false));
        return "OK: " + file + $"  (порт {o.Port}, путь {o.UrlPath}, автозапуск сервера)";
    }

    static Dictionary<string, string> IniValues(Options o)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool inside = false;
        foreach (var raw in File.ReadAllLines(IniPath(o)))
        {
            var l = raw.Trim();
            if (l.StartsWith('[')) { inside = l.Equals(IniSection, StringComparison.OrdinalIgnoreCase); continue; }
            var kv = l.Split('=', 2);
            if (inside && kv.Length == 2) d[kv[0].Trim()] = kv[1].Trim().Trim('"');
        }
        return d;
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
    /// <summary>Проекты Unreal на этом ПК, сначала тот, что открывали последним. Источники: пути к .uproject в настройках
    /// редактора (%LOCALAPPDATA%\UnrealEngine\*\Saved\Config), «Документы\Unreal Projects» и неглубокий поиск по локальным дискам.</summary>
    public static List<string> FindProjects()
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string p) { try { var f = Path.GetFullPath(p.Replace('/', '\\')); if (File.Exists(f)) found.Add(f); } catch { } }

        var ueCfg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnrealEngine");
        try
        {
            if (Directory.Exists(ueCfg))
                foreach (var ver in Directory.GetDirectories(ueCfg))
                {
                    var cfg = Path.Combine(ver, "Saved", "Config");
                    if (!Directory.Exists(cfg)) continue;
                    foreach (var ini in Directory.EnumerateFiles(cfg, "*.ini", SearchOption.AllDirectories))
                        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                                     File.ReadAllText(ini), @"[A-Za-z]:[\\/][^""\r\n,()=]*?\.uproject"))
                            Add(m.Value);
                }
        }
        catch { }

        try
        {
            var docs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Unreal Projects");
            if (Directory.Exists(docs))
                foreach (var d in Directory.GetDirectories(docs))
                    foreach (var f in Directory.GetFiles(d, "*.uproject")) Add(f);
        }
        catch { }

        // Неглубокий поиск: диск\папка\проект\*.uproject (до 3 уровней), не дольше 8 с
        var skip = new[] { "Windows", "Program Files", "Program Files (x86)", "ProgramData", "$Recycle.Bin", "System Volume Information", "Recovery", "PerfLogs" };
        var sw = Stopwatch.StartNew();
        void Scan(string dir, int depth)
        {
            if (depth > 3 || sw.Elapsed > TimeSpan.FromSeconds(8)) return;
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*.uproject")) Add(f);
                foreach (var d in Directory.EnumerateDirectories(dir))
                {
                    var name = Path.GetFileName(d);
                    if (depth == 0 && skip.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                    if (name.StartsWith('.') || name is "AppData" or "node_modules" or "Intermediate" or "DerivedDataCache" or "Saved" or "Binaries") continue;
                    Scan(d, depth + 1);
                }
            }
            catch { }
        }
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady)) Scan(drive.RootDirectory.FullName, 0);

        // Последний открытый — по журналу редактора (Saved\Logs), иначе по самому .uproject
        static DateTime LastUse(string p)
        {
            var t = File.GetLastWriteTime(p);
            try
            {
                var logs = Path.Combine(Path.GetDirectoryName(p)!, "Saved", "Logs");
                if (Directory.Exists(logs))
                    foreach (var l in Directory.EnumerateFiles(logs, "*.log")) { var lt = File.GetLastWriteTime(l); if (lt > t) t = lt; }
            }
            catch { }
            return t;
        }
        return found.OrderByDescending(LastUse).ToList();
    }

    /// <summary>Есть ли на этом ПК Unreal Engine (установленный из Epic Games Launcher или собранный из исходников).</summary>
    public static bool EngineInstalled()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\EpicGames\Unreal Engine");
            if (k != null && k.GetSubKeyNames().Length > 0) return true;
            using var b = Registry.CurrentUser.OpenSubKey(@"Software\Epic Games\Unreal Engine\Builds");
            return b != null && b.GetValueNames().Length > 0;
        }
        catch { return false; }
    }

    static string? EngineAssociation(Options o)
    {
        try { return JsonNode.Parse(File.ReadAllText(o.UProject))?["EngineAssociation"]?.ToString(); }
        catch { return null; }
    }

    /// <summary>Папка движка из EngineAssociation в .uproject: установленные версии — HKLM, собранные из исходников — HKCU.</summary>
    public static string? EngineDir(Options o)
    {
        var assoc = EngineAssociation(o);
        if (string.IsNullOrEmpty(assoc)) return null;
        string? dir;
        using (var k = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\EpicGames\Unreal Engine\{assoc}"))
            dir = k?.GetValue("InstalledDirectory") as string;
        if (string.IsNullOrEmpty(dir))
        {
            using var b = Registry.CurrentUser.OpenSubKey(@"Software\Epic Games\Unreal Engine\Builds");
            dir = b?.GetValue(assoc) as string;
        }
        return !string.IsNullOrEmpty(dir) && Directory.Exists(dir) ? dir : null;
    }

    public static string CheckEngine(Options o)
    {
        var engineDir = EngineDir(o);
        if (engineDir == null)
        {
            var assoc = EngineAssociation(o);
            return $"ВНИМАНИЕ: папка движка не найдена (EngineAssociation = {(string.IsNullOrEmpty(assoc) ? "не задан" : assoc)}). "
                   + "Проверьте вручную, что в движке есть плагин ModelContextProtocol (Edit -> Plugins).";
        }

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
        try { (s.Editors, s.Listeners) = Unreal.Scan(o); }
        catch { }
        s.McpPort = PortOpen(o.Port);
        (s.McpOk, s.McpInfo) = s.McpPort ? Unreal.McpProbe(o) : (false, $"порт {o.Port} на 127.0.0.1 закрыт");
        try
        {
            if (File.Exists(o.UProject))
            {
                var plugins = JsonNode.Parse(File.ReadAllText(o.UProject))?["Plugins"] as JsonArray;
                s.PluginsOn = plugins != null && new[] { PluginMcp, PluginTools }.All(n =>
                    plugins.OfType<JsonObject>().Any(p => p["Name"]?.ToString() == n
                        && string.Equals(p["Enabled"]?.ToString(), "true", StringComparison.OrdinalIgnoreCase)));
                s.ConfigChanged = File.GetLastWriteTime(o.UProject);
            }
            if (File.Exists(IniPath(o)))
            {
                var v = IniValues(o);
                s.IniOk = v.TryGetValue("bAutoStartServer", out var a) && a.Equals("True", StringComparison.OrdinalIgnoreCase)
                          && v.TryGetValue("ServerPortNumber", out var p) && p == o.Port.ToString()
                          && v.TryGetValue("ServerUrlPath", out var u) && u == o.UrlPath;
                var t = File.GetLastWriteTime(IniPath(o));
                if (t > s.ConfigChanged) s.ConfigChanged = t;
            }
            s.McpJsonOk = File.Exists(McpJsonPath(o)) && File.ReadAllText(McpJsonPath(o)).Contains("unreal-mcp");
        }
        catch { }
        if (!s.McpOk) s.EditorLog = Unreal.EditorLog(o);
        s.TunnelState = Tunnel.State(o);
        return s;
    }

    public static bool PortOpen(int port)
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

    // ---------- Тексты для ПК Claude ----------
    public static string ClaudePcInfo(Options o, Status s)
    {
        if (Tunnel.Paired(o)) return ClaudePcInfoTunnel(o, s);
        var sb = new StringBuilder();
        var host = o.HostOrName;
        sb.AppendLine("ДЛЯ ПК Claude (где установлен Claude Code)");
        sb.AppendLine();
        sb.AppendLine($"Имя этого компьютера (ПК Unreal):  {Environment.MachineName}");
        var ips = LocalIPv4();
        sb.AppendLine($"IP-адреса ПК Unreal:               {(ips.Count == 0 ? "не найдены" : string.Join(", ", ips.Select(x => $"{x.Ip} ({x.Iface})")))}");
        sb.AppendLine($"Пользователь для SSH:        {o.SshUser}   (пароль = пароль входа в Windows на ПК Unreal)");
        sb.AppendLine($"Сетевая папка проекта:       \\\\{host}\\{o.ShareName}");
        sb.AppendLine($"MCP-сервер Unreal:           {o.McpUrl}   (через SSH-туннель)");
        sb.AppendLine();
        sb.AppendLine("Команды на ПК Claude (cmd или PowerShell), по порядку:");
        sb.AppendLine($"  net use {o.Drive} \\\\{host}\\{o.ShareName} /persistent:yes");
        sb.AppendLine($"  ssh -N -L {o.Port}:127.0.0.1:{o.Port} {o.SshUser}@{host}        (окно не закрывать)");
        sb.AppendLine($"  cd /d {o.Drive}\\");
        sb.AppendLine("  claude");
        sb.AppendLine();
        sb.AppendLine("В Claude Code: /plugin install unreal-engine-skills-for-claude-code@claude-plugins-official");
        sb.AppendLine("Проверка: команда /mcp должна показать unreal-mcp как подключённый.");
        sb.AppendLine("Если имя ПК Unreal не находится по сети, подставьте вместо него IP-адрес из списка выше.");
        sb.AppendLine();
        sb.AppendLine("Состояние ПК Unreal сейчас:");
        sb.AppendLine($"  sshd:               {(string.IsNullOrEmpty(s.Sshd) ? "не установлен" : s.Sshd + " / " + s.SshdStart)}");
        sb.AppendLine($"  порт 22 открыт:     {(s.Fw22 ? "да" : "нет")}");
        sb.AppendLine($"  сетевая папка:      {(string.IsNullOrEmpty(s.SharePath) ? "нет" : s.SharePath)}");
        sb.AppendLine($"  плагины в .uproject:{(s.PluginsOn ? " включены" : " не включены")}");
        sb.AppendLine($"  ini / .mcp.json:    {(s.IniOk ? "да" : "нет")} / {(s.McpJsonOk ? "да" : "нет")}");
        var ed = s.Editors.FirstOrDefault(e => e.ThisProject);
        sb.AppendLine($"  Unreal Editor:      {(ed != null ? $"запущен с этим проектом (PID {ed.Pid}, с {ed.Started:HH:mm:ss})" : s.Editors.Count > 0 ? "запущен с другим проектом" : "не запущен")}");
        sb.AppendLine($"  MCP-сервер:         {(s.McpOk ? s.McpInfo : "не отвечает — " + s.McpInfo)}");
        return sb.ToString();
    }

    static string ClaudePcInfoTunnel(Options o, Status s)
    {
        var sb = new StringBuilder();
        sb.AppendLine("ПК Claude — связь через обратный SSH-туннель");
        sb.AppendLine();
        sb.AppendLine($"ПК Claude:                         {o.ClaudeUser}@{o.ClaudeHost}:{o.ClaudePort}");
        sb.AppendLine($"Туннель:                     {s.TunnelState}   (задача «{Tunnel.TaskName}», запускается сама при включении ПК Unreal)");
        sb.AppendLine($"На ПК Claude откроются:            127.0.0.1:{o.Port} — MCP-сервер Unreal, 127.0.0.1:{o.SmbTunnelPort} — папка проекта");
        sb.AppendLine($"Пользователь для папки:      {Environment.UserDomainName}\\{Environment.UserName}   (пароль = пароль входа в Windows на ПК Unreal)");
        sb.AppendLine();
        sb.AppendLine("На ПК Claude:");
        sb.AppendLine($"  1. Запустите «Подключить-UE.cmd» на рабочем столе (или «Подключить проект» в UeBridge на ПК Claude).");
        sb.AppendLine($"     Он подключит папку проекта как {o.Drive} и откроет её в Cursor / VS Code.");
        sb.AppendLine("     При первом запуске спросит пароль Windows от ПК Unreal и запомнит его.");
        sb.AppendLine("  2. В Claude Code один раз: /plugin install unreal-engine-skills-for-claude-code@claude-plugins-official");
        sb.AppendLine("  3. Проверка: /mcp должен показать unreal-mcp как подключённый.");
        sb.AppendLine();
        sb.AppendLine("Состояние ПК Unreal сейчас:");
        sb.AppendLine($"  сетевая папка:      {(string.IsNullOrEmpty(s.SharePath) ? "нет" : s.SharePath)}");
        sb.AppendLine($"  плагины в .uproject:{(s.PluginsOn ? " включены" : " не включены")}");
        sb.AppendLine($"  ini / .mcp.json:    {(s.IniOk ? "да" : "нет")} / {(s.McpJsonOk ? "да" : "нет")}");
        var ed = s.Editors.FirstOrDefault(e => e.ThisProject);
        sb.AppendLine($"  Unreal Editor:      {(ed != null ? $"запущен с этим проектом (PID {ed.Pid}, с {ed.Started:HH:mm:ss})" : s.Editors.Count > 0 ? "запущен с другим проектом" : "не запущен")}");
        sb.AppendLine($"  MCP-сервер:         {(s.McpOk ? s.McpInfo : "не отвечает — " + s.McpInfo)}");
        return sb.ToString();
    }

    public static string ClaudePcScript(Options o)
    {
        var host = o.HostOrName;
        var sb = new StringBuilder();
        sb.Append("@echo off\r\n");
        sb.Append("chcp 65001 >nul\r\n");
        sb.Append("title UE Bridge: подключение ПК Claude к ПК Unreal\r\n");
        sb.Append($"set HOST={host}\r\n");
        sb.Append($"set SHARE={o.ShareName}\r\n");
        sb.Append($"set SSHUSER={o.SshUser}\r\n");
        sb.Append($"set PORT={o.Port}\r\n");
        sb.Append($"set DRIVE={o.Drive}\r\n");
        sb.Append("\r\n");
        sb.Append("echo [1/3] Сетевая папка %DRIVE% = \\\\%HOST%\\%SHARE%\r\n");
        sb.Append("if not exist %DRIVE%\\ net use %DRIVE% \\\\%HOST%\\%SHARE% /persistent:yes\r\n");
        sb.Append("if not exist %DRIVE%\\ ( echo Не удалось подключить папку. Проверьте имя/IP ПК Unreal и пароль. & pause & exit /b 1 )\r\n");
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
