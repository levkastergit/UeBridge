using System.Text.Json.Nodes;

namespace UeBridge;

/// <summary>Проверка новой версии в GitHub Releases. Скачивание и замена файла делаются вручную со страницы релиза.</summary>
public static class Updater
{
    public const string Repo = "levkastergit/UeBridge";
    public static string ReleasesPage => $"https://github.com/{Repo}/releases/latest";

    public static Version Current
    {
        get
        {
            var v = typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
        }
    }

    public sealed record Check(Version? Latest, string? Error);

    public static async Task<Check> CheckAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("UeBridge/" + Current);
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            using var resp = await http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest");
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                return new Check(null, $"на GitHub ({Repo}) пока нет ни одного релиза");
            resp.EnsureSuccessStatusCode();
            var j = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!;
            var tag = j["tag_name"]?.ToString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest))
                return new Check(null, "непонятный тег релиза: " + tag);
            return new Check(new Version(latest.Major, latest.Minor, Math.Max(latest.Build, 0)), null);
        }
        catch (Exception ex)
        {
            return new Check(null, ex.Message);
        }
    }
}
