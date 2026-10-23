using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace UeBridge;

/// <summary>Код подключения: ПК Claude создаёт, ПК Unreal вставляет. Внутри адрес ПК Claude, ключ для входа и ключ узла ПК Claude.</summary>
public sealed record PairInfo(string Host, int Port, string User, string Key, string HostKey)
{
    const string Prefix = "UEB1-";

    public string Encode()
    {
        var j = new JsonObject { ["h"] = Host, ["p"] = Port, ["u"] = User, ["k"] = Key, ["hk"] = HostKey };
        return Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(j.ToJsonString())).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static PairInfo Decode(string code)
    {
        code = string.Concat(code.Where(c => !char.IsWhiteSpace(c)));
        if (!code.StartsWith(Prefix)) throw new Exception("это не код подключения UeBridge (он начинается с " + Prefix + ")");
        var b64 = code[Prefix.Length..].Replace('-', '+').Replace('_', '/');
        b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
        JsonNode j;
        try { j = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(b64)))!; }
        catch { throw new Exception("код подключения повреждён — скопируйте его заново целиком"); }
        return new PairInfo(j["h"]!.ToString(), j["p"]!.GetValue<int>(), j["u"]!.ToString(), j["k"]!.ToString(), j["hk"]!.ToString());
    }
}

/// <summary>Общее для обеих сторон: файлы в ProgramData, ssh.exe, права на файлы.</summary>
public static class Win
{
    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "UeBridge");
    public static string SshExe => Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh.exe");
    public static string SshKeygenExe => Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh-keygen.exe");

    /// <summary>Оставить доступ только SYSTEM и Администраторам (SID, чтобы не зависеть от языка Windows).</summary>
    public static void RestrictToAdmins(string path, bool dir) => Icacls(path, dir, usersRead: false);

    /// <summary>Папка UeBridge в ProgramData: менять могут только SYSTEM и Администраторы, пользователи — только читать.
    /// Скрипт туннеля оттуда запускается от SYSTEM, поэтому обычный пользователь не должен иметь права его подменить.</summary>
    public static void SecureDir()
    {
        Directory.CreateDirectory(Dir);
        Icacls(Dir, dir: true, usersRead: true);
    }

    /// <summary>Записать файл заново (удалив прежний, чтобы владельцем стали Администраторы).</summary>
    public static void WriteFile(string path, string text, bool bom = false)
    {
        if (File.Exists(path)) File.Delete(path);
        File.WriteAllText(path, text, new UTF8Encoding(bom));
    }

    static void Icacls(string path, bool dir, bool usersRead)
    {
        var inherit = dir ? "(OI)(CI)" : "";
        var args = new List<string> { path, "/inheritance:r", "/grant:r", $"*S-1-5-18:{inherit}F", "/grant:r", $"*S-1-5-32-544:{inherit}F" };
        if (usersRead) args.AddRange(new[] { "/grant:r", $"*S-1-5-32-545:{inherit}RX" });
        var psi = new ProcessStartInfo("icacls.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new Exception("не удалось задать права на " + path + ": " + err.Trim());
    }

    /// <summary>Запуск ssh/ssh-keygen с аргументами и (необязательно) входными данными.</summary>
    public static (int Code, string Out, string Err) Run(string exe, IEnumerable<string> args, string? stdin = null, int timeoutMs = 60_000)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new Exception("не удалось запустить " + exe);
        var so = p.StandardOutput.ReadToEndAsync();
        var se = p.StandardError.ReadToEndAsync();
        if (stdin != null) p.StandardInput.Write(stdin);
        p.StandardInput.Close();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(true); } catch { }
            return (-1, "", $"нет ответа за {timeoutMs / 1000} с");
        }
        Task.WaitAll(new Task[] { so, se }, 5_000);
        return (p.ExitCode, so.IsCompleted ? so.Result.Trim() : "", se.IsCompleted ? se.Result.Trim() : "");
    }

    /// <summary>Понятное объяснение типичных ошибок ssh.</summary>
    public static string ExplainSsh(string err, string host, int port)
    {
        if (err.Contains("timed out", StringComparison.OrdinalIgnoreCase) || err.Contains("Connection refused", StringComparison.OrdinalIgnoreCase)
            || err.Contains("No route", StringComparison.OrdinalIgnoreCase) || err.Contains("Could not resolve", StringComparison.OrdinalIgnoreCase))
            return $"ПК Claude недоступен по адресу {host}:{port}. Проверьте адрес в коде подключения, что ПК Claude включён и что хостинг ПК Claude пропускает входящие соединения на порт {port}.";
        if (err.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
            return "ПК Claude не принял ключ. Нажмите на ПК Claude «Настроить ПК Claude» и вставьте сюда новый код подключения.";
        if (err.Contains("Host key verification failed", StringComparison.OrdinalIgnoreCase) || err.Contains("HOST IDENTIFICATION HAS CHANGED", StringComparison.OrdinalIgnoreCase))
            return "Ключ узла ПК Claude не совпадает с кодом подключения. Вставьте свежий код с ПК Claude.";
        if (err.Contains("forwarding failed", StringComparison.OrdinalIgnoreCase))
            return "ПК Claude не открыл переадресацию порта: порт на ПК Claude занят (например, старым туннелем) — подождите минуту, туннель переподключится сам.";
        return err;
    }
}

/// <summary>ПК Unreal: обратный SSH-туннель к ПК Claude. ПК Unreal сам подключается к ПК Claude и открывает на ПК Claude (только на 127.0.0.1)
/// порт MCP-сервера Unreal и порт для папки проекта (SMB).</summary>
public static class Tunnel
{
    public const string TaskName = "UeBridge Tunnel";
    public const string HostAlias = "uebridge-claudepc";
    static string SshDir => Path.Combine(Win.Dir, "ssh");
    public static string KeyFile => Path.Combine(SshDir, "claudepc_key");
    static string KnownHosts => Path.Combine(SshDir, "known_hosts");
    static string Script => Path.Combine(Win.Dir, "tunnel.ps1");
    public static string LogFile => Path.Combine(Win.Dir, "tunnel.log");

    /// <summary>Куда на этом ПК ведут порты туннеля. Меняется только в тестах (ПК Claude и ПК Unreal на одной машине).</summary>
    public static string TargetHost = "127.0.0.1";

    public static bool Paired(Options o) => o.ClaudeHost.Length > 0 && File.Exists(KeyFile) && File.Exists(KnownHosts);

    // ---------- Принять код подключения с ПК Claude ----------
    public static string Pair(Options o, string code)
    {
        var p = PairInfo.Decode(code);
        Win.SecureDir();
        Directory.CreateDirectory(SshDir);
        Win.RestrictToAdmins(SshDir, dir: true);
        Win.WriteFile(KeyFile, p.Key.Replace("\r\n", "\n").TrimEnd() + "\n");
        Win.WriteFile(KnownHosts, HostAlias + " " + p.HostKey.Trim() + "\n");
        Win.RestrictToAdmins(KeyFile, dir: false);
        Win.RestrictToAdmins(KnownHosts, dir: false);
        o.ClaudeHost = p.Host;
        o.ClaudePort = p.Port;
        o.ClaudeUser = p.User;
        o.Save();
        return $"OK: код принят — ПК Claude {p.User}@{p.Host}:{p.Port}";
    }

    static List<string> SshArgs(Options o) => new()
    {
        "-i", KeyFile, "-p", o.ClaudePort.ToString(),
        "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes", "-o", "UserKnownHostsFile=" + KnownHosts,
        "-o", "HostKeyAlias=" + HostAlias, "-o", "ConnectTimeout=15", "-o", "LogLevel=ERROR",
    };

    // ---------- Шаг: туннель работает и ПК Claude через него видит MCP и папку ----------
    public static string EnsureTunnel(Options o, Action<string> progress)
    {
        if (!Paired(o)) return "ВНИМАНИЕ: ПК Unreal ещё не связан с ПК Claude — вставьте код подключения с ПК Claude и нажмите «Подключить к ПК Claude».";

        // Скрипт туннеля: ssh в цикле, при обрыве переподключается через 10 с
        var args = SshArgs(o).Concat(new[]
        {
            "-N", "-T", "-o", "ExitOnForwardFailure=yes", "-o", "ServerAliveInterval=15", "-o", "ServerAliveCountMax=3",
            "-R", $"127.0.0.1:{o.Port}:{TargetHost}:{o.Port}", "-R", $"127.0.0.1:{o.SmbTunnelPort}:{TargetHost}:445",
            $"{o.ClaudeUser}@{o.ClaudeHost}",
        });
        var script = new StringBuilder();
        script.Append("# UeBridge: обратный SSH-туннель ПК Unreal -> ПК Claude. Запускается задачей планировщика «" + TaskName + "» от имени SYSTEM.\r\n");
        script.Append("$log = " + Ps.Q(LogFile) + "\r\n");
        script.Append("$sshArgs = @(" + string.Join(", ", args.Select(Ps.Q)) + ")\r\n");
        script.Append("while ($true) {\r\n");
        script.Append("    if ((Test-Path $log) -and (Get-Item $log).Length -gt 1MB) { Remove-Item $log }\r\n");
        script.Append("    Add-Content $log ((Get-Date -Format s) + ' start')\r\n");
        script.Append("    & " + Ps.Q(Win.SshExe) + " @sshArgs 2>&1 | ForEach-Object { Add-Content $log ((Get-Date -Format s) + ' ' + $_) }\r\n");
        script.Append("    Add-Content $log ((Get-Date -Format s) + ' exit ' + $LASTEXITCODE)\r\n");
        script.Append("    Start-Sleep 10\r\n");
        script.Append("}\r\n");
        Win.SecureDir();
        Win.WriteFile(Script, script.ToString(), bom: true);
        Win.RestrictToAdmins(Script, dir: false);

        // Задача планировщика: SYSTEM, при включении ПК, без ограничения времени, перезапуск при сбое
        var xml = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo><Description>UeBridge: обратный SSH-туннель к ПК Claude ({System.Security.SecurityElement.Escape(o.ClaudeHost)})</Description></RegistrationInfo>
  <Triggers><BootTrigger><Enabled>true</Enabled></BootTrigger></Triggers>
  <Principals><Principal id=""Author""><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <StartWhenAvailable>true</StartWhenAvailable>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <RestartOnFailure><Interval>PT1M</Interval><Count>999</Count></RestartOnFailure>
  </Settings>
  <Actions Context=""Author""><Exec><Command>powershell.exe</Command><Arguments>-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File ""{Script}""</Arguments></Exec></Actions>
</Task>";
        var started = DateTime.Now;
        var (code, output) = Ps.Run(@"
$xml = __XML__
Stop-ScheduledTask -TaskName __TASK__ -ErrorAction SilentlyContinue
Get-CimInstance Win32_Process -Filter ""Name = 'powershell.exe'"" | Where-Object { $_.CommandLine -like '*UeBridge\tunnel.ps1*' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Get-CimInstance Win32_Process -Filter ""Name = 'ssh.exe'"" | Where-Object { $_.CommandLine -like '*HostKeyAlias=__ALIAS__*' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Register-ScheduledTask -TaskName __TASK__ -Xml $xml -Force | Out-Null
Start-ScheduledTask -TaskName __TASK__
'OK'
".Replace("__XML__", Ps.Q(xml)).Replace("__TASK__", Ps.Q(TaskName)).Replace("__ALIAS__", HostAlias), 60_000);
        if (code != 0) throw new Exception("не удалось создать задачу планировщика: " + output);
        progress($"Задача планировщика «{TaskName}» запущена: туннель к {o.ClaudeUser}@{o.ClaudeHost}:{o.ClaudePort}, "
                 + $"на ПК Claude откроются 127.0.0.1:{o.Port} (MCP) и 127.0.0.1:{o.SmbTunnelPort} (папка проекта).");

        // Туннель считается поднятым, если ssh проработал 8 с и не вышел
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(40))
        {
            Thread.Sleep(2000);
            var fail = LastFailure(started);
            if (fail != null) throw new Exception("туннель не поднялся: " + Win.ExplainSsh(fail, o.ClaudeHost, o.ClaudePort));
            if (sw.Elapsed > TimeSpan.FromSeconds(8) && SshRunning()) break;
        }
        if (!SshRunning()) throw new Exception("туннель не поднялся за 40 с. Журнал туннеля: " + LogFile);

        var (ok, info) = Check(o);
        return ok ? "OK: туннель работает, " + info : "ВНИМАНИЕ: туннель поднят, но проверка со стороны ПК Claude не прошла — " + info;
    }

    /// <summary>Строка журнала туннеля об ошибке, появившаяся после <paramref name="since"/>; null — ошибок нет.</summary>
    static string? LastFailure(DateTime since)
    {
        try
        {
            if (!File.Exists(LogFile)) return null;
            string? err = null;
            foreach (var line in File.ReadAllLines(LogFile))
            {
                if (line.Length < 20 || !DateTime.TryParseExact(line[..19], "s", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
                    || t < since.AddSeconds(-1)) continue;
                var text = line[20..];
                if (text.StartsWith("exit ")) return err ?? ("ssh завершился с кодом " + text[5..]);
                if (text != "start") err = text;
            }
        }
        catch { }
        return null;
    }

    static bool SshRunning()
    {
        var (code, output) = Ps.Run(@"@(Get-CimInstance Win32_Process -Filter ""Name = 'ssh.exe'"" | Where-Object { $_.CommandLine -like '*HostKeyAlias=__ALIAS__*' -and $_.CommandLine -like '* -N *' }).Count"
            .Replace("__ALIAS__", HostAlias), 30_000);
        return code == 0 && int.TryParse(output, out var n) && n > 0;
    }

    // ---------- Проверка со стороны ПК Claude (через принудительную команду ключа на ПК Claude) ----------
    /// <summary>Передаёт ПК Claude данные о проекте и получает от него результат проверки портов туннеля и MCP.</summary>
    public static (bool Ok, string Info) Check(Options o)
    {
        var info = new JsonObject
        {
            ["action"] = "unreal",
            ["computer"] = Environment.MachineName,
            ["unrealUser"] = Environment.UserDomainName + "\\" + Environment.UserName,
            ["share"] = o.ShareName,
            ["mcpPort"] = o.Port,
            ["urlPath"] = o.UrlPath,
            ["smbPort"] = o.SmbTunnelPort,
            ["drive"] = o.Drive,
        };
        var stdin = Convert.ToBase64String(Encoding.UTF8.GetBytes(info.ToJsonString()));
        var (code, output, err) = Win.Run(Win.SshExe, SshArgs(o).Append($"{o.ClaudeUser}@{o.ClaudeHost}"), stdin, 60_000);
        if (code != 0) return (false, Win.ExplainSsh(err.Length > 0 ? err : output, o.ClaudeHost, o.ClaudePort));
        JsonNode? j = null;
        try { j = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(output.Split('\n').Last().Trim()))); } catch { }
        if (j == null) return (false, "ПК Claude ответил непонятно: " + output + " " + err);
        bool mcpPort = j["mcpPort"]?.GetValue<bool>() ?? false, mcp = j["mcp"]?.ToString() == "ok", smb = j["smbPort"]?.GetValue<bool>() ?? false;
        var text = $"с ПК Claude ({j["user"]}@{o.ClaudeHost}): MCP {(mcp ? "отвечает" : mcpPort ? "порт открыт, но MCP не ответил: " + j["mcp"] : "порт закрыт")}, "
                   + $"папка проекта (порт {o.SmbTunnelPort}) {(smb ? "доступна" : "недоступна")}";
        return (mcp && smb, text);
    }

    /// <summary>Короткое состояние туннеля для строки состояния.</summary>
    public static string State(Options o)
    {
        if (!Paired(o)) return "не настроен";
        return SshRunning() ? "работает" : "не работает";
    }
}
