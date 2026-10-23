using System.Diagnostics;

namespace UeBridge;

public sealed class MainForm : Form
{
    Options _opt = Options.Load();
    bool _busy;

    readonly TextBox txtProject = new() { ReadOnly = true, Dock = DockStyle.Fill };
    readonly Button btnBrowse = new() { Text = "Обзор…", AutoSize = true };
    readonly TextBox txtShare = new() { Dock = DockStyle.Fill };
    readonly NumericUpDown numPort = new() { Minimum = 1, Maximum = 65535, Value = 8000, Dock = DockStyle.Fill };
    readonly TextBox txtPath = new() { Text = "/mcp", Dock = DockStyle.Fill };
    readonly ComboBox cmbHost = new() { DropDownStyle = ComboBoxStyle.DropDown, Dock = DockStyle.Fill };
    readonly ComboBox cmbDrive = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };

    readonly Button btnApply = new() { Text = "Настроить всё", AutoSize = true, Padding = new Padding(6, 2, 6, 2) };
    readonly Button btnUpdate = new() { Text = "Обновить", AutoSize = true, Padding = new Padding(6, 2, 6, 2) };
    readonly Button btnStatus = new() { Text = "Проверить состояние", AutoSize = true, Padding = new Padding(6, 2, 6, 2) };
    readonly Button btnScript = new() { Text = "Сохранить скрипт для ПК Claude…", AutoSize = true, Padding = new Padding(6, 2, 6, 2) };
    readonly Button btnCopy = new() { Text = "Копировать текст для ПК Claude", AutoSize = true, Padding = new Padding(6, 2, 6, 2) };
    readonly TextBox txtPair = new() { Dock = DockStyle.Fill, Font = new Font("Consolas", 9f) };
    readonly Button btnPair = new() { Text = "Подключить к ПК Claude", AutoSize = true, Padding = new Padding(6, 0, 6, 0) };
    readonly Button btnRole = new() { Text = "Это ПК Claude…", AutoSize = true, Padding = new Padding(6, 0, 6, 0) };

    readonly TextBox txtInfo = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, Font = new Font("Consolas", 9.5f) };
    readonly TextBox txtLog = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, Font = new Font("Consolas", 9.5f) };
    readonly Label lblStatus = new() { AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };

    public MainForm()
    {
        Text = $"UE Bridge {Updater.Current} — ПК Unreal: настройка для Claude Code на ПК Claude";
        Font = new Font("Segoe UI", 9.75f);
        ClientSize = new Size(960, 780);
        MinimumSize = new Size(760, 600);
        StartPosition = FormStartPosition.CenterScreen;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(10) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        var p1 = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3 };
        p1.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        p1.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        p1.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        p1.Controls.Add(Lbl("Проект Unreal (.uproject):"), 0, 0);
        p1.Controls.Add(txtProject, 1, 0);
        p1.Controls.Add(btnBrowse, 2, 0);
        root.Controls.Add(p1, 0, 0);

        var p2 = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 10 };
        p2.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        p2.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
        p2.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        p2.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        p2.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        p2.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        p2.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        p2.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        p2.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        p2.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60));
        p2.Controls.Add(Lbl("Имя сетевой папки:"), 0, 0);
        p2.Controls.Add(txtShare, 1, 0);
        p2.Controls.Add(Lbl("Порт MCP:"), 2, 0);
        p2.Controls.Add(numPort, 3, 0);
        p2.Controls.Add(Lbl("Путь:"), 4, 0);
        p2.Controls.Add(txtPath, 5, 0);
        p2.Controls.Add(Lbl("Адрес для ПК Claude:"), 6, 0);
        p2.Controls.Add(cmbHost, 7, 0);
        p2.Controls.Add(Lbl("Диск на ПК Claude:"), 8, 0);
        p2.Controls.Add(cmbDrive, 9, 0);
        root.Controls.Add(p2, 0, 1);

        var pPair = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 4 };
        pPair.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        pPair.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pPair.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        pPair.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        pPair.Controls.Add(Lbl("Код подключения от ПК Claude:"), 0, 0);
        pPair.Controls.Add(txtPair, 1, 0);
        pPair.Controls.Add(btnPair, 2, 0);
        pPair.Controls.Add(btnRole, 3, 0);
        root.Controls.Add(pPair, 0, 2);

        var p3 = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Margin = new Padding(0, 6, 0, 6) };
        p3.Controls.AddRange(new Control[] { btnApply, btnUpdate, btnStatus, btnScript, btnCopy });
        root.Controls.Add(p3, 0, 3);

        var g4 = new GroupBox { Text = "Данные и команды для ПК Claude", Dock = DockStyle.Fill, Padding = new Padding(8) };
        g4.Controls.Add(txtInfo);
        root.Controls.Add(g4, 0, 4);

        var g5 = new GroupBox { Text = "Журнал", Dock = DockStyle.Fill, Padding = new Padding(8) };
        g5.Controls.Add(txtLog);
        root.Controls.Add(g5, 0, 5);

        root.Controls.Add(lblStatus, 0, 6);

        var tip = new ToolTip();
        tip.SetToolTip(btnApply, "Включить OpenSSH Server, расшарить папку проекта, включить плагины MCP в .uproject, записать ini и .mcp.json, "
                                 + "запустить (или перезапустить) Unreal Editor и проверить MCP-сервер");
        tip.SetToolTip(btnUpdate, "Проверить, есть ли новая версия программы на GitHub, и заново применить все настройки");
        tip.SetToolTip(btnStatus, "Только проверить состояние, ничего не менять");
        tip.SetToolTip(btnScript, "Только если ПК Claude и ПК Unreal в одной локальной сети: .cmd-файл, который на ПК Claude подключит папку, поднимет туннель и запустит Claude Code");
        tip.SetToolTip(txtPair, "Если ПК Claude в другой сети (например, сервер в интернете): запустите UeBridge на ПК Claude, нажмите там «Настроить ПК Claude» и вставьте сюда код");
        tip.SetToolTip(btnPair, "Принять код с ПК Claude и запустить обратный SSH-туннель: ПК Unreal сам подключается к ПК Claude и открывает там MCP и папку проекта");
        btnPair.Click += async (_, _) => await Pair();
        btnRole.Click += (_, _) => Program.SwitchRole(this, "claude");

        btnBrowse.Click += (_, _) => Browse();
        btnApply.Click += async (_, _) => await RunAll();
        btnUpdate.Click += async (_, _) => await UpdateAndRunAll();
        btnStatus.Click += async (_, _) => { if (ReadOptions(false)) await RefreshStatus(); };
        btnScript.Click += (_, _) => SaveScript();
        btnCopy.Click += (_, _) => { if (txtInfo.Text.Length > 0) { Clipboard.SetText(txtInfo.Text); Log("Текст для ПК Claude скопирован в буфер обмена"); } };

        FillFromOptions();
        Shown += async (_, _) =>
        {
            Log($"UE Bridge {Updater.Current}. Запускать на ПК Unreal — там, где установлен Unreal Engine.");
            PickCodeFromClipboard();
            if (!File.Exists(_opt.UProject)) await FindProject();
            if (File.Exists(_opt.UProject)) await RefreshStatus();
            else Log("Проект Unreal не найден автоматически: выберите файл .uproject («Обзор…») и нажмите «Настроить всё».");
        };
    }

    /// <summary>Новый ПК Unreal: если в буфере обмена код подключения ПК Claude (например, скопированный в RDP-сеансе), подставить его.</summary>
    void PickCodeFromClipboard()
    {
        try
        {
            var text = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : "";
            if (!text.StartsWith("UEB1-")) return;
            var p = PairInfo.Decode(text);
            if (Tunnel.Paired(_opt) && Tunnel.SameKey(p) && _opt.ClaudeHost == p.Host && _opt.ClaudePort == p.Port) return;
            txtPair.Text = text;
            Log($"В буфере обмена найден код подключения ПК Claude ({p.User}@{p.Host}) — он будет принят при «Настроить всё».");
        }
        catch { }
    }

    /// <summary>Новый ПК Unreal: найти проект самостоятельно (последний открытый).</summary>
    async Task FindProject()
    {
        Log("Ищу проекты Unreal на этом ПК…");
        var list = await Task.Run(Setup.FindProjects);
        if (list.Count == 0) return;
        txtProject.Text = list[0];
        txtShare.Text = DefaultShareName();
        ReadOptions(false);
        Log("Выбран проект: " + list[0] + (list.Count > 1 ? $"  (найдено {list.Count}; другой можно выбрать кнопкой «Обзор…»)" : ""));
        foreach (var other in list.Skip(1).Take(5)) Log("    также найден: " + other);
        Log("Нажмите «Настроить всё».");
    }

    static Label Lbl(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(3, 6, 6, 3) };

    string DefaultShareName()
    {
        var name = Path.GetFileNameWithoutExtension(txtProject.Text);
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Replace(' ', '_');
    }

    void FillFromOptions()
    {
        txtProject.Text = _opt.UProject;
        txtShare.Text = _opt.ShareName.Length > 0 ? _opt.ShareName : (txtProject.Text.Length > 0 ? DefaultShareName() : "");
        numPort.Value = Math.Clamp(_opt.Port, 1, 65535);
        txtPath.Text = string.IsNullOrWhiteSpace(_opt.UrlPath) ? "/mcp" : _opt.UrlPath;

        cmbHost.Items.Clear();
        cmbHost.Items.Add(Environment.MachineName);
        foreach (var (ip, _) in Setup.LocalIPv4()) cmbHost.Items.Add(ip);
        cmbHost.Text = string.IsNullOrWhiteSpace(_opt.Host) ? Environment.MachineName : _opt.Host;

        cmbDrive.Items.Clear();
        for (char c = 'Z'; c >= 'H'; c--) cmbDrive.Items.Add(c + ":");
        cmbDrive.SelectedItem = cmbDrive.Items.Contains(_opt.Drive) ? _opt.Drive : "Z:";
    }

    bool ReadOptions(bool requireProject)
    {
        _opt.UProject = txtProject.Text.Trim();
        _opt.ShareName = txtShare.Text.Trim();
        _opt.Port = (int)numPort.Value;
        var path = txtPath.Text.Trim();
        if (path.Length == 0) path = "/mcp";
        if (!path.StartsWith('/')) path = "/" + path;
        _opt.UrlPath = path;
        txtPath.Text = path;
        _opt.Host = cmbHost.Text.Trim();
        _opt.Drive = cmbDrive.SelectedItem?.ToString() ?? "Z:";
        _opt.Save();

        if (requireProject)
        {
            if (!File.Exists(_opt.UProject))
            {
                MessageBox.Show(this, "Выберите файл проекта .uproject.", "UE Bridge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            if (_opt.ShareName.Length == 0 || _opt.ShareName.IndexOfAny(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }) >= 0)
            {
                MessageBox.Show(this, "Имя сетевой папки пустое или содержит недопустимые символы.", "UE Bridge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
        }
        return true;
    }

    void Browse()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Выберите файл проекта Unreal",
            Filter = "Проект Unreal (*.uproject)|*.uproject",
            CheckFileExists = true,
        };
        var dir = Path.GetDirectoryName(txtProject.Text);
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) dlg.InitialDirectory = dir;
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            txtProject.Text = dlg.FileName;
            if (txtShare.Text.Length == 0) txtShare.Text = DefaultShareName();
            ReadOptions(false);
        }
    }

    void SetBusy(bool busy)
    {
        _busy = busy;
        foreach (var b in new[] { btnApply, btnUpdate, btnStatus, btnScript, btnBrowse, btnPair, btnRole }) b.Enabled = !busy;
        UseWaitCursor = busy;
    }

    void Log(string s)
    {
        if (InvokeRequired) { BeginInvoke(() => Log(s)); return; }
        txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {s}\r\n");
    }

    async Task<bool> Step(string title, Func<string> work)
    {
        Log("• " + title);
        var sw = Stopwatch.StartNew();
        var task = Task.Run(work);
        while (await Task.WhenAny(task, Task.Delay(30_000)) != task)
            Log($"    …ещё выполняется ({sw.Elapsed:m\\:ss})");
        try
        {
            var result = await task;
            Log("    " + result.Replace("\n", "\r\n    "));
            return !result.StartsWith("ВНИМАНИЕ");
        }
        catch (Exception ex)
        {
            Log("    ОШИБКА: " + ex.Message.Replace("\n", "\r\n    "));
            return false;
        }
    }

    async Task RunAll()
    {
        if (_busy || !ReadOptions(true)) return;
        SetBusy(true);
        try
        {
            var o = _opt;
            Log("=== Настройка ПК Unreal ===");
            int failed = 0;
            // Вставленный (или взятый из буфера обмена) код подключения принимаем первым шагом
            var code = txtPair.Text.Trim();
            if (code.Length > 0)
            {
                if (await Step("Код подключения ПК Claude", () => Tunnel.Pair(o, code))) txtPair.Text = "";
                else failed++;
            }
            var paired = Tunnel.Paired(o);
            // В режиме туннеля ПК Unreal сам подключается к ПК Claude, свой SSH-сервер ему не нужен
            if (!paired && !await Step("OpenSSH Server: служба sshd, автозапуск, правило брандмауэра на порт 22", () => Setup.EnsureOpenSsh(s => Log("    " + s)))) failed++;
            if (!await Step($"Сетевая папка \\\\{Environment.MachineName}\\{o.ShareName} -> {o.ProjectDir}", () => Setup.EnsureShare(o))) failed++;
            if (!await Step("Плагины ModelContextProtocol и AllToolsets в .uproject", () => Setup.EnableProjectPlugins(o))) failed++;
            if (!await Step($"Порт MCP {o.Port}: не занят ли другой программой", () => Unreal.EnsurePort(o, p => Invoke(new Action(() => numPort.Value = p))))) failed++;
            if (!await Step("Настройки MCP-сервера в Saved/Config/WindowsEditor/EditorPerProjectUserSettings.ini", () => Setup.WriteIni(o))) failed++;
            if (!await Step(".mcp.json в папке проекта", () => Setup.WriteMcpJson(o))) failed++;
            if (!await Step("Проверка движка Unreal", () => Setup.CheckEngine(o))) failed++;
            if (!await Step("Unreal Editor и MCP-сервер: запуск и проверка по протоколу MCP", () => Unreal.EnsureMcp(o, s => Log("    " + s), Confirm))) failed++;
            if (paired && !await Step($"Туннель к ПК Claude ({o.ClaudeHost}) и проверка со стороны ПК Claude", () => Tunnel.EnsureTunnel(o, s => Log("    " + s)))) failed++;
            Log(failed == 0 ? "=== Готово, ошибок нет ===" : $"=== Готово, замечаний: {failed} (см. выше) ===");
            await RefreshStatusCore();
        }
        finally { SetBusy(false); }
    }

    async Task Pair()
    {
        if (_busy || !ReadOptions(true)) return;
        if (txtPair.Text.Trim().Length == 0)
        {
            MessageBox.Show(this, "Вставьте код подключения: его показывает UeBridge на ПК Claude после «Настроить ПК Claude».", "UE Bridge", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        SetBusy(true);
        try
        {
            var o = _opt;
            var code = txtPair.Text;
            Log("=== Подключение к ПК Claude ===");
            if (await Step("Код подключения", () => Tunnel.Pair(o, code)))
            {
                txtPair.Text = "";
                await Step($"Туннель к ПК Claude ({o.ClaudeHost}) и проверка со стороны ПК Claude", () => Tunnel.EnsureTunnel(o, s => Log("    " + s)));
            }
            await RefreshStatusCore();
        }
        finally { SetBusy(false); }
    }

    async Task UpdateAndRunAll()
    {
        if (_busy || !ReadOptions(false)) return;
        SetBusy(true);
        try
        {
            Log($"Проверяю новую версию программы на GitHub ({Updater.Repo})…");
            var ch = await Updater.CheckAsync();
            if (ch.Latest != null && ch.Latest > Updater.Current)
            {
                Log($"Доступна версия {ch.Latest} (у вас {Updater.Current}). Открываю страницу релиза: скачайте UeBridge.exe и замените этот файл.");
                try { Process.Start(new ProcessStartInfo(Updater.ReleasesPage) { UseShellExecute = true }); }
                catch (Exception ex) { Log("Не удалось открыть браузер: " + ex.Message + "  Адрес: " + Updater.ReleasesPage); }
            }
            else
            {
                Log(ch.Error != null
                    ? "Проверка версии: " + ch.Error
                    : $"У вас последняя версия {Updater.Current}.");
            }
        }
        finally { SetBusy(false); }
        await RunAll();
    }

    async Task RefreshStatus()
    {
        if (_busy) return;
        SetBusy(true);
        try { await RefreshStatusCore(); }
        finally { SetBusy(false); }
    }

    async Task RefreshStatusCore()
    {
        var o = _opt;
        var s = await Task.Run(() => Setup.GetStatus(o));
        var (fix, notes) = Unreal.Diagnose(o, s);
        txtInfo.Text = Setup.ClaudePcInfo(o, s);
        var sshd = string.IsNullOrEmpty(s.Sshd) ? "не установлен" : s.Sshd;
        var editor = s.Editors.Any(e => e.ThisProject) ? "открыт" : s.Editors.Count > 0 ? "другой проект" : "нет";
        lblStatus.Text = $"sshd: {sshd}   |   порт 22: {(s.Fw22 ? "открыт" : "закрыт")}   |   папка: {(s.SharePath.Length > 0 ? "есть" : "нет")}   |   "
                       + $"плагины: {(s.PluginsOn ? "вкл" : "выкл")}   |   редактор: {editor}   |   MCP {o.Port}: {(s.McpOk ? "отвечает" : "нет")}"
                       + (Tunnel.Paired(o) ? $"   |   туннель к ПК Claude: {s.TunnelState}" : "");
        if (fix == McpFix.None)
        {
            Log($"MCP-сервер Unreal отвечает на {o.McpUrl} ({s.McpInfo}). Можно подключаться с ПК Claude.");
            return;
        }
        Log("MCP-сервер Unreal не отвечает. Диагностика:");
        foreach (var n in notes) Log("    " + n);
        Log(fix == McpFix.Unknown
            ? "    Автоматического исправления для этого случая нет — пришлите этот журнал."
            : "    Нажмите «Настроить всё» — программа исправит это сама.");
    }

    bool Confirm(string message) => (bool)Invoke(new Func<bool>(() =>
        MessageBox.Show(this, message, "UE Bridge", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes));

    void SaveScript()
    {
        if (!ReadOptions(true)) return;
        using var dlg = new SaveFileDialog
        {
            Title = "Сохранить скрипт для ПК Claude",
            Filter = "Командный файл (*.cmd)|*.cmd",
            FileName = "Подключить-UE-локальная-сеть.cmd",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        File.WriteAllText(dlg.FileName, Setup.ClaudePcScript(_opt), new System.Text.UTF8Encoding(false));
        Log("Скрипт для ПК Claude сохранён: " + dlg.FileName + " — перенесите его на ПК Claude и запустите.");
    }
}
