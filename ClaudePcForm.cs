using System.Diagnostics;

namespace UeBridge;

/// <summary>Окно для ПК Claude — компьютера с Claude Code. ПК Unreal подключается сюда обратным SSH-туннелем.</summary>
public sealed class ClaudePcForm : Form
{
    readonly Options _opt = Options.Load();
    bool _busy;

    readonly ComboBox cmbAddr = new() { DropDownStyle = ComboBoxStyle.DropDown, Dock = DockStyle.Fill };
    readonly NumericUpDown numSsh = new() { Minimum = 1, Maximum = 65535, Value = 22, Dock = DockStyle.Fill };

    readonly Button btnSetup = new() { Text = "Настроить ПК Claude", AutoSize = true, Padding = new Padding(6, 2, 6, 2) };
    readonly Button btnStatus = new() { Text = "Проверить состояние", AutoSize = true, Padding = new Padding(6, 2, 6, 2) };
    readonly Button btnConnect = new() { Text = "Подключить проект", AutoSize = true, Padding = new Padding(6, 2, 6, 2) };
    readonly Button btnNewCode = new() { Text = "Новый код", AutoSize = true, Padding = new Padding(6, 2, 6, 2) };
    readonly Button btnRole = new() { Text = "Это ПК Unreal…", AutoSize = true, Padding = new Padding(6, 2, 6, 2) };
    readonly Button btnCopy = new() { Text = "Копировать код", AutoSize = true, Padding = new Padding(6, 2, 6, 2), Dock = DockStyle.Right };

    readonly TextBox txtCode = new() { Multiline = true, ReadOnly = true, WordWrap = true, Dock = DockStyle.Fill, Font = new Font("Consolas", 9f), Height = 70 };
    readonly TextBox txtLog = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, Font = new Font("Consolas", 9.5f) };

    public ClaudePcForm()
    {
        Text = $"UE Bridge {Updater.Current} — ПК Claude: приём туннеля от ПК Unreal";
        Font = new Font("Segoe UI", 9.75f);
        ClientSize = new Size(900, 640);
        MinimumSize = new Size(700, 500);
        StartPosition = FormStartPosition.CenterScreen;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(10) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        var p1 = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 4 };
        p1.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        p1.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        p1.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        p1.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        p1.Controls.Add(Lbl("Адрес этого ПК для ПК Unreal:"), 0, 0);
        p1.Controls.Add(cmbAddr, 1, 0);
        p1.Controls.Add(Lbl("SSH-порт:"), 2, 0);
        p1.Controls.Add(numSsh, 3, 0);
        root.Controls.Add(p1, 0, 0);

        var p2 = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Margin = new Padding(0, 6, 0, 6) };
        p2.Controls.AddRange(new Control[] { btnSetup, btnStatus, btnConnect, btnNewCode, btnRole });
        root.Controls.Add(p2, 0, 1);

        var g3 = new GroupBox { Text = "Код подключения — вставьте его в UeBridge на ПК Unreal", Dock = DockStyle.Top, Height = 120, Padding = new Padding(8) };
        g3.Controls.Add(txtCode);
        g3.Controls.Add(btnCopy);
        root.Controls.Add(g3, 0, 2);

        var g4 = new GroupBox { Text = "Журнал", Dock = DockStyle.Fill, Padding = new Padding(8) };
        g4.Controls.Add(txtLog);
        root.Controls.Add(g4, 0, 3);

        var tip = new ToolTip();
        tip.SetToolTip(btnSetup, "Установить и настроить OpenSSH Server (вход только по ключу), открыть порт в брандмауэре, создать ключ для ПК Unreal и код подключения, проверить всё подключением к самому себе");
        tip.SetToolTip(btnConnect, "Подключить папку проекта с ПК Unreal и открыть её в Cursor / VS Code (то же, что «Подключить-UE.cmd» на рабочем столе)");
        tip.SetToolTip(btnNewCode, "Создать новый ключ для ПК Unreal: старый код перестанет действовать");
        tip.SetToolTip(btnCopy, "Код содержит ключ, которым ПК Unreal может только открыть туннель к этому ПК. Не публикуйте его.");

        btnSetup.Click += async (_, _) => await RunSetup(renew: false);
        btnNewCode.Click += async (_, _) =>
        {
            if (MessageBox.Show(this, "Создать новый ключ? Старый код подключения перестанет действовать, на ПК Unreal нужно будет вставить новый.",
                    "UE Bridge", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                await RunSetup(renew: true);
        };
        btnStatus.Click += async (_, _) => await RefreshStatus();
        btnConnect.Click += (_, _) => Connect();
        btnCopy.Click += (_, _) => { if (txtCode.Text.Length > 0) { Clipboard.SetText(txtCode.Text); Log("Код подключения скопирован в буфер обмена. Вставьте его в UeBridge на ПК Unreal."); } };
        btnRole.Click += (_, _) => Program.SwitchRole(this, "unreal");

        foreach (var ip in ClaudePc.Addresses()) cmbAddr.Items.Add(ip);
        cmbAddr.Text = _opt.PublicHost.Length > 0 ? _opt.PublicHost : cmbAddr.Items.Count > 0 ? cmbAddr.Items[0]!.ToString() : "";
        numSsh.Value = Math.Clamp(_opt.SshPort, 1, 65535);

        Shown += async (_, _) =>
        {
            Log($"UE Bridge {Updater.Current}, режим ПК Claude — компьютер с Claude Code. ПК Unreal (с Unreal Engine) подключается сюда сам через интернет.");
            Log("Порядок: «Настроить ПК Claude» → скопировать код → вставить его в UeBridge на ПК Unreal → здесь «Подключить проект».");
            txtCode.Text = ClaudePc.StoredCode();
            await RefreshStatus();
        };
    }

    static Label Lbl(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(3, 6, 6, 3) };

    void Log(string s)
    {
        if (InvokeRequired) { BeginInvoke(() => Log(s)); return; }
        txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {s}\r\n");
    }

    void SetBusy(bool busy)
    {
        _busy = busy;
        foreach (var b in new[] { btnSetup, btnStatus, btnConnect, btnNewCode, btnRole }) b.Enabled = !busy;
        UseWaitCursor = busy;
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

    bool ReadOptions()
    {
        _opt.PublicHost = cmbAddr.Text.Trim();
        _opt.SshPort = (int)numSsh.Value;
        _opt.Save();
        if (_opt.PublicHost.Length == 0)
        {
            MessageBox.Show(this, "Укажите адрес этого ПК, по которому к нему может подключиться ПК Unreal (публичный IP или имя).", "UE Bridge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        return true;
    }

    async Task RunSetup(bool renew)
    {
        if (_busy || !ReadOptions()) return;
        SetBusy(true);
        try
        {
            var o = _opt;
            Log("=== Настройка ПК Claude ===");
            int failed = 0;
            if (!await Step($"OpenSSH Server: вход только по ключу, порт {o.SshPort}, правило брандмауэра", () => ClaudePc.EnsureSshd(o, s => Log("    " + s)))) failed++;
            if (!await Step("Скрипты приёма данных от ПК Unreal и подключения папки проекта", ClaudePc.WriteScripts)) failed++;
            string code = "";
            if (failed == 0 && !await Step("Ключ для ПК Unreal, код подключения и самопроверка", () =>
                {
                    var (c, result) = ClaudePc.EnsurePairing(o, renew, s => Log("    " + s));
                    code = c;
                    return result;
                })) failed++;
            if (code.Length > 0)
            {
                txtCode.Text = code;
                Log("Код подключения готов: нажмите «Копировать код» и вставьте его в UeBridge на ПК Unreal (поле «Код подключения от ПК Claude»).");
            }
            Log(failed == 0 ? "=== Готово, ошибок нет ===" : $"=== Готово, замечаний: {failed} (см. выше) ===");
            await RefreshStatusCore();
        }
        finally { SetBusy(false); }
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
        var lines = await Task.Run(() => ClaudePc.Status(o));
        Log("Состояние ПК Claude:");
        foreach (var l in lines) Log("    " + l);
    }

    void Connect()
    {
        if (!File.Exists(ClaudePc.DesktopCmd))
        {
            Log("Сначала нажмите «Настроить ПК Claude».");
            return;
        }
        // Через Проводник — чтобы папка подключилась в обычном (не административном) сеансе, где её увидят Cursor и Claude Code
        Process.Start(new ProcessStartInfo("explorer.exe", "\"" + ClaudePc.DesktopCmd + "\"") { UseShellExecute = false })?.Dispose();
        Log("Запущено «" + Path.GetFileName(ClaudePc.DesktopCmd) + "»: оно подключит папку проекта и откроет её в редакторе. При первом запуске спросит пароль Windows от ПК Unreal.");
    }
}
