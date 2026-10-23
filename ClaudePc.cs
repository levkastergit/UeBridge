using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;

namespace UeBridge;

/// <summary>ПК Claude (где Claude Code): SSH-сервер для обратного туннеля с ПК Unreal, код подключения, скрипты приёма и подключения папки.</summary>
public static class ClaudePc
{
    const string KeyComment = "uebridge-unrealpc";
    const string FwRule = "UeBridge-ClaudePC-SSH";
    const string BlockBegin = "# --- UeBridge (begin) ---";
    const string BlockEnd = "# --- UeBridge (end) ---";

    static string SshData => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ssh");
    static string SshdConfig => Path.Combine(SshData, "sshd_config");
    static string HostKeyPub => Path.Combine(SshData, "ssh_host_ed25519_key.pub");
    static string SshdExe => Path.Combine(Environment.SystemDirectory, "OpenSSH", "sshd.exe");
    static string ReceiveScript => Path.Combine(Win.Dir, "receive.ps1");
    static string ConnectScript => Path.Combine(Win.Dir, "connect.ps1");
    static string PrivDir => Path.Combine(Win.Dir, "claude");
    static string CodeFile => Path.Combine(PrivDir, "pair-code.txt");
    public static string DesktopCmd => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Подключить-UE.cmd");
    /// <summary>Данные о проекте, которые ПК Unreal присылает при каждом подключении (пишет receive.ps1 от имени этого пользователя).</summary>
    static string UnrealInfoFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UeBridge", "unrealpc.json");

    // Для администраторов OpenSSH для Windows берёт ключи из administrators_authorized_keys (блок Match Group administrators в sshd_config)
    static bool IsAdmin => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    static string AuthorizedKeys => IsAdmin
        ? Path.Combine(SshData, "administrators_authorized_keys")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "authorized_keys");

    // Ключ ПК Unreal может только открывать обратные переадресации и запускать receive.ps1 — без командной строки
    static string KeyOptions => $"restrict,port-forwarding,command=\"powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File {ReceiveScript}\"";

    /// <summary>IPv4-адреса этого ПК: сначала публичные.</summary>
    public static List<string> Addresses() =>
        Setup.LocalIPv4().Select(x => x.Ip).OrderBy(ip => IsPrivate(ip) ? 1 : 0).ToList();

    static bool IsPrivate(string ip)
    {
        var b = IPAddress.Parse(ip).GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
    }

    // ---------- 1. SSH-сервер ----------
    public static string EnsureSshd(Options o, Action<string> progress)
    {
        Setup.InstallOpenSshServer(progress);
        var (code, output) = Ps.Run("Set-Service sshd -StartupType Automatic\nif ((Get-Service sshd).Status -ne 'Running') { Start-Service sshd }\n'OK'", 120_000);
        if (code != 0) throw new Exception(output);
        // sshd_config и ключи узла служба создаёт при первом запуске
        var sw = Stopwatch.StartNew();
        while (!(File.Exists(SshdConfig) && File.Exists(HostKeyPub)) && sw.Elapsed < TimeSpan.FromSeconds(30)) Thread.Sleep(1000);
        if (!File.Exists(SshdConfig) || !File.Exists(HostKeyPub)) throw new Exception("служба sshd не создала " + SshdConfig + " и ключи узла");

        // Свои настройки — блоком в начале файла: sshd берёт первое значение каждого параметра
        var lines = File.ReadAllLines(SshdConfig).ToList();
        var before = lines.ToList();
        int b = lines.FindIndex(l => l.Trim() == BlockBegin), e = lines.FindIndex(l => l.Trim() == BlockEnd);
        if (b >= 0 && e > b) lines.RemoveRange(b, e - b + 1);
        lines.InsertRange(0, new[]
        {
            BlockBegin,
            $"Port {o.SshPort}",
            "PasswordAuthentication no",
            "KbdInteractiveAuthentication no",
            "AllowTcpForwarding remote",
            "ClientAliveInterval 15",
            "ClientAliveCountMax 3",
            BlockEnd,
        });
        var notes = new List<string>();
        if (!lines.SequenceEqual(before))
        {
            var bak = SshdConfig + ".uebridge.bak";
            if (!File.Exists(bak)) File.Copy(SshdConfig, bak);
            File.WriteAllLines(SshdConfig, lines, new UTF8Encoding(false));
            var (tc, to, te) = Win.Run(SshdExe, new[] { "-t" }, null, 30_000);
            if (tc != 0)
            {
                File.WriteAllLines(SshdConfig, before, new UTF8Encoding(false));
                throw new Exception("sshd не принял настройки, файл возвращён как был: " + (te + " " + to).Trim());
            }
            (code, output) = Ps.Run("Restart-Service sshd -Force\n'OK'", 120_000);
            if (code != 0) throw new Exception("не удалось перезапустить sshd: " + output);
            notes.Add("настройки sshd обновлены (копия исходного файла: sshd_config.uebridge.bak)");
        }

        (code, output) = Ps.Run(@"
Get-NetFirewallRule -Name __RULE__ -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule -Name __RULE__ -DisplayName 'UeBridge: SSH для ПК Unreal' -Direction Inbound -Protocol TCP -LocalPort __PORT__ -Action Allow -Profile Any | Out-Null
Start-Sleep 1
$l = @(Get-NetTCPConnection -State Listen -LocalPort __PORT__ -ErrorAction SilentlyContinue | Where-Object { (Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue).ProcessName -eq 'sshd' })
if ($l.Count -eq 0) { throw 'sshd не слушает порт __PORT__' }
'OK'
".Replace("__RULE__", Ps.Q(FwRule)).Replace("__PORT__", o.SshPort.ToString()), 60_000);
        if (code != 0) throw new Exception(output);
        return $"OK: sshd работает на порту {o.SshPort}, вход только по ключу, разрешена только обратная переадресация, "
               + $"брандмауэр пропускает порт {o.SshPort}" + (notes.Count > 0 ? "; " + string.Join("; ", notes) : "");
    }

    // ---------- 2. Скрипты: приём данных от ПК Unreal и подключение папки проекта ----------
    public static string WriteScripts()
    {
        Win.SecureDir();
        Win.WriteFile(ReceiveScript, ReceivePs1, bom: true);
        Win.WriteFile(ConnectScript, ConnectPs1, bom: true);
        if (File.Exists(DesktopCmd)) File.Delete(DesktopCmd);
        File.WriteAllText(DesktopCmd, "@echo off\r\nchcp 65001 >nul\r\npowershell -NoProfile -ExecutionPolicy Bypass -File \"" + ConnectScript + "\"\r\n", new UTF8Encoding(false));
        return "OK: " + ReceiveScript + ", " + ConnectScript + ", на рабочем столе — «" + Path.GetFileName(DesktopCmd) + "»";
    }

    // ---------- 3. Ключ для ПК Unreal и код подключения ----------
    public static (string Code, string Result) EnsurePairing(Options o, bool renew, Action<string> progress)
    {
        Win.SecureDir();
        Directory.CreateDirectory(PrivDir);
        Win.RestrictToAdmins(PrivDir, dir: true);

        var keysFile = AuthorizedKeys;
        Directory.CreateDirectory(Path.GetDirectoryName(keysFile)!);
        var keys = File.Exists(keysFile) ? File.ReadAllLines(keysFile).ToList() : new List<string>();
        PairInfo? old = null;
        try { if (File.Exists(CodeFile)) old = PairInfo.Decode(File.ReadAllText(CodeFile)); } catch { }
        var hostKey = string.Join(' ', File.ReadAllText(HostKeyPub).Trim().Split(' ').Take(2));
        var user = Environment.UserName;
        var oldPub = old == null ? null : PublicKeyOf(old.Key);
        bool keyOk = oldPub != null && keys.Any(l => l.Contains(oldPub) && l.StartsWith(KeyOptions));

        string priv;
        if (renew || !keyOk)
        {
            var tmp = Path.Combine(PrivDir, "new");
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            Directory.CreateDirectory(tmp);
            try
            {
                var file = Path.Combine(tmp, "k");
                var (c, so, se) = Win.Run(Win.SshKeygenExe, new[] { "-q", "-t", "ed25519", "-N", "", "-C", KeyComment, "-f", file }, null, 30_000);
                if (c != 0) throw new Exception("ssh-keygen: " + (se + " " + so).Trim());
                priv = File.ReadAllText(file);
                var pub = File.ReadAllText(file + ".pub").Trim();
                keys.RemoveAll(l => l.Contains(KeyComment));
                keys.Add(KeyOptions + " " + pub);
                File.WriteAllLines(keysFile, keys, new UTF8Encoding(false));
                if (IsAdmin) Win.RestrictToAdmins(keysFile, dir: false);
                progress(old == null ? "Создан ключ для ПК Unreal." : "Создан новый ключ для ПК Unreal — старый код подключения больше не действует.");
            }
            finally { Directory.Delete(tmp, true); }
        }
        else priv = old!.Key;

        var p = new PairInfo(o.PublicHost, o.SshPort, user, priv, hostKey);
        var code = p.Encode();
        Win.WriteFile(CodeFile, code);
        Win.RestrictToAdmins(CodeFile, dir: false);
        return (code, SelfTest(o, p));
    }

    public static string StoredCode()
    {
        try { return File.Exists(CodeFile) ? File.ReadAllText(CodeFile).Trim() : ""; }
        catch { return ""; }
    }

    static string? PublicKeyOf(string privateKey)
    {
        var tmp = Path.Combine(PrivDir, "chk");
        try
        {
            Directory.CreateDirectory(tmp);
            var file = Path.Combine(tmp, "k");
            Win.WriteFile(file, privateKey.Replace("\r\n", "\n").TrimEnd() + "\n");
            Win.RestrictToAdmins(file, dir: false);
            var (c, so, _) = Win.Run(Win.SshKeygenExe, new[] { "-y", "-f", file }, null, 15_000);
            return c == 0 ? string.Join(' ', so.Trim().Split(' ').Take(2)) : null;
        }
        catch { return null; }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }

    // ---------- Самопроверка: подключение к самому себе тем же ключом, что получит ПК Unreal ----------
    static string SelfTest(Options o, PairInfo p)
    {
        var tmp = Path.Combine(PrivDir, "selftest");
        Directory.CreateDirectory(tmp);
        Process? ssh = null;
        try
        {
            var key = Path.Combine(tmp, "k");
            var kh = Path.Combine(tmp, "kh");
            Win.WriteFile(key, p.Key.Replace("\r\n", "\n").TrimEnd() + "\n");
            Win.RestrictToAdmins(key, dir: false);
            Win.WriteFile(kh, Tunnel.HostAlias + " " + p.HostKey + "\n");
            var common = new List<string>
            {
                "-i", key, "-p", o.SshPort.ToString(), "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes",
                "-o", "UserKnownHostsFile=" + kh, "-o", "HostKeyAlias=" + Tunnel.HostAlias, "-o", "ConnectTimeout=10", "-o", "LogLevel=ERROR",
            };

            // 1) Вход по ключу и принудительная команда (receive.ps1)
            var req = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"action\":\"selftest\"}"));
            var (c, so, se) = Win.Run(Win.SshExe, common.Append($"{p.User}@127.0.0.1"), req, 60_000);
            if (c != 0) throw new Exception("вход по ключу не прошёл: " + (se + " " + so).Trim());
            JsonNode? j = null;
            try { j = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(so.Split('\n').Last().Trim()))); } catch { }
            if (j?["selftest"]?.GetValue<bool>() != true) throw new Exception("receive.ps1 ответил непонятно: " + so + " " + se);

            // 2) Сервер предлагает только вход по ключу (без пароля и keyboard-interactive)
            var (ac, ao, ae) = Win.Run(Win.SshExe, new[] { "-p", o.SshPort.ToString(), "-o", "BatchMode=yes", "-o", "PubkeyAuthentication=no",
                "-o", "StrictHostKeyChecking=yes", "-o", "UserKnownHostsFile=" + kh, "-o", "HostKeyAlias=" + Tunnel.HostAlias, "-o", "ConnectTimeout=10",
                $"{p.User}@127.0.0.1", "exit" }, null, 30_000);
            var methods = System.Text.RegularExpressions.Regex.Match(ae, @"Permission denied \(([^)]*)\)").Groups[1].Value;
            if (ac == 0 || methods != "publickey")
                throw new Exception("SSH-сервер принимает не только ключи: " + (methods.Length > 0 ? methods : (ae + " " + ao).Trim()));

            // 3) Обратная переадресация: порт на ПК Claude -> локальный приёмник
            using var target = new TcpListener(IPAddress.Loopback, 0);
            target.Start();
            var tport = ((IPEndPoint)target.LocalEndpoint).Port;
            int rport;
            using (var probe = new TcpListener(IPAddress.Loopback, 0)) { probe.Start(); rport = ((IPEndPoint)probe.LocalEndpoint).Port; }
            var psi = new ProcessStartInfo(Win.SshExe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var a in common.Concat(new[] { "-N", "-o", "ExitOnForwardFailure=yes", "-R", $"127.0.0.1:{rport}:127.0.0.1:{tport}", $"{p.User}@127.0.0.1" }))
                psi.ArgumentList.Add(a);
            ssh = Process.Start(psi)!;
            var sw = Stopwatch.StartNew();
            bool forwarded = false;
            while (!forwarded && sw.Elapsed < TimeSpan.FromSeconds(15) && !ssh.HasExited)
            {
                Thread.Sleep(500);
                try
                {
                    using var cl = new TcpClient();
                    if (!cl.ConnectAsync(IPAddress.Loopback, rport).Wait(1000) || !cl.Connected) continue;
                    var acc = target.AcceptTcpClientAsync();
                    if (acc.Wait(5000)) { acc.Result.Dispose(); forwarded = true; }
                }
                catch { }
            }
            if (!forwarded)
                throw new Exception("обратная переадресация не работает" + (ssh.HasExited ? ": " + ssh.StandardError.ReadToEnd().Trim() : ""));
            return "OK: проверено подключением к самому себе — вход только по ключу (пароль не принимается), приём данных и обратная переадресация портов работают";
        }
        finally
        {
            try { if (ssh != null && !ssh.HasExited) ssh.Kill(true); } catch { }
            ssh?.Dispose();
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    // ---------- Состояние ----------
    public static List<string> Status(Options o)
    {
        var r = new List<string>();
        var (code, output) = Ps.Run(@"
$svc = Get-Service sshd -ErrorAction SilentlyContinue
$fw = Get-NetFirewallRule -Name __RULE__ -ErrorAction SilentlyContinue
$l = @(Get-NetTCPConnection -State Listen -LocalPort __PORT__ -ErrorAction SilentlyContinue)
ConvertTo-Json -Compress -InputObject ([ordered]@{ sshd = $(if ($svc) { [string]$svc.Status } else { '' }); fw = [bool]($fw -and $fw.Enabled -eq 'True'); listen = $l.Count -gt 0 })
".Replace("__RULE__", Ps.Q(FwRule)).Replace("__PORT__", o.SshPort.ToString()), 60_000);
        if (code == 0)
        {
            var j = JsonNode.Parse(output)!;
            var sshd = j["sshd"]!.ToString();
            r.Add($"sshd: {(sshd.Length == 0 ? "не установлен" : sshd)}, порт {o.SshPort}: {(j["listen"]!.GetValue<bool>() ? "слушает" : "не слушает")}, "
                  + $"правило брандмауэра: {(j["fw"]!.GetValue<bool>() ? "есть" : "нет")}");
        }
        else r.Add("не удалось проверить sshd: " + output);
        r.Add("Код подключения: " + (File.Exists(CodeFile) ? "создан" : "ещё не создан — нажмите «Настроить ПК Claude»"));

        if (!File.Exists(UnrealInfoFile))
        {
            r.Add("ПК Unreal ещё ни разу не подключался.");
            return r;
        }
        try
        {
            var c = JsonNode.Parse(File.ReadAllText(UnrealInfoFile))!;
            int mcp = c["mcpPort"]!.GetValue<int>(), smb = c["smbPort"]!.GetValue<int>();
            r.Add($"ПК Unreal {c["computer"]} последний раз присылал данные {c["time"]}: проект {c["share"]}, диск {c["drive"]}");
            bool m = Setup.PortOpen(mcp), s = Setup.PortOpen(smb);
            r.Add($"Туннель от ПК Unreal сейчас: MCP 127.0.0.1:{mcp} — {(m ? "открыт" : "закрыт")}, папка 127.0.0.1:{smb} — {(s ? "открыта" : "закрыта")}");
            if (m)
            {
                var (ok, info) = Unreal.McpProbe(new Options { Port = mcp, UrlPath = c["urlPath"]?.ToString() ?? "/mcp" });
                r.Add("MCP через туннель: " + info);
            }
            var drive = c["drive"]!.ToString();
            r.Add($"Диск {drive}: " + (Directory.Exists(drive + "\\") ? "подключён" : "не подключён — запустите «Подключить-UE.cmd» на рабочем столе"));
        }
        catch (Exception ex) { r.Add("не удалось прочитать " + UnrealInfoFile + ": " + ex.Message); }
        return r;
    }

    // ---------- Тексты скриптов ----------
    const string PsCommon = @"
function Test-Port($p) {
    $c = New-Object Net.Sockets.TcpClient
    try { $c.ConnectAsync('127.0.0.1', [int]$p).Wait(1500) -and $c.Connected } catch { $false } finally { $c.Dispose() }
}
";

    static string ReceivePs1 => @"# UeBridge (ПК Claude): принудительная команда ключа ПК Unreal в authorized_keys.
# ПК Unreal присылает (base64 JSON) данные о проекте; скрипт сохраняет их для «Подключить-UE.cmd»
# и проверяет со стороны ПК Claude порты туннеля и MCP-сервер. Ответ — base64 JSON.
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
" + PsCommon + @"
function Out-Result($o) { [Console]::Out.Write([Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(($o | ConvertTo-Json -Compress)))); exit 0 }
function Test-Mcp($port, $path) {
    try {
        $url = 'http://127.0.0.1:' + $port + $path
        $req = [Net.HttpWebRequest]::Create($url)
        $req.Method = 'POST'; $req.ContentType = 'application/json'; $req.Accept = 'application/json, text/event-stream'
        $req.Timeout = 8000; $req.ReadWriteTimeout = 8000
        $b = [Text.Encoding]::UTF8.GetBytes('{""jsonrpc"":""2.0"",""id"":1,""method"":""initialize"",""params"":{""protocolVersion"":""2025-06-18"",""capabilities"":{},""clientInfo"":{""name"":""UeBridge-ClaudePC"",""version"":""1""}}}')
        $s = $req.GetRequestStream(); $s.Write($b, 0, $b.Length); $s.Close()
        $resp = $req.GetResponse()
        $sid = $resp.Headers['Mcp-Session-Id']
        $rd = New-Object IO.StreamReader($resp.GetResponseStream())
        $ok = $false
        while ($null -ne ($line = $rd.ReadLine())) { if ($line -match '""jsonrpc""') { $ok = $line -match '""result""|""error""'; break } }
        $resp.Close()
        if ($sid) { try { $d = [Net.HttpWebRequest]::Create($url); $d.Method = 'DELETE'; $d.Headers.Add('Mcp-Session-Id', $sid); $d.Timeout = 3000; $d.GetResponse().Close() } catch { } }
        if ($ok) { 'ok' } else { 'нет ответа JSON-RPC' }
    } catch { $_.Exception.Message }
}
try {
    $d = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String([Console]::In.ReadToEnd().Trim())) | ConvertFrom-Json
    if ($d.action -eq 'selftest') { Out-Result ([ordered]@{ selftest = $true; user = $env:USERNAME }) }
    if ([string]$d.drive -notmatch '^[D-Z]:$' -or [string]$d.share -notmatch '^[^\\/:*?""<>|]+$' -or [string]$d.urlPath -notmatch '^/[^\s""]*$') { throw 'неверные данные от ПК Unreal' }
    $info = [ordered]@{
        computer = [string]$d.computer; unrealUser = [string]$d.unrealUser; share = [string]$d.share; drive = [string]$d.drive
        mcpPort = [int]$d.mcpPort; urlPath = [string]$d.urlPath; smbPort = [int]$d.smbPort; time = (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
    }
    $dir = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'UeBridge'
    New-Item -ItemType Directory -Force $dir | Out-Null
    ($info | ConvertTo-Json) | Set-Content -Encoding UTF8 (Join-Path $dir 'unrealpc.json')
    Out-Result ([ordered]@{ user = $env:USERNAME; mcpPort = (Test-Port $info.mcpPort); mcp = (Test-Mcp $info.mcpPort $info.urlPath); smbPort = (Test-Port $info.smbPort) })
} catch { [Console]::Error.Write('ПК Claude: ' + $_.Exception.Message); exit 1 }
";

    static string ConnectPs1 => @"# UeBridge (ПК Claude): подключить папку проекта с ПК Unreal через туннель и открыть её в редакторе.
$ErrorActionPreference = 'Stop'
$Host.UI.RawUI.WindowTitle = 'UeBridge: подключение проекта с ПК Unreal'
" + PsCommon + @"
function Stop-WithMessage($msg) { Write-Host $msg -ForegroundColor Yellow; Read-Host 'Нажмите Enter, чтобы закрыть'; exit 1 }
$dir = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'UeBridge'
$infoFile = Join-Path $dir 'unrealpc.json'
if (-not (Test-Path $infoFile)) { Stop-WithMessage 'ПК Unreal ещё ни разу не подключался к этому ПК. На ПК Unreal в UeBridge вставьте код подключения и нажмите «Подключить к ПК Claude».' }
$c = Get-Content $infoFile -Raw -Encoding UTF8 | ConvertFrom-Json

Write-Host ""[1/3] Жду туннель от ПК Unreal ($($c.computer))...""
$n = 0
while (-not ((Test-Port $c.smbPort) -and (Test-Port $c.mcpPort))) {
    if (++$n -gt 30) { Stop-WithMessage 'Туннель от ПК Unreal не работает. Проверьте, что ПК Unreal включён и подключён к интернету: туннель на нём запускается сам. Если редактор Unreal на ПК Unreal закрыт, порт MCP тоже будет закрыт — откройте проект (или нажмите «Настроить всё» в UeBridge на ПК Unreal).' }
    Start-Sleep 2
}

$drive = [string]$c.drive
$remote = '\\127.0.0.1\' + $c.share
Write-Host ""[2/3] Папка проекта $remote -> $drive""
$m = Get-SmbMapping -LocalPath $drive -ErrorAction SilentlyContinue
if ($m -and ($m.RemotePath -ne $remote -or -not (Test-Path ($drive + '\')))) { Remove-SmbMapping -LocalPath $drive -Force -ErrorAction SilentlyContinue; $m = $null }
if (-not $m) {
    $credFile = Join-Path $dir 'unrealpc-cred.xml'
    $ok = $false
    for ($i = 0; $i -lt 3 -and -not $ok; $i++) {
        if (Test-Path $credFile) { $cred = Import-Clixml $credFile }
        else {
            $cred = Get-Credential -UserName $c.unrealUser -Message ""Пароль входа в Windows на ПК Unreal ($($c.computer)). Нужен один раз: дальше он хранится зашифрованным для вашей учётной записи на этом ПК.""
            if (-not $cred) { Stop-WithMessage 'Пароль не введён.' }
        }
        try {
            New-SmbMapping -LocalPath $drive -RemotePath $remote -TcpPort ([int]$c.smbPort) -Credential $cred | Out-Null
            $cred | Export-Clixml $credFile
            $ok = $true
        } catch {
            Write-Host ('Не удалось подключить папку: ' + $_.Exception.Message) -ForegroundColor Yellow
            Remove-Item $credFile -ErrorAction SilentlyContinue
        }
    }
    if (-not $ok) { Stop-WithMessage 'Папка проекта не подключена.' }
}

Write-Host ""[3/3] Открываю $drive\""
$editors = @(@(
    (Join-Path $env:LOCALAPPDATA 'Programs\cursor\Cursor.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Microsoft VS Code\Code.exe'),
    (Join-Path $env:ProgramFiles 'Microsoft VS Code\Code.exe')
) | Where-Object { Test-Path $_ })
if ($editors.Count -gt 0) { Start-Process $editors[0] -ArgumentList ($drive + '\'); exit 0 }
if (Get-Command claude -ErrorAction SilentlyContinue) { Set-Location ($drive + '\'); claude; exit 0 }
Start-Process explorer.exe ($drive + '\')
";
}
