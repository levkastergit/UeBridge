using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace UeBridge;

public sealed record EditorProc(int Pid, DateTime Started, string CommandLine, bool ThisProject);
public sealed record Listener(string Address, int Port, int Pid, string Process);

public enum McpFix { None, WaitEditor, LaunchEditor, RestartEditor, ChangePort, Unknown }

/// <summary>Unreal Editor и его MCP-сервер: проверка, диагностика, запуск и перезапуск редактора.</summary>
public static class Unreal
{
    // ---------- Процессы редактора и слушающие порты ----------
    public static (List<EditorProc> Editors, List<Listener> Listeners) Scan(Options o)
    {
        var (code, output) = Ps.Run(@"
$port = __PORT__
$eds = @(Get-CimInstance Win32_Process -Filter ""Name = 'UnrealEditor.exe'"" | ForEach-Object {
    [pscustomobject]@{ pid = [int]$_.ProcessId; start = $(if ($_.CreationDate) { $_.CreationDate.ToString('o') } else { '' }); cmd = [string]$_.CommandLine } })
$ids = @($eds | ForEach-Object { $_.pid })
$ls = @(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue |
    Where-Object { $_.LocalPort -eq $port -or $ids -contains [int]$_.OwningProcess } | ForEach-Object {
        $p = Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue
        [pscustomobject]@{ addr = [string]$_.LocalAddress; port = [int]$_.LocalPort; pid = [int]$_.OwningProcess; name = $(if ($p) { $p.ProcessName } else { '' }) } })
ConvertTo-Json -Compress -Depth 4 -InputObject ([ordered]@{ editors = $eds; listen = $ls })
".Replace("__PORT__", o.Port.ToString()), 60_000);
        if (code != 0) throw new Exception("не удалось получить список процессов и портов: " + output);

        var j = JsonNode.Parse(output)!;
        var editors = new List<EditorProc>();
        foreach (var e in j["editors"]?.AsArray() ?? new JsonArray())
        {
            var cmd = e?["cmd"]?.ToString() ?? "";
            var started = DateTimeOffset.TryParse(e?["start"]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
                ? t.LocalDateTime : DateTime.MinValue;
            editors.Add(new EditorProc(e!["pid"]!.GetValue<int>(), started, cmd, IsThisProject(cmd, o)));
        }
        var listeners = new List<Listener>();
        foreach (var l in j["listen"]?.AsArray() ?? new JsonArray())
            listeners.Add(new Listener(l!["addr"]?.ToString() ?? "", l["port"]!.GetValue<int>(), l["pid"]!.GetValue<int>(), l["name"]?.ToString() ?? ""));
        return (editors, listeners);
    }

    static bool IsThisProject(string cmd, Options o) =>
        o.UProject.Length > 0 && cmd.Replace('/', '\\').Contains(o.UProject.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase);

    // ---------- Проверка MCP по протоколу: запрос initialize (MCP Streamable HTTP) ----------
    public static (bool Ok, string Info) McpProbe(Options o) => McpProbeAsync(o).GetAwaiter().GetResult();

    static async Task<(bool, string)> McpProbeAsync(Options o)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var http = new HttpClient();
        try
        {
            var body = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\","
                       + "\"capabilities\":{},\"clientInfo\":{\"name\":\"UeBridge\",\"version\":\"" + Updater.Current + "\"}}}";
            using var req = new HttpRequestMessage(HttpMethod.Post, o.McpUrl) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            req.Headers.Accept.ParseAdd("application/json");
            req.Headers.Accept.ParseAdd("text/event-stream");
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            string json = "";
            using (var sr = new StreamReader(await resp.Content.ReadAsStreamAsync(cts.Token)))
            {
                if (resp.Content.Headers.ContentType?.MediaType == "text/event-stream")
                {
                    string? line;
                    while ((line = await sr.ReadLineAsync(cts.Token)) != null)
                        if (line.StartsWith("data:")) { json = line[5..].Trim(); break; }
                }
                else json = await sr.ReadToEndAsync(cts.Token);
            }

            // Сессию, открытую проверкой, закрываем, чтобы не копить их в редакторе
            if (resp.Headers.TryGetValues("Mcp-Session-Id", out var ids))
            {
                try
                {
                    using var del = new HttpRequestMessage(HttpMethod.Delete, o.McpUrl);
                    del.Headers.Add("Mcp-Session-Id", ids.First());
                    using var _ = await http.SendAsync(del, cts.Token);
                }
                catch { }
            }

            JsonNode? j = null;
            try { j = JsonNode.Parse(json); } catch { }
            if (j?["result"] is JsonObject res)
            {
                var name = $"{res["serverInfo"]?["name"]} {res["serverInfo"]?["version"]}".Trim();
                return (true, "MCP отвечает" + (name.Length > 0 ? ": " + name : "") + $", протокол {res["protocolVersion"]}");
            }
            // Ответ JSON-RPC с ошибкой тоже значит, что MCP-сервер работает — просто не принял тестовый запрос
            if (j?["jsonrpc"] != null && j["error"] != null)
                return (true, $"MCP отвечает (на тестовый запрос — ошибка: {j["error"]?["message"]})");
            return (false, resp.IsSuccessStatusCode
                ? "неожиданный ответ MCP: " + Short(json)
                : $"{o.McpUrl} ответил HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} {Short(json)}".Trim());
        }
        catch (OperationCanceledException) { return (false, $"{o.McpUrl}: нет ответа за 8 с"); }
        catch (Exception ex) { return (false, $"{o.McpUrl}: {ex.Message}"); }
    }

    static string Short(string s) => s.Length > 300 ? s[..300] + "…" : s;

    // ---------- Журнал редактора: строки о MCP-сервере ----------
    public static List<string> EditorLog(Options o)
    {
        var file = Path.Combine(o.ProjectDir, "Saved", "Logs", Path.GetFileNameWithoutExtension(o.UProject) + ".log");
        var lines = new List<string>();
        try
        {
            if (!File.Exists(file)) return lines;
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length > 4_000_000) fs.Seek(-4_000_000, SeekOrigin.End);
            using var sr = new StreamReader(fs);
            string? line;
            while ((line = sr.ReadLine()) != null)
                if (line.Contains("ModelContextProtocol", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("LogHttpListener", StringComparison.OrdinalIgnoreCase))
                    lines.Add(line.Trim());
        }
        catch { }
        return lines.TakeLast(8).ToList();
    }

    // ---------- Диагностика: почему MCP не отвечает и что с этим сделать ----------
    public static (McpFix Fix, List<string> Notes) Diagnose(Options o, Status s)
    {
        var notes = new List<string>();
        if (s.McpOk) return (McpFix.None, notes);
        var fix = DiagnoseCore(o, s, notes);
        if (s.EditorLog.Count > 0)
        {
            notes.Add("Журнал редактора (" + Path.GetFileName(o.UProject).Replace(".uproject", ".log") + "), строки о MCP:");
            notes.AddRange(s.EditorLog.Select(l => "  " + l));
        }
        return (fix, notes);
    }

    static McpFix DiagnoseCore(Options o, Status s, List<string> notes)
    {
        notes.Add("MCP не отвечает: " + s.McpInfo);
        if (!s.PluginsOn) notes.Add($"В .uproject не включены плагины {Setup.PluginMcp} и {Setup.PluginTools}.");
        if (!s.IniOk) notes.Add("В EditorPerProjectUserSettings.ini нет нужных настроек MCP (автозапуск, порт, путь).");

        var mine = s.Editors.Where(e => e.ThisProject).ToList();
        var mineIds = mine.Select(e => e.Pid).ToHashSet();
        var busy = s.Listeners.Where(l => l.Port == o.Port && !mineIds.Contains(l.Pid)).ToList();
        if (busy.Count > 0)
        {
            notes.Add($"Порт {o.Port} занят другой программой: " + string.Join(", ", busy.Select(Who)) + ".");
            return McpFix.ChangePort;
        }
        if (mine.Count == 0)
        {
            foreach (var e in s.Editors) notes.Add($"Запущен Unreal Editor с другим проектом (PID {e.Pid}): {e.CommandLine}");
            notes.Add("Unreal Editor с этим проектом не запущен.");
            return McpFix.LaunchEditor;
        }

        var ed = mine[0];
        var own = s.Listeners.Where(l => mineIds.Contains(l.Pid)).ToList();
        var onPort = own.Where(l => l.Port == o.Port).ToList();
        if (onPort.Count > 0)
        {
            notes.Add($"Редактор слушает порт {o.Port} ({string.Join(", ", onPort.Select(l => l.Address))}), но MCP-запрос к {o.McpUrl} не прошёл.");
            if (onPort.All(l => l.Address.Contains(':')))
                notes.Add("Сервер слушает только IPv6, а проверка и SSH-туннель идут на 127.0.0.1.");
            return McpFix.Unknown;
        }
        if (own.Count > 0)
            notes.Add("Редактор слушает другие порты: " + string.Join(", ", own.Select(l => $"{l.Address}:{l.Port}")) + ".");

        if (ed.Started < s.ConfigChanged)
        {
            notes.Add($"Редактор (PID {ed.Pid}) запущен в {ed.Started:HH:mm:ss}, а плагины/настройки изменены позже, в {s.ConfigChanged:HH:mm:ss}: "
                      + "редактор их не загрузил, нужен перезапуск.");
            return McpFix.RestartEditor;
        }
        if (DateTime.Now - ed.Started < TimeSpan.FromMinutes(5))
        {
            notes.Add($"Редактор (PID {ed.Pid}) запущен недавно, в {ed.Started:HH:mm:ss}, и, возможно, ещё загружается.");
            return McpFix.WaitEditor;
        }
        notes.Add($"Редактор (PID {ed.Pid}) запущен в {ed.Started:HH:mm:ss}, после изменения настроек, но MCP-сервер в нём не запущен.");
        return McpFix.RestartEditor;
    }

    static string Who(Listener l) => $"{(l.Process.Length > 0 ? l.Process : "?")} (PID {l.Pid}, {l.Address})";

    // ---------- Шаг: свободен ли порт MCP; если занят чужой программой — выбрать другой ----------
    public static string EnsurePort(Options o, Action<int> onChanged)
    {
        var (editors, listeners) = Scan(o);
        var mine = editors.Where(e => e.ThisProject).Select(e => e.Pid).ToHashSet();
        var onPort = listeners.Where(l => l.Port == o.Port).ToList();
        var busy = onPort.Where(l => !mine.Contains(l.Pid)).ToList();
        if (busy.Count == 0)
            return onPort.Count > 0 ? $"OK: порт {o.Port} слушает Unreal Editor этого проекта" : $"OK: порт {o.Port} свободен";

        var used = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(e => e.Port).ToHashSet();
        var free = Enumerable.Range(o.Port + 1, 200).FirstOrDefault(p => p <= 65535 && !used.Contains(p));
        if (free == 0) throw new Exception($"порт {o.Port} занят ({string.Join(", ", busy.Select(Who))}), а свободный порт рядом не найден");
        var old = o.Port;
        o.Port = free;
        o.Save();
        onChanged(free);
        return $"OK: порт {old} занят ({string.Join(", ", busy.Select(Who))}), MCP переведён на порт {free}. "
               + "Команды для ПК Claude обновлены — если скрипт для ПК Claude уже сохраняли, сохраните его заново.";
    }

    // ---------- Шаг: добиться, чтобы MCP-сервер отвечал (запуск/перезапуск редактора) ----------
    public static string EnsureMcp(Options o, Action<string> progress, Func<string, bool> confirm)
    {
        var s = Setup.GetStatus(o);
        var (fix, notes) = Diagnose(o, s);
        if (fix == McpFix.None) return "OK: " + s.McpInfo;
        foreach (var n in notes) progress(n);

        if (fix == McpFix.WaitEditor)
        {
            progress("Жду, пока редактор загрузится и запустит MCP-сервер (до 10 мин)…");
            if (WaitMcp(o, s.Editors.First(e => e.ThisProject).Pid, TimeSpan.FromMinutes(10), progress, out var info)) return "OK: " + info;
            progress("MCP-сервер так и не запустился.");
            fix = McpFix.RestartEditor;
        }
        if (fix == McpFix.RestartEditor)
        {
            if (!confirm("Чтобы запустить MCP-сервер, Unreal Editor с этим проектом нужно перезапустить.\n\n"
                         + "UeBridge закроет редактор так же, как кнопка × в его окне: если есть несохранённые изменения, "
                         + "редактор сам предложит их сохранить. Затем проект откроется заново с запущенным MCP-сервером.\n\n"
                         + "Перезапустить редактор сейчас?"))
                return "ВНИМАНИЕ: перезапуск редактора отменён, MCP-сервер не запущен. Нажмите «Настроить всё», когда будет удобно перезапустить редактор.";
            foreach (var e in s.Editors.Where(e => e.ThisProject)) CloseEditor(e.Pid, progress);
            // При выходе редактор сохраняет свои настройки и мог перезаписать ini
            progress(Setup.WriteIni(o));
            fix = McpFix.LaunchEditor;
        }
        if (fix == McpFix.LaunchEditor)
        {
            var pid = LaunchEditor(o, progress);
            progress("Жду запуска MCP-сервера (первая загрузка проекта после включения плагинов может идти долго, ограничение — 15 мин)…");
            if (WaitMcp(o, pid, TimeSpan.FromMinutes(15), progress, out var info)) return "OK: " + info;
            throw new Exception("MCP-сервер не запустился.\n" + string.Join("\n", Diagnose(o, Setup.GetStatus(o)).Notes));
        }
        if (fix == McpFix.ChangePort)
            throw new Exception($"порт {o.Port} занят другой программой — нажмите «Настроить всё» ещё раз, программа выберет свободный порт");
        throw new Exception("автоматического исправления для этого случая нет. Пришлите журнал целиком.");
    }

    static bool WaitMcp(Options o, int pid, TimeSpan timeout, Action<string> progress, out string info)
    {
        var sw = Stopwatch.StartNew();
        info = "";
        while (sw.Elapsed < timeout)
        {
            if (Setup.PortOpen(o.Port))
            {
                bool ok;
                (ok, info) = McpProbe(o);
                if (ok) return true;
            }
            if (!IsRunning(pid))
            {
                progress($"Unreal Editor (PID {pid}) закрылся, не дождавшись запуска MCP-сервера.");
                return false;
            }
            Thread.Sleep(5000);
        }
        return false;
    }

    static bool IsRunning(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }

    static void CloseEditor(int pid, Action<string> progress)
    {
        Process p;
        try { p = Process.GetProcessById(pid); }
        catch (ArgumentException) { return; }
        using (p)
        {
            progress(p.CloseMainWindow()
                ? $"Закрываю Unreal Editor (PID {pid}). Если он спросит о сохранении — ответьте в его окне."
                : $"Unreal Editor (PID {pid}) не принял команду закрытия — закройте его сами, программа ждёт.");
            if (!p.WaitForExit(15 * 60_000)) throw new Exception("Unreal Editor не закрылся за 15 минут");
        }
        progress("Unreal Editor закрыт.");
        Thread.Sleep(2000);
    }

    static string? EditorExe(Options o)
    {
        var dir = Setup.EngineDir(o);
        if (dir == null) return null;
        var exe = Path.GetFullPath(Path.Combine(dir, "Engine", "Binaries", "Win64", "UnrealEditor.exe"));
        return File.Exists(exe) ? exe : null;
    }

    static int LaunchEditor(Options o, Action<string> progress)
    {
        var exe = EditorExe(o);
        // Флаги командной строки из документации Epic «Unreal MCP in Unreal Editor»
        var args = $"\"{o.UProject}\" -ModelContextProtocolStartServer -ModelContextProtocolPort={o.Port}";
        int pid = 0;
        if (UProjectAssociated())
        {
            // Открываем сам .uproject через Проводник — как двойной щелчок: редактор запускается от имени пользователя,
            // без прав администратора. MCP-сервер стартует по автозапуску из ini (его записывает шаг настроек).
            // Вспомогательные .cmd во временной папке не создаём: антивирус блокирует такой запуск.
            progress("Открываю проект в Unreal Editor (как двойным щелчком по " + Path.GetFileName(o.UProject) + ")");
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + o.UProject + "\"") { UseShellExecute = false })?.Dispose();
            pid = WaitEditorProcess(o, TimeSpan.FromSeconds(90));
            if (pid == 0) progress("Через Проводник редактор не открылся.");
        }
        if (pid == 0)
        {
            if (exe == null) throw new Exception("не найден UnrealEditor.exe движка этого проекта, а .uproject не открывается двойным щелчком");
            progress($"Запускаю Unreal Editor напрямую: \"{exe}\" {args}");
            Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, WorkingDirectory = o.ProjectDir })?.Dispose();
            pid = WaitEditorProcess(o, TimeSpan.FromSeconds(90));
        }
        if (pid == 0) throw new Exception("Unreal Editor с этим проектом не запустился");
        progress($"Unreal Editor запущен (PID {pid}).");
        return pid;
    }

    /// <summary>Открывается ли .uproject двойным щелчком (связь, которую регистрирует Epic Games Launcher / UnrealVersionSelector).</summary>
    static bool UProjectAssociated()
    {
        try
        {
            using var ext = Registry.ClassesRoot.OpenSubKey(".uproject");
            var progId = ext?.GetValue("") as string;
            if (string.IsNullOrEmpty(progId)) return false;
            using var cmd = Registry.ClassesRoot.OpenSubKey(progId + @"\shell\open\command");
            return !string.IsNullOrEmpty(cmd?.GetValue("") as string);
        }
        catch { return false; }
    }

    static int WaitEditorProcess(Options o, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            try
            {
                var ed = Scan(o).Editors.FirstOrDefault(e => e.ThisProject);
                if (ed != null) return ed.Pid;
            }
            catch { }
            Thread.Sleep(3000);
        }
        return 0;
    }
}
