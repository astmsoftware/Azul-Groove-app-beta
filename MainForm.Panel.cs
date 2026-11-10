using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;

namespace AzulGroove;

// Painel inicial do app: botões, configurações e verificação de atualização.
public partial class MainForm
{
    const string ReleasesApi = "https://api.github.com/repos/andrsodremiranda/Azul-Groove-app/releases/latest";
    const string ReleasesPage = "https://github.com/andrsodremiranda/Azul-Groove-app/releases";
    const string ReleasesListApi = "https://api.github.com/repos/andrsodremiranda/Azul-Groove-app/releases?per_page=15";
    const string ReleaseDownloadPrefix = "https://github.com/andrsodremiranda/Azul-Groove-app/releases/download/";
    static readonly HttpClient UpdHttp = new() { Timeout = TimeSpan.FromSeconds(15) };
    static readonly HttpClient DlHttp = new() { Timeout = Timeout.InfiniteTimeSpan }; // download pode demorar

    bool panelActive;      // true enquanto o painel local está na tela
    bool hotkeyOk = true;  // false se o Windows recusou a combinação de teclas
    bool updating;         // evita baixar duas vezes ao mesmo tempo
    bool justUpdated;      // true na 1ª abertura depois de atualizar (abre as novidades)
    List<(string? Setup, string? Portable)> relLinks = new(); // links da lista de versões mostrada no painel

    static string UpdMarker => Path.Combine(DataDir, "update.json");

    static Version? ParseVer(string? s)
    {
        var m = Regex.Match(s ?? "", @"\d+(\.\d+){1,3}");
        if (m.Success && Version.TryParse(m.Value, out var v)) return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
        return null;
    }

    static string JStr(JsonElement e, string k) =>
        e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    // Lê (e apaga) o aviso deixado pelo app antigo antes de rodar o instalador
    static (string from, string to)? ConsumeUpdateMarker()
    {
        try
        {
            if (!File.Exists(UpdMarker)) return null;
            var txt = File.ReadAllText(UpdMarker);
            File.Delete(UpdMarker);
            using var doc = JsonDocument.Parse(txt);
            var from = JStr(doc.RootElement, "from");
            var to = JStr(doc.RootElement, "to");
            if (!Regex.IsMatch(from, @"^\d+(\.\d+){1,3}$") || !Regex.IsMatch(to, @"^\d+(\.\d+){1,3}$")) return null;
            // só mostra se a versão nova realmente está rodando (instalação pode ter falhado)
            return Version.TryParse(to, out var tv) && CurrentVersion >= new Version(tv.Major, tv.Minor, Math.Max(tv.Build, 0))
                ? (from, to) : null;
        }
        catch { return null; }
    }

    // Fonte única da versão: AppVersion (MainForm.Beta.cs). Antes isto lia a versão do assembly (csproj), que ficava
    // desatualizada e fazia o app achar que não estava na versão mais recente ao verificar atualização.
    static Version CurrentVersion => ParseVer(AppVersion) ?? new Version(1, 0, 0);

    static string DisplayVersion => CurrentVersion.ToString();

    void ShowPanel()
    {
        if (web.CoreWebView2 == null) return;
        panelActive = true;
        pageTheme = null; // o painel não informa tema: a janela segue a escolha do usuário / Windows
        ApplyThemeMode(pushToSite: false);
        // direita-para-esquerda só se o idioma for RTL E já tiver os textos do painel (senão apareceria inglês espelhado)
        var rtl = I18nList().Any(x => x.Rtl && x.Code.Equals(LangCode, StringComparison.OrdinalIgnoreCase))
                  && I18nAll().TryGetValue(LangCode, out var own) && own.ContainsKey("p.title");
        web.CoreWebView2.NavigateToString(PanelHtml
            .Replace("@T@", PanelTextsJson())
            .Replace("@LANG@", LangCode)
            .Replace("@DIR@", rtl ? "rtl" : "ltr")
            .Replace("@DD_SCROLL@", DropdownScrollbarCss)
            .Replace("@DD_HELPER@", DropdownViewportScript));
    }

    void GoStart()
    {
        if (cfg.StartPage == 2) web.CoreWebView2.Navigate(ChatUrl);
        else if (cfg.StartPage == 1) web.CoreWebView2.Navigate(HomeUrl);
        else ShowPanel();
    }

    // IMPORTANTE: depois de NavigateToString o WebView2 NÃO informa "data:" em Source (costuma vir
    // "about:blank"). Por isso o painel é identificado por esta flag, e não pela URL.
    bool OnPanel => panelActive;

    static bool IsPanelSource(string? s) =>
        string.IsNullOrEmpty(s) || s.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                                || s.StartsWith("about:", StringComparison.OrdinalIgnoreCase);

    void PostToPanel(object data)
    {
        if (web.CoreWebView2 == null || !OnPanel) return;
        web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(data));
    }

    void PushPanelState()
    {
        PostToPanel(new
        {
            type = "state",
            version = CurrentVersion.ToString(),
            displayVersion = DisplayVersion,
            theme = cfg.Theme,
            hotkeyOn = cfg.HotkeyOn,
            preset = cfg.Preset,
            presets = Presets.Select((p, i) => PresetLabel(i)).ToArray(),
            tray = cfg.TrayOnClose,
            start = cfg.StartPage,
            hotkeyOk,
            hotkeyLabel = PresetLabel(cfg.Preset),
            news = justUpdated,
            aiMin = lai.StopOnMinimize,
            lang = string.IsNullOrEmpty(cfg.Lang) ? "auto" : cfg.Lang,
            langs = I18nList().Select(x => new { code = x.Code, name = x.Name }).ToArray()
        });
        justUpdated = false; // as novidades abrem sozinhas só uma vez
    }

    // Retorna true se a mensagem era um comando do painel
    bool HandlePanelMessage(CoreWebView2WebMessageReceivedEventArgs e)
    {
        string t;
        try { t = e.TryGetWebMessageAsString(); } catch { return false; }
        if (t == null || !t.StartsWith("panel:")) return false;
        if (!panelActive || !IsPanelSource(e.Source)) return true; // só o painel local manda comandos

        // IA do PC: "panel:ai:<nome>:<argumento>" (o argumento pode ter ":", como um link https://)
        if (t.StartsWith("panel:ai:", StringComparison.Ordinal))
        {
            var rest = t[9..];
            var k = rest.IndexOf(':');
            PanelAiCommand(k < 0 ? rest : rest[..k], k < 0 ? "" : rest[(k + 1)..]);
            return true;
        }

        var p = t.Split(':');
        var cmd = p.Length > 1 ? p[1] : "";
        switch (cmd)
        {
            case "get": PushPanelState(); MusicPost("init", installed: FindMusicExe() != null); break;
            case "music": _ = MusicGoAsync(); break;
            case "update": _ = CheckUpdateAsync(); break;
            case "data": OpenDataFolder(); break;
            case "open" when p.Length > 2:
                if (p[2] == "chat") ShowChat();
                else if (p[2] == "login") ShowChat();   // o login (Discord, Google, GitHub) fica no chat
                else if (p[2] == "local") ShowLocalChat(); // chat offline (IA no PC) sem ligar o "Modo offline"
                else if (p[2] == "home") NavigateTo(HomeUrl);
                else if (p[2] == "docs") NavigateTo(HomeUrl + "docs");
                else if (p[2] == "ia") NavigateTo(HomeUrl + "ia");
                break;
            case "news": _ = SendReleasesAsync("news"); break;
            case "versions": _ = SendReleasesAsync("versions"); break;
            case "dl" when p.Length > 3 && int.TryParse(p[2], out var ri) && ri >= 0 && ri < relLinks.Count:
                var link = p[3] == "portable" ? relLinks[ri].Portable : relLinks[ri].Setup;
                if (link != null && link.StartsWith(ReleaseDownloadPrefix, StringComparison.OrdinalIgnoreCase)) OpenExternal(link);
                break;
            case "ext" when p.Length > 2:
                if (p[2] == "releases") OpenExternal(ReleasesPage);
                break;
            case "lang" when p.Length > 2:
                cfg.Lang = I18nNormalize(p[2]); cfg.Save();
                TrayRetranslate();   // bandeja no novo idioma
                ShowPanel();         // recarrega o painel no novo idioma (ele pede o estado de novo sozinho)
                break;
            case "set" when p.Length > 3 && int.TryParse(p[3], out var v):
                ApplyPanelSetting(p[2], v);
                PushPanelState(); // devolve o estado real ao painel (ex.: atalho recusado pelo Windows)
                break;
        }
        return true;
    }

    // Comandos da seção "IA no seu PC" do painel. Reaproveita toda a lógica de MainForm_LocalAi.cs.
    void PanelAiCommand(string name, string arg)
    {
        if (arg.Length > 2000) return;
        switch (name)
        {
            case "aistate": case "mdl_dl": case "mdl_dluse": case "mdl_cancel": case "mdl_use":
            case "mdl_del": case "mdl_url": case "mdl_import": case "mdl_vulkan":
                LAiHandle(name, arg);
                break;
            case "mdl_stop": // desliga o motor e libera a memória (liga de novo sozinho quando for conversar)
                if (lchatBusy) { _ = LChatPushAsync("aiMsg", "Espere a resposta terminar para desligar a IA."); break; }
                LAiStop();
                laiPausedByMin = false;
                LAiPhase("idle", "");
                _ = LAiPushStateAsync();
                break;
        }
    }

    void ApplyPanelSetting(string key, int v)
    {
        switch (key)
        {
            case "theme" when v >= 0 && v <= 2:
                cfg.Theme = v; cfg.Save();
                for (int j = 0; j < miTheme.DropDownItems.Count; j++)
                    ((ToolStripMenuItem)miTheme.DropDownItems[j]).Checked = j == v;
                ApplyThemeMode(pushToSite: true);
                break;
            case "hotkey":
                miHotkeyOn.Checked = v == 1; // o próprio item salva e registra
                break;
            case "preset" when v >= 0 && v < Presets.Length:
                cfg.Preset = v; cfg.Save();
                for (int j = 0; j < miKeys.DropDownItems.Count; j++)
                    ((ToolStripMenuItem)miKeys.DropDownItems[j]).Checked = j == v;
                RegisterHotkey(); UpdateLabels();
                break;
            case "tray":
                miTray.Checked = v == 1;
                break;
            case "start" when v >= 0 && v <= 2:
                cfg.StartPage = v; cfg.Save();
                break;
            case "aimin":
                lai.StopOnMinimize = v == 1; lai.Save();
                break;
        }
    }

    void OpenDataFolder()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(DataDir) { UseShellExecute = true });
        }
        catch { /* ignora */ }
    }

    // Instalado pelo Setup (existe unins000.exe ao lado do .exe) ou versão Portable?
    static bool IsInstalledBySetup()
    {
        try
        {
            var dir = Path.GetDirectoryName(Environment.ProcessPath ?? Application.ExecutablePath);
            return dir != null && Directory.EnumerateFiles(dir, "unins*.exe").Any();
        }
        catch { return false; }
    }

    async Task CheckUpdateAsync()
    {
        if (updating) return;
        var cur = CurrentVersion.ToString();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, ReleasesApi);
            req.Headers.UserAgent.ParseAdd("AzulGroove-App");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var r = await UpdHttp.SendAsync(req);
            if (r.StatusCode == HttpStatusCode.NotFound)
            {
                PostToPanel(new { type = "update", status = "info", current = cur, latest = "", url = ReleasesPage, msg = Tr("pu.nopub") });
                return;
            }
            r.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            var url = root.TryGetProperty("html_url", out var u) ? (u.GetString() ?? ReleasesPage) : ReleasesPage;

            var relName = JStr(root, "name");
            if ((ParseVer(tag) ?? ParseVer(relName)) is Version latest)
            {
                if (latest <= CurrentVersion)
                {
                    PostToPanel(new { type = "update", status = "ok", current = cur, latest = latest.ToString(), url, auto = false, msg = "" });
                    return;
                }

                // Procura o instalador (…Setup.exe) entre os arquivos da versão publicada
                string? dlUrl = null, dlName = null, digest = null;
                if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in assets.EnumerateArray())
                    {
                        var n = a.TryGetProperty("name", out var nn) ? nn.GetString() ?? "" : "";
                        var d = a.TryGetProperty("browser_download_url", out var dd) ? dd.GetString() : null;
                        if (d == null || !n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                        if (n.Contains("setup", StringComparison.OrdinalIgnoreCase))
                        {
                            dlUrl = d; dlName = n;
                            digest = a.TryGetProperty("digest", out var dg) && dg.ValueKind == JsonValueKind.String ? dg.GetString() : null;
                            break;
                        }
                    }
                }

                if (dlUrl == null || !dlUrl.StartsWith(ReleaseDownloadPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    PostToPanel(new { type = "update", status = "new", current = cur, latest = latest.ToString(), url, auto = false,
                        msg = Tr("pu.nosetup") });
                    return;
                }
                if (!IsInstalledBySetup())
                {
                    PostToPanel(new { type = "update", status = "new", current = cur, latest = latest.ToString(), url, auto = false,
                        msg = Tr("pu.portable") });
                    return;
                }

                // Clicou em "Verificar" e existe versão nova: baixa, executa e instala sozinho
                PostToPanel(new { type = "update", status = "new", current = cur, latest = latest.ToString(), url, auto = true, msg = Tr("pu.auto") });
                await DownloadAndInstallAsync(dlUrl, latest.ToString(), digest, url);
            }
            else
            {
                PostToPanel(new { type = "update", status = "info", current = cur, latest = tag, url,
                    msg = TrF("pu.notag", tag) });
            }
        }
        catch
        {
            PostToPanel(new { type = "update", status = "err", current = cur, latest = "", url = ReleasesPage, msg = Tr("pu.err") });
        }
    }

    // Busca as versões publicadas e manda para o painel (mode: "news" = novidades, "versions" = baixar versões)
    async Task SendReleasesAsync(string mode)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, ReleasesListApi);
            req.Headers.UserAgent.ParseAdd("AzulGroove-App");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var r = await UpdHttp.SendAsync(req);
            r.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());

            var items = new List<object>();
            var links = new List<(string? Setup, string? Portable)>();
            foreach (var rel in doc.RootElement.EnumerateArray())
            {
                if (rel.TryGetProperty("draft", out var dr) && dr.ValueKind == JsonValueKind.True) continue;
                var tag = JStr(rel, "tag_name");
                var name = JStr(rel, "name");
                var body = JStr(rel, "body");
                var date = JStr(rel, "published_at");
                var pre = rel.TryGetProperty("prerelease", out var pr) && pr.ValueKind == JsonValueKind.True;

                string? setup = null, portable = null;
                if (rel.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                    foreach (var a in assets.EnumerateArray())
                    {
                        var n = JStr(a, "name");
                        var d = JStr(a, "browser_download_url");
                        if (!d.StartsWith(ReleaseDownloadPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                        var ext = Path.GetExtension(n).ToLowerInvariant();
                        if (n.Contains("portable", StringComparison.OrdinalIgnoreCase) && (ext == ".exe" || ext == ".zip")) portable ??= d;
                        else if (n.Contains("setup", StringComparison.OrdinalIgnoreCase) && ext == ".exe") setup ??= d;
                    }

                var ver = ParseVer(tag) ?? ParseVer(name);
                items.Add(new
                {
                    title = string.IsNullOrWhiteSpace(name) ? tag : name,
                    version = ver?.ToString() ?? "",
                    date = date.Length >= 10 ? date[..10] : "",
                    pre,
                    body = body.Length > 2000 ? body[..2000] + "…" : body,
                    setup = setup != null,
                    portable = portable != null,
                    current = ver != null && ver == CurrentVersion
                });
                links.Add((setup, portable));
            }
            relLinks = links;
            PostToPanel(new { type = "releases", mode, err = false, items });
        }
        catch
        {
            PostToPanel(new { type = "releases", mode, err = true, items = Array.Empty<object>() });
        }
    }

    async Task DownloadAndInstallAsync(string url, string latest, string? digest, string pageUrl)
    {
        if (updating) return;
        updating = true;
        string? file = null;
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "AzulGroove-Update");
            Directory.CreateDirectory(dir);
            file = Path.Combine(dir, $"AzulGroove-Setup-{latest}.exe");

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("AzulGroove-App");
            using var r = await DlHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            r.EnsureSuccessStatusCode();
            var total = r.Content.Headers.ContentLength ?? -1;

            await using (var src = await r.Content.ReadAsStreamAsync())
            await using (var dst = File.Create(file))
            {
                var buf = new byte[81920];
                long done = 0; int last = -1, n;
                while ((n = await src.ReadAsync(buf)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n));
                    done += n;
                    if (total > 0)
                    {
                        var pct = (int)(done * 100 / total);
                        if (pct != last) { last = pct; PostToPanel(new { type = "progress", percent = pct }); }
                    }
                }
            }

            // Confere a integridade quando o GitHub informa o SHA-256 do arquivo
            if (digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                await using var fs = File.OpenRead(file);
                var hash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(fs));
                if (!hash.Equals(digest[7..], StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("sha256");
            }

            // Deixa um aviso para o app novo mostrar a animação de "atualizado" quando abrir
            try { File.WriteAllText(UpdMarker, JsonSerializer.Serialize(new { from = CurrentVersion.ToString(), to = latest })); } catch { }

            // Avisa que o app vai reiniciar
            PostToPanel(new { type = "restart", seconds = 5 });
            try { tray.ShowBalloonTip(3000, "Azul Groove", Tr("pu.balloon"), ToolTipIcon.Info); } catch { }
            await Task.Delay(5000);

            // Instalação silenciosa (Inno Setup): sem assistente, sem janelas. Quando o instalador
            // termina, o cmd abre o app novo, que mostra a animação de "atualizado".
            var appExe = Environment.ProcessPath ?? Application.ExecutablePath;
            var silent = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /CLOSEAPPLICATIONS /FORCECLOSEAPPLICATIONS";
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                Arguments = $"/c \"\"{file}\" {silent} & start \"\" \"{appExe}\"\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
            };
            System.Diagnostics.Process.Start(psi);
            exiting = true;
            Close();
        }
        catch
        {
            try { if (file != null && File.Exists(file)) File.Delete(file); } catch { }
            try { File.Delete(UpdMarker); } catch { }
            PostToPanel(new { type = "update", status = "err", current = CurrentVersion.ToString(), latest = latest, url = pageUrl, auto = false,
                msg = Tr("pu.dlfail") });
        }
        finally { updating = false; }
    }

    const string PanelHtml = """
<!doctype html><html lang="@LANG@" dir="@DIR@"><head><meta charset="utf-8"><meta name="color-scheme" content="dark light"><title>Azul Groove · Painel</title><style>
:root{--bg:#0f1117;--card:#181b25;--line:#272c3b;--tx:#f2f4ff;--mu:#9aa3b2;--ac:#5865f2;--ac2:#7a5cff;--scroll-thumb:rgba(154,163,178,.55);--scroll-thumb-hover:#5865f2;color-scheme:dark}
@media (prefers-color-scheme:light){:root{--bg:#f4f6fb;--card:#fff;--line:#e0e4ef;--tx:#14161a;--mu:#646b78;--scroll-thumb:rgba(100,107,120,.48);--scroll-thumb-hover:#5865f2;color-scheme:light}}
*{box-sizing:border-box;margin:0;padding:0}
body{background:var(--bg);color:var(--tx);font-family:"Segoe UI",system-ui,sans-serif;padding:26px clamp(16px,4vw,44px) 50px;line-height:1.5}
.w{max-width:880px;margin:0 auto}
.hd{display:flex;align-items:center;gap:16px;margin-bottom:26px}
.lg{width:62px;height:62px;border-radius:18px;background:linear-gradient(135deg,var(--ac),var(--ac2));display:flex;align-items:center;justify-content:center;gap:5px;box-shadow:0 10px 30px rgba(88,101,242,.45)}
.lg i{width:6px;height:30px;border-radius:3px;background:#fff;transform:scaleY(.34);animation:eq 1s ease-in-out infinite;will-change:transform}
.lg i:nth-child(1){animation-delay:-.9s}.lg i:nth-child(2){animation-delay:-.6s}.lg i:nth-child(3){animation-delay:-.3s}.lg i:nth-child(4){animation-delay:-.75s}.lg i:nth-child(5){animation-delay:-.15s}
@keyframes eq{0%,100%{transform:scaleY(.34)}50%{transform:scaleY(1)}}
@media (prefers-reduced-motion:reduce){.lg i{animation:none}}.hid .lg i{animation-play-state:paused}
h1{font-size:1.6rem;letter-spacing:-.3px}.hd p{color:var(--mu);font-size:.92rem}
h2{font-size:.78rem;letter-spacing:1.2px;text-transform:uppercase;color:var(--ac);margin:30px 0 10px}
.g{display:grid;grid-template-columns:repeat(auto-fill,minmax(200px,1fr));gap:12px}
.b{display:flex;flex-direction:column;gap:2px;text-align:left;background:var(--card);border:1px solid var(--line);border-radius:14px;padding:16px;color:var(--tx);text-decoration:none;cursor:pointer;font:inherit;transition:border-color .15s,transform .15s}
.b:hover{border-color:var(--ac);transform:translateY(-2px)}.b small{color:var(--mu)}
.b.p{background:linear-gradient(135deg,var(--ac),var(--ac2));border:0;color:#fff}.b.p small{color:#e6e9ff}
.c{background:var(--card);border:1px solid var(--line);border-radius:14px;padding:6px 18px}
.r{display:flex;justify-content:space-between;align-items:center;gap:14px;padding:13px 0;border-bottom:1px solid var(--line)}
.r:last-child{border:0}.r small{display:block;color:var(--mu)}
.seg{display:flex;border:1px solid var(--line);border-radius:10px;overflow:hidden}
.seg button{background:none;border:0;color:var(--mu);padding:7px 13px;cursor:pointer;font:inherit;font-size:.88rem}
.seg button.on{background:var(--ac);color:#fff}
.dd{position:relative;min-width:190px}
.ddb{width:100%;text-align:left;background:var(--bg);color:var(--tx);border:1px solid var(--line);border-radius:10px;padding:8px 34px 8px 12px;font:inherit;font-size:.88rem;cursor:pointer;position:relative;transition:.15s}
.ddb:hover,.dd.open .ddb{border-color:var(--ac)}
.ddb::after{content:"";position:absolute;right:14px;top:50%;width:7px;height:7px;border-right:2px solid var(--mu);border-bottom:2px solid var(--mu);transform:translateY(-70%) rotate(45deg);transition:.2s}
.dd.open .ddb::after{transform:translateY(-30%) rotate(225deg)}
.ddl{display:none;max-height:260px;overflow:auto;position:absolute;right:0;top:calc(100% + 6px);min-width:100%;z-index:20;padding:5px;border-radius:12px;background:var(--card);border:1px solid var(--line);box-shadow:0 16px 40px rgba(0,0,0,.35)}
.dd.open .ddl{display:block}
.ddo{padding:8px 12px;border-radius:8px;cursor:pointer;font-size:.88rem;white-space:nowrap}
.ddo:hover{background:rgba(109,124,255,.18)}.ddo.sel{background:var(--ac);color:#fff}
@DD_SCROLL@
.in{background:var(--bg);color:var(--tx);border:1px solid var(--line);border-radius:10px;padding:8px 12px;font:inherit;font-size:.88rem;min-width:200px;outline:none;transition:.15s}
.in:focus{border-color:var(--ac);box-shadow:0 0 0 3px rgba(88,101,242,.25)}
.btn.dg{background:#d9455b}.btn:disabled{opacity:.5;cursor:default}
#az-st{display:none;margin-top:10px;color:var(--mu);font-size:.92rem}
.sw{position:relative;width:44px;height:25px;flex:none}.sw input{opacity:0;width:100%;height:100%;position:absolute;cursor:pointer;z-index:2}
.sw span{position:absolute;inset:0;background:var(--line);border-radius:25px;transition:.2s}
.sw span:before{content:"";position:absolute;left:3px;top:3px;width:19px;height:19px;border-radius:50%;background:#fff;transition:.2s}
.sw input:checked+span{background:var(--ac)}.sw input:checked+span:before{transform:translateX(19px)}
.btn{background:var(--ac);color:#fff;border:0;border-radius:10px;padding:9px 16px;font:inherit;font-size:.9rem;cursor:pointer;text-decoration:none;display:inline-block}
.btn.o{background:none;border:1px solid var(--line);color:var(--tx)}
.btn.pri{display:inline-flex;align-items:center;gap:8px;white-space:nowrap;flex:none;padding:10px 18px;font-weight:600;border-radius:12px;background:linear-gradient(135deg,var(--ac),var(--ac2));box-shadow:0 6px 18px rgba(88,101,242,.35);transition:transform .15s,filter .15s,box-shadow .15s}
.btn.pri:hover:not(:disabled){transform:translateY(-1px);filter:brightness(1.1);box-shadow:0 10px 24px rgba(88,101,242,.45)}
.btn.pri:active:not(:disabled){transform:none}
.btn.pri.spin svg{animation:azspin .9s linear infinite}@keyframes azspin{to{transform:rotate(360deg)}}
#upd.upok{color:#3ba55d}#upd.uperr{color:#ed4245}#upd.upnew{color:var(--ac)}
#upd{margin-top:10px;color:var(--mu);font-size:.92rem}#upd b{color:var(--tx)}
.md{position:fixed;inset:0;background:rgba(0,0,0,.55);display:flex;align-items:center;justify-content:center;padding:20px;z-index:9}.md[hidden]{display:none}
.mc{background:var(--card);border:1px solid var(--line);border-radius:16px;width:min(640px,100%);max-height:84vh;overflow:auto;padding:20px}
.mh{display:flex;justify-content:space-between;align-items:center;gap:10px;margin-bottom:14px}.mh div{display:flex;gap:8px}
.rl{border:1px solid var(--line);border-radius:12px;padding:14px;margin-bottom:10px}
.rl h4{display:flex;gap:8px;align-items:center;flex-wrap:wrap}.rl h4 small{color:var(--mu);font-weight:400}
.tg{font-size:.72rem;background:var(--ac);color:#fff;border-radius:20px;padding:2px 9px}
.rl pre{white-space:pre-wrap;font:inherit;color:var(--mu);font-size:.88rem;margin:8px 0}
.ac{display:flex;gap:8px;flex-wrap:wrap;margin-top:8px;align-items:center}
.rc{text-align:center;padding:34px 20px}.rc h3{margin:12px 0 6px}.rc p{color:var(--mu)}
.ft{margin-top:34px;color:var(--mu);font-size:.85rem;text-align:center}
.pb{height:6px;border-radius:6px;background:var(--line);margin-top:8px;overflow:hidden;max-width:360px}.pb[hidden]{display:none}.pb i{display:block;height:100%;width:0;background:linear-gradient(90deg,var(--ac),var(--ac2));transition:width .3s}
.ainote{color:var(--mu);font-size:.88rem;margin:10px 2px 0}
.mdl{background:var(--card);border:1px solid var(--line);border-radius:14px;padding:14px 18px;margin-top:10px}.mdl.use{border-color:var(--ac)}
.mdl h4{display:flex;gap:8px;align-items:center;flex-wrap:wrap}.mdl p{color:var(--mu);font-size:.9rem;margin:4px 0}.mdl .meta{color:var(--mu);font-size:.8rem}
.aitabs{display:inline-flex;margin-top:12px}.aipane{margin-top:12px}.aipane[hidden]{display:none}.aipane .ainote{margin-top:0}
#ai-msg{margin-top:10px;color:var(--mu);font-size:.92rem}
.mu{grid-column:1/-1;display:flex;align-items:center;justify-content:space-between;gap:16px;padding:18px 22px;border-radius:16px;border:1px solid rgba(122,92,255,.55);background:linear-gradient(135deg,rgba(88,101,242,.24),rgba(122,92,255,.10)),var(--card)}
.mu b{display:flex;align-items:center;gap:10px;flex-wrap:wrap;font-size:1.05rem}.mu small{display:block;color:var(--mu);margin-top:3px}
.mu .mi{display:inline-flex;align-items:center;justify-content:center;flex:none;width:34px;height:34px;border-radius:10px;background:linear-gradient(135deg,#5865f2,#7a5cff);box-shadow:0 4px 14px rgba(106,92,255,.45)}#mu-btn{display:inline-flex;align-items:center;gap:8px}#mu-btn svg{flex:none}
.mu .tg{letter-spacing:.5px;font-weight:700}#mu-st.er{color:#ed4245}
</style></head><body><div class="w">
<div class="hd"><div class="lg"><i></i><i></i><i></i><i></i><i></i></div><div><h1>Azul Groove</h1><p><span data-i="p.sub"></span> <b id="ver">...</b></p></div></div>

<h2 data-i="p.goto"></h2>
<div class="g">
<div class="mu" id="mu"><div><b><span class="mi"><svg viewBox="0 0 24 24" width="20" height="20" fill="none"><path d="M9 18V6.5l10-2V16" stroke="#fff" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/><circle cx="6.5" cy="18" r="2.6" fill="#fff"/><circle cx="16.5" cy="16" r="2.6" fill="#fff"/></svg></span><span data-i="p.music.t"></span><span class="tg" data-i="p.music.new"></span></b><small data-i="p.music.s"></small><div class="pb" id="mu-pb" hidden><i></i></div><small id="mu-st"></small></div><button class="btn pri" id="mu-btn"></button></div>
<button class="b p" data-open="chat"><b data-i="p.chat"></b><small data-i="p.chat.s"></small></button>
<button class="b" data-open="local"><b data-i="p.local"></b><small data-i="p.local.s"></small></button>
<button class="b" data-open="login"><b data-i="p.login"></b><small data-i="p.login.s"></small></button>
<button class="b" data-open="home"><b data-i="p.home"></b><small data-i="p.home.s"></small></button>
<button class="b" data-open="ia"><b data-i="p.ia"></b><small data-i="p.ia.s"></small></button>
<button class="b" data-open="docs"><b data-i="p.docs"></b><small data-i="p.docs.s"></small></button>
<a class="b" href="https://discord.gg/g4cW4zRvjf" target="_blank"><b data-i="p.discord"></b><small data-i="p.discord.s"></small></a>
<button class="b" data-md="versions"><b data-i="p.versions"></b><small data-i="p.versions.s"></small></button>
<button class="b" data-md="news"><b data-i="p.news"></b><small data-i="p.news.s"></small></button>
</div>

<h2 data-i="p.set"></h2>
<div class="c">
<div class="r"><div><span data-i="p.theme"></span><small data-i="p.theme.s"></small></div><div class="seg" id="theme"><button data-v="0" data-i="p.th.auto"></button><button data-v="1" data-i="p.th.light"></button><button data-v="2" data-i="p.th.dark"></button></div></div>
<div class="r"><div><span data-i="p.hk"></span><small data-i="p.hk.s"></small></div><label class="sw"><input type="checkbox" id="hk"><span></span></label></div>
<div class="r"><div><span data-i="p.keys"></span><small id="hkinfo"></small></div><select id="preset"></select></div>
<div class="r"><div><span data-i="p.tray"></span><small data-i="p.tray.s"></small></div><label class="sw"><input type="checkbox" id="tray"><span></span></label></div>
<div class="r"><div data-i="p.start"></div><select id="start"><option value="0" data-i="p.st.panel"></option><option value="1" data-i="p.st.site"></option><option value="2" data-i="p.st.chat"></option></select></div>
<div class="r"><div><span data-i="p.lang"></span><small data-i="p.lang.s"></small></div><select id="lang"></select></div>
<div class="r"><div><span data-i="p.data"></span><small data-i="p.data.s"></small></div><button class="btn o" id="data" data-i="p.data.b"></button></div>
</div>

<h2 data-i="p.ai"></h2>
<div class="c"><div class="r"><div><span data-i="p.ai.state"></span><small id="ai-st" data-i="p.loading"></small><div class="pb" id="ai-pb" hidden><i></i></div></div><div class="ac" style="margin:0"><button class="btn o" id="ai-stop" hidden data-i="p.ai.stop"></button><button class="btn" data-open="local" data-i="p.ai.open"></button></div></div></div>
<div class="seg aitabs" id="aitabs"><button data-t="geral" class="on" data-i="p.ai.t.gen"></button><button data-t="modelos" data-i="p.ai.t.mod"></button><button data-t="add" data-i="p.ai.t.add"></button></div>
<div class="aipane" id="aip-geral"><div class="c">
<div class="r"><div><span data-i="p.ai.vk"></span><small id="ai-vkinfo" data-i="p.ai.vk.s"></small></div><label class="sw"><input type="checkbox" id="ai-vk"><span></span></label></div>
<div class="r"><div><span data-i="p.ai.min"></span><small data-i="p.ai.min.s"></small></div><label class="sw"><input type="checkbox" id="aimin"><span></span></label></div>
</div></div>
<div class="aipane" id="aip-modelos" hidden>
<p class="ainote" id="ai-ram"></p>
<div id="ai-list"></div>
</div>
<div class="aipane" id="aip-add" hidden><div class="c">
<div class="r"><div><span data-i="p.ai.url"></span><small data-i="p.ai.url.s"></small></div><div class="ac" style="margin:0"><input class="in" id="ai-url" data-ph="p.ai.url.ph" spellcheck="false"><button class="btn" id="ai-urlgo" data-i="p.dl"></button></div></div>
<div class="r"><div><span data-i="p.ai.imp"></span><small data-i="p.ai.imp.s"></small></div><button class="btn o" id="ai-imp" data-i="p.ai.imp.b"></button></div>
</div></div>
<div id="ai-msg"></div>

<h2 data-i="p.upd"></h2>
<div class="c"><div class="r"><div><span data-i="p.upd.ver"></span> <b id="ver2">...</b><small data-i="p.upd.s"></small></div><button class="btn pri" id="chk"></button></div></div>
<div id="upd"></div>

<p class="ft" data-r="p.ft"></p>
</div>
<div class="md" id="md" hidden><div class="mc"><div class="mh"><b id="mt"></b><div><button class="btn o" id="gh" data-i="p.gh"></button><button class="btn o" id="mx" data-i="p.close"></button></div></div><div id="ml"></div></div></div>
<div class="md" id="rs" hidden><div class="mc rc"><div class="lg" style="margin:0 auto"><i></i><i></i><i></i><i></i><i></i></div><h3 data-i="p.rs.t"></h3><p id="rsp"></p></div></div>
<script>
const $=s=>document.querySelector(s), send=m=>chrome.webview.postMessage(m);
// ---- idioma: os textos chegam prontos do app (T) já no idioma escolhido; {0}, {1}... são preenchidos aqui ----
const T=@T@;
const t=(k,...a)=>(T[k]!=null?T[k]:k).replace(/\{(\d+)\}/g,(m,i)=>a[i]!=null?a[i]:m);
document.querySelectorAll('[data-open]').forEach(b=>b.onclick=()=>send('panel:open:'+b.dataset.open));
document.querySelectorAll('[data-ext]').forEach(b=>b.onclick=()=>send('panel:ext:'+b.dataset.ext));
document.querySelectorAll('#theme button').forEach(b=>b.onclick=()=>{send('panel:set:theme:'+b.dataset.v);mark(+b.dataset.v)});
const mark=v=>document.querySelectorAll('#theme button').forEach(b=>b.classList.toggle('on',+b.dataset.v===v));
$('#hk').onchange=e=>send('panel:set:hotkey:'+(e.target.checked?1:0));
$('#tray').onchange=e=>send('panel:set:tray:'+(e.target.checked?1:0));
$('#preset').onchange=e=>send('panel:set:preset:'+e.target.value);
$('#start').onchange=e=>send('panel:set:start:'+e.target.value);
$('#aimin').onchange=e=>send('panel:set:aimin:'+(e.target.checked?1:0));
$('#data').onclick=()=>send('panel:data');
const el=(n,c,x)=>{const e=document.createElement(n);if(c)e.className=c;if(x!=null)e.textContent=x;return e};
@DD_HELPER@
// texto com só <b>…</b> e <br> permitidos (montado com DOM, sem innerHTML)
function rich(e,s){e.textContent='';let b=null;String(s).split(/(<\/?b>|<br>)/).forEach(p=>{if(p==='<b>')b=el('b');else if(p==='</b>'){if(b)e.appendChild(b);b=null}else if(p==='<br>')e.appendChild(document.createElement('br'));else if(p)(b||e).appendChild(document.createTextNode(p))});if(b)e.appendChild(b)}
document.title=t('p.title');
document.querySelectorAll('[data-i]').forEach(e=>e.textContent=t(e.dataset.i));
document.querySelectorAll('[data-ph]').forEach(e=>e.placeholder=t(e.dataset.ph));
document.querySelectorAll('[data-r]').forEach(e=>rich(e,t(e.dataset.r)));
// Lista suspensa própria (a lista nativa do Windows ignora o tema escuro). Usada também pelo bloco Beta.
function ddify(sel){
 sel.style.display='none';
 const w=el('div','dd'),b=el('button','ddb'),l=el('div','ddl');b.type='button';w.appendChild(b);w.appendChild(l);sel.after(w);
 const sync=()=>{l.textContent='';Array.from(sel.options).forEach(o=>{const d=el('div','ddo'+(o.value===sel.value?' sel':''),o.textContent);
  d.onclick=ev=>{ev.stopPropagation();sel.value=o.value;w.classList.remove('open');sync();sel.dispatchEvent(new Event('change'))};l.appendChild(d)});
  const cur=sel.options[sel.selectedIndex];b.textContent=cur?cur.textContent:''};
 b.onclick=ev=>{ev.stopPropagation();const open=w.classList.contains('open');document.querySelectorAll('.dd.open').forEach(x=>x.classList.remove('open'));w.classList.toggle('open',!open);if(!open)fitDropdown(w,l,260)};
 sel.sync=sync;sync();wireDropdownKeyboard(w,b,l);return w}
document.addEventListener('click',()=>document.querySelectorAll('.dd.open').forEach(x=>x.classList.remove('open')));
ddify($('#preset'));ddify($('#start'));ddify($('#lang'));
$('#lang').onchange=e=>send('panel:lang:'+e.target.value);
document.querySelectorAll('[data-md]').forEach(b=>b.onclick=()=>openMd(b.dataset.md));
function openMd(mode){$('#mt').textContent=t(mode==='news'?'p.news':'p.versions');$('#ml').textContent=t('p.loading');$('#md').hidden=false;send('panel:'+mode)}
$('#mx').onclick=()=>$('#md').hidden=true;
$('#gh').onclick=()=>send('panel:ext:releases');
function showRel(d){const box=$('#ml');box.textContent='';
 if(d.err||!d.items.length){box.textContent=t(d.err?'p.rel.err':'p.rel.none');return}
 d.items.forEach((it,i)=>{const c=el('div','rl'),h=el('h4');
  h.appendChild(el('span','',it.title+(it.version&&it.title.indexOf(it.version)<0?' ('+it.version+')':'')));
  if(it.current)h.appendChild(el('span','tg',t('p.tag.inst')));if(it.pre)h.appendChild(el('span','tg',t('p.tag.pre')));if(it.date)h.appendChild(el('small','',it.date));
  c.appendChild(h);
  if(it.body)c.appendChild(el('pre','',it.body));else if(d.mode==='news')c.appendChild(el('pre','',t('p.rel.nonotes')));
  if(d.mode==='versions'){const a=el('div','ac');
   if(it.setup){const b=el('button','btn',t('p.rel.setup'));b.onclick=()=>send('panel:dl:'+i+':setup');a.appendChild(b)}
   if(it.portable){const b=el('button','btn o',t('p.rel.port'));b.onclick=()=>send('panel:dl:'+i+':portable');a.appendChild(b)}
   if(!it.setup&&!it.portable)a.appendChild(el('small','',t('p.rel.nofiles')));
   c.appendChild(a)}
  box.appendChild(c)})}
const IC_R='<svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 12a9 9 0 1 1-3-6.7L21 8"/><path d="M21 3v5h-5"/></svg>';
const IC_D='<svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 3v12M7 10l5 5 5-5M4 21h16"/></svg>';
function setChk(ic,t,spin,dis){const c=$('#chk');c.innerHTML=ic+'<span>'+t+'</span>';c.classList.toggle('spin',!!spin);c.disabled=!!(spin||dis)}
setChk(IC_R,t('p.chk'));
$('#chk').onclick=()=>{setChk(IC_R,t('p.chking'),true);$('#upd').className='';$('#upd').textContent=t('p.searching');send('panel:update')};

// ---- IA no seu PC (tudo configurado aqui; o chat offline só conversa) ----
const ai={s:null,dl:null};
const sendAi=(n,a)=>send('panel:ai:'+n+':'+(a||''));
const gbTxt=n=>n>=1?n.toLocaleString(document.documentElement.lang||undefined,{minimumFractionDigits:1,maximumFractionDigits:1})+' GB':Math.max(1,Math.round(n*1024))+' MB';
const mb=n=>(n/1048576).toFixed(0);
function renderAiStatus(){const s=ai.s;if(!s)return;
 const act=s.items.find(i=>i.id===s.active),run=s.items.find(i=>i.id===s.running);
 const busy=s.phase==='download'||s.phase==='extract'||s.phase==='load';
 let msg;
 if(busy)msg='⏳ '+s.text+(s.phase==='download'&&s.pct?' '+s.pct+'%':'');
 else if(s.phase==='error')msg='⚠️ '+s.text;
 else if(run)msg=t('p.ai.on',run.name);
 else if(act)msg=t('p.ai.off',act.name);
 else msg=t('p.ai.none');
 $('#ai-st').textContent=msg;
 const pb=$('#ai-pb');pb.hidden=!(busy&&(s.phase==='download'||s.phase==='extract'));pb.firstChild.style.width=(s.pct||0)+'%';
 $('#ai-stop').hidden=!run;
}
function renderAi(){const s=ai.s;if(!s)return;
 renderAiStatus();
 $('#ai-vk').checked=!!s.vulkan;
 $('#ai-vkinfo').textContent=s.engineReady?t('p.ai.vk.in',s.vulkan?'Vulkan':'CPU'):t('p.ai.vk.s');
 $('#ai-ram').textContent=t('p.ai.ram',s.ramGb);
 const box=$('#ai-list');box.textContent='';
 s.items.forEach(i=>{
  const c=el('div','mdl'+(i.id===s.active&&i.have?' use':'')),h=el('h4');
  h.appendChild(el('span','',i.name));
  if(i.rec)h.appendChild(el('span','tg',t('p.tag.rec')));
  if(i.id===s.active&&i.have)h.appendChild(el('span','tg',t('p.tag.use')));
  c.appendChild(h);c.appendChild(el('p','',i.desc));
  const m=[];m.push(i.have?t('p.m.have'):t('p.m.dl',gbTxt(i.gb)));if(i.ram)m.push(t('p.m.ram',i.ram));if(i.lic)m.push(i.lic);
  c.appendChild(el('div','meta',m.join(' · ')));
  const a=el('div','ac'),other=!!s.dl&&s.dl!==i.id;
  if(s.dl===i.id){
   const pb=el('div','pb'),bar=el('i');bar.id='dlbar';pb.appendChild(bar);pb.style.flex='1';pb.style.minWidth='140px';
   const d=ai.dl&&ai.dl.id===i.id?ai.dl:null;bar.style.width=d&&d.total?Math.min(100,d.got*100/d.total)+'%':'0';
   a.appendChild(pb);const tx=el('small','',d&&d.total?mb(d.got)+' / '+mb(d.total)+' MB':t('p.downloading'));tx.id='dltxt';a.appendChild(tx);
   const x=el('button','btn o',t('p.cancel'));x.onclick=()=>sendAi('mdl_cancel');a.appendChild(x);
  }else if(i.have){
   if(i.id!==s.active){const u=el('button','btn',t('p.use'));u.onclick=()=>sendAi('mdl_use',i.id);a.appendChild(u)}
   const x=el('button','btn o dg',t(i.custom?'p.rm':'p.del'));x.onclick=()=>{if(confirm(t(i.custom?'p.rm.q':'p.del.q',i.name)))sendAi('mdl_del',i.id)};a.appendChild(x);
  }else{
   const u=el('button','btn',t('p.dluse'));u.disabled=other||!s.online;u.onclick=()=>sendAi('mdl_dluse',i.id);a.appendChild(u);
   const o=el('button','btn o',t('p.dlonly'));o.disabled=other||!s.online;o.onclick=()=>sendAi('mdl_dl',i.id);a.appendChild(o);
   if(i.custom){const x=el('button','btn o dg',t('p.rm'));x.onclick=()=>sendAi('mdl_del',i.id);a.appendChild(x)}
   if(!s.online)a.appendChild(el('small','',t('p.offnow')));
  }
  c.appendChild(a);box.appendChild(c)})}
function aiTab(t){document.querySelectorAll('#aitabs button').forEach(b=>b.classList.toggle('on',b.dataset.t===t));
 ['geral','modelos','add'].forEach(k=>$('#aip-'+k).hidden=k!==t)}
document.querySelectorAll('#aitabs button').forEach(b=>b.onclick=()=>aiTab(b.dataset.t));
let aiMsgT=0;
function laiMsg(fn,data){
 if(fn==='ai'){ai.s=data;renderAi()}
 else if(fn==='engine'&&ai.s){ai.s.phase=data.phase;ai.s.text=data.text;ai.s.pct=data.pct;renderAiStatus()}
 else if(fn==='aiDl'){ai.dl=data;const b=$('#dlbar');if(b&&data.total){b.style.width=Math.min(100,data.got*100/data.total)+'%';const t=$('#dltxt');if(t)t.textContent=mb(data.got)+' / '+mb(data.total)+' MB'}}
 else if(fn==='aiMsg'){const m=$('#ai-msg');m.textContent=data;clearTimeout(aiMsgT);aiMsgT=setTimeout(()=>m.textContent='',9000)}
}
$('#ai-vk').onchange=e=>sendAi('mdl_vulkan',e.target.checked?'1':'0');
$('#ai-stop').onclick=()=>sendAi('mdl_stop');
$('#ai-imp').onclick=()=>sendAi('mdl_import');
$('#ai-urlgo').onclick=()=>{const v=$('#ai-url').value.trim();if(!v)return;sendAi('mdl_url',v);$('#ai-url').value='';aiTab('modelos')};
$('#ai-url').onkeydown=e=>{if(e.key==='Enter')$('#ai-urlgo').click()};
const muB=$('#mu-btn'),muS=$('#mu-st'),muP=$('#mu-pb');let muOn=false;
function muLabel(){const P='<svg viewBox="0 0 24 24" width="16" height="16" fill="currentColor"><path d="M8 5.5v13a1 1 0 0 0 1.5.86l10.5-6.5a1 1 0 0 0 0-1.72L9.5 4.64A1 1 0 0 0 8 5.5z"/></svg>',D='<svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 4v11M7 10.5l5 5 5-5M5 20h14"/></svg>';muB.innerHTML=(muOn?P:D)+'<span></span>';muB.lastChild.textContent=t(muOn?'p.music.open':'p.music.install')}
function muBar(p){muP.hidden=p==null;if(p!=null)muP.firstChild.style.width=p+'%'}
function muMsg(d){
 if(d.installed!=null)muOn=!!d.installed;
 const ph=d.phase;muS.className='';
 if(ph==='init'||ph==='done'){muB.disabled=false;muBar(null);muS.textContent='';muLabel()}
 else if(ph==='err'){muB.disabled=false;muBar(null);muS.className='er';muS.textContent=d.msg||t('p.music.err');muLabel()}
 else{muB.disabled=true;
  if(ph==='check'){muS.textContent=t('p.music.check');muBar(null)}
  else if(ph==='download'){muS.textContent=t('p.music.dl',d.percent);muBar(d.percent)}
  else if(ph==='install'){muS.textContent=t('p.music.inst');muBar(100)}
  else if(ph==='launch'){muS.textContent=t('p.music.launch');muBar(100)}}
}
muB.onclick=()=>{muB.disabled=true;send('panel:music:go')};muLabel();
chrome.webview.addEventListener('message',e=>{const d=e.data;
 if(d.type==='lai'){laiMsg(d.fn,d.data);return}
 if(d.type==='music'){muMsg(d);return}
 if(d.type==='state'){$('#ver').textContent=$('#ver2').textContent=d.displayVersion||d.version;mark(d.theme);$('#hk').checked=d.hotkeyOn;$('#tray').checked=d.tray;$('#start').value=d.start;$('#start').sync();$('#aimin').checked=!!d.aiMin;if(d.news)openMd('news');
  $('#preset').innerHTML=d.presets.map((p,i)=>'<option value="'+i+'">'+p+'</option>').join('');$('#preset').value=d.preset;$('#preset').sync();
  {const s=$('#lang');s.textContent='';[['auto',t('p.lang.auto')]].concat(d.langs.map(l=>[l.code,l.name])).forEach(x=>{const o=document.createElement('option');o.value=x[0];o.textContent=x[1];s.appendChild(o)});s.value=d.lang;s.sync()}
  $('#hkinfo').textContent=!d.hotkeyOn?'':d.hotkeyOk?t('p.hk.on',d.hotkeyLabel):t('p.hk.bad')}
 if(d.type==='progress'){setChk(IC_D,t('p.dling',d.percent),false,true);$('#upd').className='upnew';rich($('#upd'),t('p.upd.dl',d.percent))}
 if(d.type==='restart'){$('#upd').textContent='';$('#rs').hidden=false;let s=d.seconds;const show=n=>$('#rsp').textContent=t('p.rs.p',Math.max(n,0));show(s);const tm=setInterval(()=>{s--;show(s);if(s<=0)clearInterval(tm)},1000)}
 if(d.type==='releases')showRel(d);
 if(d.type==='update'){const u=$('#upd');
  u.className=d.status==='new'?'upnew':d.status==='ok'?'upok':d.status==='err'?'uperr':'';
  if(d.status==='new'&&d.auto)setChk(IC_D,t('p.downloading'),false,true);else if(d.status==='err')setChk(IC_R,t('p.retry'));else setChk(IC_R,t('p.chk.again'));
  rich(u,d.status==='new'?t('p.upd.new',d.latest,d.current)+(d.msg||''):d.status==='ok'?t('p.upd.ok',d.current):(d.msg||''));
  if(d.url&&(d.status==='new'?!d.auto:d.status!=='ok')){const a=el('a','btn',t(d.status==='new'?'p.upd.get':'p.upd.page'));a.href=d.url;a.target='_blank';u.appendChild(document.createTextNode(' '));u.appendChild(a)}}});
document.addEventListener('visibilitychange',()=>document.documentElement.classList.toggle('hid',document.hidden));
send('panel:get');sendAi('aistate');
</script></body></html>
""";
}
