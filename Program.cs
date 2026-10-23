namespace UeBridge;

static class Program
{
    /// <summary>Режим, в который переключиться после закрытия текущего окна (кнопка «Это ПК Claude/ПК Unreal…»).</summary>
    public static string? NextRole;

    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        var o = Options.Load();
        string? role = o.Role.Length > 0 ? o.Role : Setup.EngineInstalled() ? "unreal" : "claude";
        while (role != null)
        {
            NextRole = null;
            Application.Run(role == "claude" ? new ClaudePcForm() : new MainForm());
            role = NextRole;
        }
    }

    public static void SwitchRole(Form current, string role)
    {
        var o = Options.Load();
        o.Role = role;
        o.Save();
        NextRole = role;
        current.Close();
    }
}
