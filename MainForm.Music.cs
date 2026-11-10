using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace AzulGroove;

// Azul Groove Music: card no início do painel, mensagem de divulgação ao abrir o app e
// "Instalar e abrir" (baixa o instalador do GitHub, instala em modo silencioso e já abre o app).
public partial class MainForm
{
    const string MusicPage = "https://github.com/andrsodremiranda/Azul-Groove-Music";
    const string MusicReleasesPage = MusicPage + "/releases";
    const string MusicLatestApi = "https://api.github.com/repos/andrsodremiranda/Azul-Groove-Music/releases/latest";
    const string MusicDownloadPrefix = MusicPage + "/releases/download/";
    const string MusicExeName = "AzulGrooveMusic.exe";
    // AppId do AzulGrooveMusic.iss (o Inno Setup grava o desinstalador em ..\Uninstall\{AppId}_is1)
    const string MusicUninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{7B1E5C3A-92D4-4F6B-8A1D-3C5E9F2B6A40}_is1";

    bool musicBusy;               // evita instalar duas vezes ao mesmo tempo
    bool musicPromoShownThisRun;  // a mensagem de divulgação aparece uma vez por abertura do app

    // Caminho do AzulGrooveMusic.exe se já estiver instalado; senão, null.
    static string? FindMusicExe()
    {
        try
        {
            foreach (var (hive, view) in new[]
            {
                (RegistryHive.CurrentUser, RegistryView.Registry64),
                (RegistryHive.LocalMachine, RegistryView.Registry64),
                (RegistryHive.LocalMachine, RegistryView.Registry32),
            })
            {
                using var bk = RegistryKey.OpenBaseKey(hive, view);
                using var k = bk.OpenSubKey(MusicUninstallKey);
                if (k == null) continue;

                if (k.GetValue("InstallLocation") is string loc && !string.IsNullOrWhiteSpace(loc))
                {
                    var p = Path.Combine(loc.Trim().Trim('"'), MusicExeName);
                    if (File.Exists(p)) return p;
                }
                if (k.GetValue("DisplayIcon") is string icon && !string.IsNullOrWhiteSpace(icon))
                {
                    var p = icon.Split(',')[0].Trim().Trim('"');
                    if (p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(p)) return p;
                }
            }
        }
        catch { /* segue para os locais padrão */ }

        try
        {
            var dirs = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Azul Groove Music"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Azul Groove Music"),
            };
            foreach (var d in dirs)
            {
                var p = Path.Combine(d, MusicExeName);
                if (File.Exists(p)) return p;
            }
        }
        catch { }
        return null;
    }

    static void LaunchMusic(string exe) =>
        Process.Start(new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? ""
        });

    // A mensagem de divulgação aparece ao abrir o app, se o Music ainda não estiver instalado,
    // a pessoa não pediu para não ver mais e o app está online (e não está mostrando as novidades da atualização).
    bool ConsumeMusicPromo()
    {
        if (musicPromoShownThisRun || cfg.MusicPromoOff || cfg.OfflineMode || justUpdated) return false;
        if (FindMusicExe() != null) return false;
        musicPromoShownThisRun = true;
        return true;
    }

    Action<string, int, string?>? musicObserver; // a mensagem de divulgação acompanha o progresso por aqui

    void MusicPost(string phase, int percent = 0, string? msg = null, bool? installed = null)
    {
        PostToPanel(new { type = "music", phase, percent, msg = msg ?? "", installed });
        musicObserver?.Invoke(phase, percent, msg);
    }

    // Botão do Music: já instalado → abre; senão → baixa, instala em modo silencioso e abre. Tudo dentro do app.
    async Task MusicGoAsync()
    {
        if (musicBusy) return;
        musicBusy = true;
        string? file = null;
        try
        {
            var exe = FindMusicExe();

            if (exe == null)
            {
                if (cfg.OfflineMode) { MusicPost("err", msg: Tr("p.music.offline")); return; }

                MusicPost("check");
                var (url, name, digest) = await MusicFindSetupAsync();
                if (url == null) { MusicPost("err", msg: Tr("p.music.nosetup")); return; }

                // ---- baixa o instalador ----
                var dir = Path.Combine(Path.GetTempPath(), "AzulGroove-Music");
                Directory.CreateDirectory(dir);
                file = Path.Combine(dir, Path.GetFileName(name ?? "AzulGrooveMusic-Setup.exe"));

                using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    req.Headers.UserAgent.ParseAdd("AzulGroove-App");
                    using var r = await DlHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                    r.EnsureSuccessStatusCode();
                    var total = r.Content.Headers.ContentLength ?? -1;
                    MusicPost("download", 0);

                    await using var src = await r.Content.ReadAsStreamAsync();
                    await using var dst = File.Create(file);
                    var buf = new byte[81920];
                    long done = 0; int last = -1, n;
                    while ((n = await src.ReadAsync(buf)) > 0)
                    {
                        await dst.WriteAsync(buf.AsMemory(0, n));
                        done += n;
                        if (total > 0)
                        {
                            var pct = (int)(done * 100 / total);
                            if (pct != last) { last = pct; MusicPost("download", pct); }
                        }
                    }
                }

                // ---- confere a integridade quando o GitHub informa o SHA-256 ----
                if (digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                {
                    await using var fs = File.OpenRead(file);
                    var hash = Convert.ToHexString(await SHA256.HashDataAsync(fs));
                    if (!hash.Equals(digest[7..], StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("sha256");
                }

                // ---- instala em modo silencioso (Inno Setup): sem assistente, sem janelas, sem pedir administrador ----
                MusicPost("install");
                var psi = new ProcessStartInfo(file, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /CURRENTUSER /CLOSEAPPLICATIONS")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using (var p = Process.Start(psi) ?? throw new InvalidOperationException("setup"))
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                    await p.WaitForExitAsync(cts.Token);
                    if (p.ExitCode != 0) throw new InvalidOperationException("setup exit " + p.ExitCode);
                }

                exe = FindMusicExe();
                if (exe == null) throw new FileNotFoundException(MusicExeName);
            }

            // ---- abre o app na hora ----
            MusicPost("launch");
            LaunchMusic(exe);
            MusicPost("done", installed: true);
        }
        catch
        {
            MusicPost("err", msg: Tr("p.music.err"));
        }
        finally
        {
            try { if (file != null && File.Exists(file)) File.Delete(file); } catch { }
            musicBusy = false;
        }
    }

    // Procura o AzulGrooveMusic-Setup*.exe no release mais recente (só aceita download vindo do repositório do Music).
    async Task<(string? Url, string? Name, string? Digest)> MusicFindSetupAsync()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, MusicLatestApi);
        req.Headers.UserAgent.ParseAdd("AzulGroove-App");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var r = await UpdHttp.SendAsync(req);
        if (r.StatusCode == System.Net.HttpStatusCode.NotFound) return (null, null, null);
        r.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());

        if (doc.RootElement.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            foreach (var a in assets.EnumerateArray())
            {
                var n = JStr(a, "name");
                var d = JStr(a, "browser_download_url");
                if (!n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !n.Contains("setup", StringComparison.OrdinalIgnoreCase)) continue;
                if (!d.StartsWith(MusicDownloadPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                var dg = JStr(a, "digest");
                return (d, n, dg.Length > 0 ? dg : null);
            }
        return (null, null, null);
    }
}
