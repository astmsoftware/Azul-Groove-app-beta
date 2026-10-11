using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;

namespace AzulGroove;

/// <summary>
/// Chat offline do Azul Groove (mesmo visual do chat do site), conversando com uma IA que roda no PC.
///  • Aceita QUALQUER app de IA compatível com o protocolo OpenAI: Ollama, LM Studio, Jan, llama.cpp / LocalAI,
///    KoboldCpp, GPT4All, Text Generation WebUI... (e também OpenRouter, OpenAI, Groq se houver internet).
///  • Detecta sozinho qual app está aberto (portas padrão) e lista os modelos.
///  • As requisições saem do app (C#), então não existe problema de CORS.
///  • Conversas e configurações: %LocalAppData%\AzulGroove\localchat.json
/// Abre pela bandeja ("Chat offline") ou pelo botão da tela "Sem conexão".
/// </summary>
public partial class MainForm
{
    const string LocalChatTitle = "Azul Groove — Chat offline";
    const string LocalChatSystem = "Você é a IA do Azul Groove. Responda em português do Brasil, de forma clara e útil.";

    static readonly HttpClient AiHttp = new() { Timeout = Timeout.InfiniteTimeSpan };
    static readonly JsonSerializerOptions LcJson = new(JsonSerializerDefaults.Web); // camelCase para a página

    static readonly LChatPreset[] LChatPresets =
    {
        new("azul",      "IA do Azul Groove (neste PC)",   "http://127.0.0.1:18741/v1", false, true),
        new("ollama",    "Ollama",                         "http://127.0.0.1:11434/v1", false, true),
        new("lmstudio",  "LM Studio",                      "http://127.0.0.1:1234/v1",  false, true),
        new("jan",       "Jan",                            "http://127.0.0.1:1337/v1",  false, true),
        new("llamacpp",  "llama.cpp / LocalAI",            "http://127.0.0.1:8080/v1",  false, true),
        new("koboldcpp", "KoboldCpp",                      "http://127.0.0.1:5001/v1",  false, true),
        new("gpt4all",   "GPT4All",                        "http://127.0.0.1:4891/v1",  false, true),
        new("textgen",   "Text Generation WebUI",          "http://127.0.0.1:5000/v1",  false, true),
        new("openrouter","OpenRouter (internet)",          "https://openrouter.ai/api/v1", true, false),
        new("openai",    "OpenAI (internet)",              "https://api.openai.com/v1", true, false),
        new("groq",      "Groq (internet)",                "https://api.groq.com/openai/v1", true, false),
        new("custom",    "Outro (compatível com OpenAI)",  "", false, false),
    };

    readonly LChatData lchatData = LChatData.Load();
    readonly LChatLook lx = LChatLook.Load(); // aparência, estilo, memória e 👍 (arquivo localchat-look.json)
    CancellationTokenSource? lchatCts;
    bool lchatBusy;
    string? lchatLogo;

    // A página se identifica pelo título (igual à tela offline)
    bool OnLocalChatPage => !IsDisposed && web.CoreWebView2 != null && web.CoreWebView2.DocumentTitle == LocalChatTitle;


    // ================= Conversas do site -> chat offline =================
    // O chat do site guarda as conversas no localStorage do navegador do app (chave azul_convs_<id do usuário>).
    // Com o site aberto (com internet) o app lê essa chave e copia as conversas para o chat offline.
    // Ao reabrir/continuar offline, as mensagens novas ficam só aqui e não são perdidas numa nova sincronização.
    const string SiteDumpJs = @"(function(){try{
function ck(n){var m=document.cookie.match(new RegExp('(?:^|;\\s*)'+n+'=([^;]*)'));try{return m?decodeURIComponent(m[1]):''}catch(e){return ''}}
function ls(k){try{return localStorage.getItem(k)||''}catch(e){return ''}}
var uid=ck('azul_user_id'),want=uid?'azul_convs_'+uid:'';
var ks=Object.keys(localStorage).filter(function(k){return k.indexOf('azul_convs_')===0&&(!want||k===want)});
var o={convs:ks.map(function(k){return localStorage.getItem(k)}),name:ck('azul_user_name'),avatar:ck('azul_user_avatar'),
 theme:ls('azul_theme'),look:ls('azul_look'),prefs:ls('azul_prefs'),cfg:ls('azul_cfg')};
try{o.key=sessionStorage.getItem('azul_cfg_k')||ls('azul_cfg_k')}catch(e){}
try{if(typeof CURIOS!=='undefined')o.curios=CURIOS}catch(e){}
return JSON.stringify(o);
}catch(e){return '{}'}})()";

    System.Windows.Forms.Timer? siteSyncTimer;
    bool siteSyncBusy;

    void LChatSyncInit()
    {
        web.CoreWebView2.NavigationCompleted += (_, _) => _ = SiteChatSyncAsync();
        siteSyncTimer = new System.Windows.Forms.Timer { Interval = 60_000 };
        siteSyncTimer.Tick += (_, _) => _ = SiteChatSyncAsync();
        siteSyncTimer.Start();
    }

    async Task SiteChatSyncAsync()
    {
        var cw = web.CoreWebView2;
        if (siteSyncBusy || cw == null || IsDisposed || lchatBusy) return;
        var src = cw.Source;
        if (string.IsNullOrEmpty(src) || !src.StartsWith("http", StringComparison.OrdinalIgnoreCase) || !IsInAppHost(src)) return;

        siteSyncBusy = true;
        try
        {
            var raw = await cw.ExecuteScriptAsync(SiteDumpJs);
            var inner = JsonSerializer.Deserialize<string>(raw);
            if (string.IsNullOrEmpty(inner)) return;
            using var doc = JsonDocument.Parse(inner);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            var d = lchatData;
            var changed = LChatApplyProfile(root);

            if (root.TryGetProperty("convs", out var cv) && cv.ValueKind == JsonValueKind.Array)
                foreach (var one in cv.EnumerateArray())
                    if (one.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(one.GetString()))
                        changed |= LChatMergeSite(one.GetString()!);

            // volta: o que foi escrito no chat offline vai para o chat do site (mesmo navegador do app)
            try { changed |= await LChatPushToSiteAsync(cw); } catch { }

            // foto de perfil: baixa uma vez (com internet) e guarda dentro do app para aparecer offline
            var av = RpcMapper.Str(root, "avatar");
            if (av.Length > 0 && av != d.AvatarUrl)
            {
                var data = await LChatDownloadAvatarAsync(av);
                if (data.Length > 0) { d.AvatarUrl = av; d.AvatarData = data; changed = true; }
            }

            if (changed)
            {
                d.Convs = d.Convs
                    .OrderByDescending(c => c.Messages.Count == 0 ? long.MaxValue : c.Messages[^1].Time)
                    .ToList();
                if (LChatCur() == null) d.Cur = d.Convs.FirstOrDefault()?.Id ?? "";
                d.Save();
            }
        }
        catch { /* sem dados / página sem acesso: ignora */ }
        finally { siteSyncBusy = false; }
    }

    // ================= Chat offline -> site =================
    // Usa a própria função de sincronização do site (syGot): ela valida, mescla pelo "u" mais novo,
    // salva no localStorage e redesenha a lista. Só envia conversas com mensagens novas escritas offline.
    async Task<bool> LChatPushToSiteAsync(CoreWebView2 cw)
    {
        var d = lchatData;
        var todo = d.Convs.Where(c => c.Messages.Count > 0 && c.Messages.Count <= 200 &&
            (c.SiteU > 0 ? c.Messages.Count > c.SiteCount : !c.Id.StartsWith("s"))).Take(20).ToList();
        if (todo.Count == 0) return false;

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var sent = new List<(LChatConv c, long u)>();
        var arr = new List<object>();
        long seq = 0;
        foreach (var c in todo)
        {
            var u = c.SiteU > 0 ? Math.Max(now, c.SiteU + 1) : now + seq++;
            var id = c.SiteU > 0 && long.TryParse(c.Id.AsSpan(1), out var sid) ? sid : u;
            arr.Add(new
            {
                id, title = c.Title.Length > 60 ? c.Title[..60] : c.Title, u,
                msgs = c.Messages.Select(m => new
                {
                    r = m.Role == "user" ? "user" : "bot", c = m.Content,
                    t = m.Time > 0 ? DateTimeOffset.FromUnixTimeSeconds(m.Time).ToLocalTime().ToString("HH:mm") : "",
                    ts = m.Time > 0 ? m.Time * 1000 : u
                }).ToList()
            });
            sent.Add((c, u));
        }

        var payload = JsonSerializer.Serialize(arr);
        var raw = await cw.ExecuteScriptAsync(
            "(function(){try{if(typeof syGot!=='function'||typeof busy==='undefined'||busy)return 0;syGot({t:'snap',d:" + payload + "});return 1}catch(e){return 0}})()");
        if (raw.Trim() != "1") return false;

        foreach (var (c, u) in sent)
        {
            if (c.SiteU == 0) { var old = c.Id; c.Id = "s" + u; if (d.Cur == old) d.Cur = c.Id; }
            c.SiteU = u; c.SiteCount = c.Messages.Count;
        }
        return true;
    }

    // Nome, tema, animação, estilo, tópicos de curiosidade, curiosidades e a IA configurada no site
    bool LChatApplyProfile(JsonElement r)
    {
        var d = lchatData;
        var ch = false;

        var name = RpcMapper.Str(r, "name").Trim();
        if (name.Length > 0 && name.Length <= 80 && name != d.Name) { d.Name = name; ch = true; }

        // tema: só aplica quando o tema DO SITE muda (não briga com o botão de tema daqui)
        var th = RpcMapper.Str(r, "theme");
        if ((th is "auto" or "light" or "dark") && th != d.SiteTheme) { d.Theme = th; d.SiteTheme = th; ch = true; }

        var look = RpcMapper.Str(r, "look");
        var animOff = false;
        if (look.Length > 0)
        {
            try
            {
                using var ld = JsonDocument.Parse(look);
                animOff = ld.RootElement.TryGetProperty("anim", out var a) && a.ValueKind == JsonValueKind.False;
            }
            catch { }
        }
        if (animOff != d.AnimOff) { d.AnimOff = animOff; ch = true; }

        var prefs = RpcMapper.Str(r, "prefs");
        if (prefs.Length > 0)
        {
            try
            {
                using var pd = JsonDocument.Parse(prefs);
                var tone = RpcMapper.Str(pd.RootElement, "tone");
                if (tone.Length > 20) tone = "";
                if (tone != d.Tone) { d.Tone = tone; ch = true; }
                if (pd.RootElement.TryGetProperty("topics", out var tp) && tp.ValueKind == JsonValueKind.Array)
                {
                    var l = tp.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                              .Select(x => x.GetString()!).Take(20).ToList();
                    if (!l.SequenceEqual(d.Topics)) { d.Topics = l; ch = true; }
                }
            }
            catch { }
        }

        // IA configurada no site (provedor / URL / modelo). A chave só é copiada se você ligar "usar a IA do site".
        var cfg = RpcMapper.Str(r, "cfg");
        if (cfg.Length > 0)
        {
            try
            {
                using var cd = JsonDocument.Parse(cfg);
                var prov = RpcMapper.Str(cd.RootElement, "provider");
                var b = RpcMapper.Str(cd.RootElement, "base");
                if (b.Length == 0) b = LChatPresets.FirstOrDefault(x => x.Id == prov)?.Base ?? "";
                var model = RpcMapper.Str(cd.RootElement, "models").Split(',')[0].Trim();
                if (prov.Length <= 30 && b.Length <= 300 && model.Length <= 200 &&
                    (prov != d.SiteProvider || b != d.SiteBase || model != d.SiteModel))
                { d.SiteProvider = prov; d.SiteBase = b; d.SiteModel = model; ch = true; }
            }
            catch { }
        }
        var key = d.PreferSite ? RpcMapper.Str(r, "key").Trim() : "";
        if (key.Length > 400) key = "";
        if (key != d.SiteKey && (key.Length > 0 || !d.PreferSite)) { d.SiteKey = key; ch = true; }

        // curiosidades do site (a lista embutida na página serve de reserva)
        if (r.TryGetProperty("curios", out var cu) && cu.ValueKind == JsonValueKind.Array &&
            cu.GetArrayLength() is > 0 and <= 400)
        {
            var rawC = cu.GetRawText();
            if (rawC.Length <= 120_000 && rawC != d.CuriosJson) { d.CuriosJson = rawC; ch = true; }
        }
        return ch;
    }

    static async Task<string> LChatDownloadAvatarAsync(string url)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps || IsLocalHost(u)) return "";
            using var cts = new CancellationTokenSource(8000);
            using var resp = await AiHttp.GetAsync(u, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!resp.IsSuccessStatusCode) return "";
            var mt = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (!mt.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return "";
            var bytes = await resp.Content.ReadAsByteArrayAsync(cts.Token);
            if (bytes.Length is 0 or > 600_000) return "";
            return $"data:{mt};base64,{Convert.ToBase64String(bytes)}";
        }
        catch { return ""; }
    }

    // Texto do bot no site pode ter <b>, <br> e entidades HTML: vira texto puro
    static string LChatClean(string s)
    {
        s = Regex.Replace(s, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, "<[^>]+>", "");
        return WebUtility.HtmlDecode(s).Trim();
    }

    bool LChatMergeSite(string json)
    {
        var d = lchatData;
        var changed = false;
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return false;

        foreach (var c in doc.RootElement.EnumerateArray())
        {
            if (c.ValueKind != JsonValueKind.Object || !c.TryGetProperty("id", out var idEl)) continue;
            var id = "s" + idEl.ToString();
            if (id.Length > 40) continue;
            var title = RpcMapper.Str(c, "title").Trim();
            long u = c.TryGetProperty("u", out var uEl) && uEl.TryGetInt64(out var uv) ? uv : 0;

            var msgs = new List<LChatMsg>();
            if (c.TryGetProperty("msgs", out var ms) && ms.ValueKind == JsonValueKind.Array)
                foreach (var m in ms.EnumerateArray())
                {
                    var text = LChatClean(RpcMapper.Str(m, "c"));
                    if (text.Length == 0) continue;
                    long ts = m.TryGetProperty("ts", out var tEl) && tEl.TryGetInt64(out var tv) ? tv / 1000 : 0;
                    msgs.Add(new LChatMsg { Role = RpcMapper.Str(m, "r") == "user" ? "user" : "assistant", Content = text, Time = ts });
                }
            if (msgs.Count == 0) continue; // conversa vazia no site: não importa

            if (u == 0) u = 1; // marca como "veio do site"
            var ex = d.Convs.FirstOrDefault(x => x.Id == id);
            if (ex == null)
            {
                d.Convs.Add(new LChatConv
                {
                    Id = id, Title = title.Length > 0 ? (title.Length > 80 ? title[..80] : title) : "Conversa do site",
                    Messages = msgs, SiteU = u, SiteCount = msgs.Count
                });
                changed = true;
            }
            else if (ex.SiteU != u || ex.SiteCount != msgs.Count)
            {
                var extras = ex.Messages.Skip(ex.SiteCount).ToList(); // o que você escreveu offline continua aqui
                ex.Messages = msgs.Concat(extras).ToList();
                ex.SiteCount = msgs.Count; ex.SiteU = u;
                if (title.Length > 0) ex.Title = title.Length > 80 ? title[..80] : title;
                changed = true;
            }
        }
        return changed;
    }

    // ================= Menu / abertura =================
    ToolStripMenuItem LocalChatMenuItem() =>
        new("Chat offline (IA no PC)", null, (_, _) => { ShowWindow(); ShowLocalChat(); });

    void ShowLocalChat() => _ = ShowLocalChatAsync();

    async Task ShowLocalChatAsync()
    {
        // Se o site está aberto agora (com internet), copia as conversas dele antes de trocar de tela
        try { await SiteChatSyncAsync(); } catch { }
        var cw = web.CoreWebView2;
        if (cw == null || IsDisposed) return;
        panelActive = false; // não injetar o bloco do painel nesta página
        skipSplash = true;   // a animação de abertura não deve sobrescrever a tela
        cw.NavigateToString(LocalChatHtml
            .Replace("@TITLE@", LocalChatTitle)
            .Replace("@LOGO@", LChatLogo()));
    }

    // Logo da Azul Groove (flor de lótus) embutida: aparece mesmo sem internet
    string LChatLogo() => lchatLogo ??= LChatAssets.Lotus;

    // ================= Mensagens da página (só páginas locais, nunca o site) =================
    bool HandleLocalChatMessage(CoreWebView2WebMessageReceivedEventArgs e)
    {
        string? t;
        try { t = e.TryGetWebMessageAsString(); } catch { return false; }
        if (t == null || !t.StartsWith("lchat:", StringComparison.Ordinal)) return false;
        if (e.Source.StartsWith("http", StringComparison.OrdinalIgnoreCase) || !OnLocalChatPage) return true;

        var cmd = t[6..];
        var i = cmd.IndexOf(':');
        var name = i < 0 ? cmd : cmd[..i];
        var arg = i < 0 ? "" : cmd[(i + 1)..];
        var d = lchatData;

        switch (name)
        {
            case "init": _ = LChatInitAsync(); break;
            case "probe": _ = LChatRefreshAsync(false); break;
            case "aistate": case "mdl_dl": case "mdl_dluse": case "mdl_cancel": case "mdl_use": case "mdl_del": case "mdl_url": case "mdl_import": case "mdl_vulkan":
                LAiHandle(name, arg); break;
            case "send": _ = LChatSendAsync(arg); break;
            case "stop": try { lchatCts?.Cancel(); } catch { } break;
            case "back": BeginInvoke(() => ShowPanel()); break;
            case "cfg": _ = LChatSaveCfgAsync(arg); break;
            case "cfgmodels": _ = LChatTestModelsAsync(arg); break;
            case "theme":
                if (arg is "auto" or "light" or "dark") { d.Theme = arg; d.Save(); }
                break;
            case "model":
                if (arg.Length is > 0 and <= 200) { d.Model = arg; d.Save(); }
                break;
            case "new": if (!lchatBusy) { LChatNew(); _ = LChatPushStateAsync(); } break;
            case "open":
                if (!lchatBusy && d.Convs.Any(c => c.Id == arg)) { d.Cur = arg; d.Save(); _ = LChatPushStateAsync(); }
                break;
            case "del":
                if (lchatBusy) break;
                d.Convs.RemoveAll(c => c.Id == arg);
                if (d.Cur == arg) d.Cur = d.Convs.FirstOrDefault()?.Id ?? "";
                d.Save(); _ = LChatPushStateAsync();
                break;
            case "delall":
                if (lchatBusy) break;
                d.Convs.Clear(); d.Cur = ""; d.Save(); _ = LChatPushStateAsync();
                break;
            case "clear":
                if (lchatBusy) break;
                LChatCur()?.Messages.Clear(); d.Save(); _ = LChatPushStateAsync();
                break;
            case "rename":
            {
                var p = arg.IndexOf('|');
                if (p <= 0) break;
                var c = d.Convs.FirstOrDefault(x => x.Id == arg[..p]);
                var title = arg[(p + 1)..].Trim();
                if (c == null || title.Length == 0) break;
                c.Title = title.Length > 80 ? title[..80] : title;
                d.Save();
                _ = LChatPushAsync("convs", LChatConvsPayload());
                break;
            }
            case "copy":
                if (arg.Length is > 0 and <= 200_000) BeginInvoke(() => { try { Clipboard.SetText(arg); } catch { } });
                break;
            case "copyconv":
            {
                var c = LChatCur();
                if (c != null && c.Messages.Count > 0)
                {
                    var txt = LChatToText(c);
                    BeginInvoke(() => { try { Clipboard.SetText(txt); } catch { } });
                }
                break;
            }
            case "export": BeginInvoke(() => LChatExport()); break;
            case "exportall": BeginInvoke(() => LChatExportAll()); break;
            case "look": LChatSetLook(arg); break;
            case "prefs": LChatSetPrefs(arg); break;
            case "like": LChatSetLike(arg); break;
            case "clearlikes": lx.Likes.Clear(); lx.Save(); break;
            case "wall":
                if (arg.StartsWith("data:image/", StringComparison.Ordinal) && arg.Length <= 3_000_000) { lx.WallImg = arg; lx.Save(); }
                break;
            case "resetlook":
                lx.ResetLook(); lx.Save();
                d.Theme = "auto"; d.Save();
                _ = LChatPushStateAsync();
                _ = LChatPushAsync("wall", "");
                break;
            case "clearcfg":
                if (lchatBusy) break;
                d.Provider = ""; d.Base = ""; d.Key = ""; d.Model = ""; d.PreferSite = false; d.SiteKey = "";
                d.Save();
                _ = LChatPushStateAsync();
                _ = LChatRefreshAsync(false);
                break;
            case "support":
                BeginInvoke(() =>
                {
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://livepix.gg/astmsoftware") { UseShellExecute = true }); } catch { }
                });
                break;
        }
        return true;
    }

    async Task LChatPushAsync(string fn, object? arg)
    {
        try
        {
            var cw = web.CoreWebView2;
            // IA do PC configurada pelo painel: o estado vai para o painel (não para a página do chat offline)
            if (cw != null && !IsDisposed && OnPanel && (fn is "ai" or "engine" or "aiDl" or "aiMsg"))
            {
                cw.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "lai", fn, data = arg }, LcJson));
                return;
            }
            if (cw == null || IsDisposed || !OnLocalChatPage) return;
            await cw.ExecuteScriptAsync($"window.__lc&&window.__lc.{fn}({JsonSerializer.Serialize(arg, LcJson)})");
        }
        catch { /* página trocada: ignora */ }
    }

    // ================= Conversas =================
    static long LChatNow() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    LChatConv? LChatCur() => lchatData.Convs.FirstOrDefault(c => c.Id == lchatData.Cur);

    LChatConv LChatNew()
    {
        var cur = LChatCur();
        if (cur != null && cur.Messages.Count == 0) return cur; // já existe uma vazia: reaproveita
        var c = new LChatConv { Id = Guid.NewGuid().ToString("N")[..8], Title = "Nova conversa" };
        lchatData.Convs.Insert(0, c);
        lchatData.Cur = c.Id;
        lchatData.Save();
        return c;
    }

    object LChatConvItem(LChatConv c) =>
        new { id = c.Id, title = c.Title, site = c.SiteU > 0, t = c.Messages.Count == 0 ? LChatNow() : c.Messages[^1].Time };

    object LChatConvsPayload() => new
    {
        convs = lchatData.Convs.Select(LChatConvItem),
        cur = lchatData.Cur
    };

    object? LChatCuriosOrNull()
    {
        if (lchatData.CuriosJson.Length == 0) return null;
        try { return JsonSerializer.Deserialize<JsonElement>(lchatData.CuriosJson); } catch { return null; }
    }

    object LChatStatePayload()
    {
        var d = lchatData;
        object? siteAi = null;
        if (d.SiteBase.Length > 0 && d.SiteModel.Length > 0)
        {
            var local = Uri.TryCreate(d.SiteBase, UriKind.Absolute, out var sbu) && IsLocalHost(sbu);
            siteAi = new { provider = d.SiteProvider, model = d.SiteModel, hasKey = d.SiteKey.Length > 0, needsKey = !local };
        }
        return new
        {
            provider = d.Provider,
            @base = d.Base,
            hasKey = d.Key.Length > 0,
            model = d.Model,
            theme = d.Theme,
            presets = LChatPresets.Select(p => new { id = p.Id, name = p.Name, @base = p.Base, key = p.Key, local = p.Local }),
            convs = d.Convs.Select(LChatConvItem),
            cur = d.Cur,
            messages = LChatCur()?.Messages ?? new List<LChatMsg>(),
            name = d.Name,
            avatar = d.AvatarData,
            look = new { wall = lx.Wall, dim = lx.Dim, fx = lx.Fx, anim = lx.AnimSet ? lx.Anim : !d.AnimOff, colors = lx.Colors },
            prefs = new
            {
                tone = lx.Tone ?? d.Tone, kb = lx.Kb, deep = lx.Deep, style = lx.Style, mem = lx.Mem, learn = lx.Learn,
                topics = lx.Topics ?? d.Topics, likes = lx.Likes.Count
            },
            preferSite = d.PreferSite,
            siteAi = siteAi,
            curios = LChatCuriosOrNull()
        };
    }

    Task LChatPushStateAsync() => LChatPushAsync("state", LChatStatePayload());

    static string LChatToText(LChatConv c)
    {
        var sb = new StringBuilder();
        sb.AppendLine(c.Title).AppendLine(new string('-', Math.Min(c.Title.Length, 60))).AppendLine();
        foreach (var m in c.Messages)
            sb.AppendLine((m.Role == "user" ? "Você" : "IA") + ":").AppendLine(m.Content).AppendLine();
        return sb.ToString();
    }

    void LChatExport()
    {
        var c = LChatCur();
        if (c == null || c.Messages.Count == 0) return;
        var safe = string.Concat(c.Title.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        using var dlg = new SaveFileDialog { Filter = "Texto (*.txt)|*.txt", FileName = safe + ".txt" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try { File.WriteAllText(dlg.FileName, LChatToText(c), Encoding.UTF8); } catch { }
    }

    // ================= Conexão com a IA (protocolo OpenAI) =================
    static bool LChatIsLocalPreset(string id) => LChatPresets.Any(p => p.Id == id && p.Local);

    // Aceita https em qualquer lugar e http só no próprio PC / rede local. Sem caminho -> acrescenta /v1.
    static string? LChatNormalizeBase(string b, out string why)
    {
        why = "";
        b = b.Trim().TrimEnd('/');
        if (!Uri.TryCreate(b, UriKind.Absolute, out var u)) { why = "URL inválida."; return null; }
        if (u.Scheme != Uri.UriSchemeHttps && !(u.Scheme == Uri.UriSchemeHttp && IsLocalHost(u)))
        {
            why = "Use https:// (ou http:// só para IA no seu PC / rede local).";
            return null;
        }
        if (u.AbsolutePath == "/") b += "/v1";
        return b;
    }

    static async Task<(bool Ok, List<string> Models, string Err)> LChatFetchModelsAsync(string baseUrl, string key, int timeoutMs = 6000)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            using var req = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/models");
            if (key.Length > 0) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
            using var resp = await AiHttp.SendAsync(req, cts.Token);
            if (!resp.IsSuccessStatusCode) return (false, new List<string>(), $"respondeu {(int)resp.StatusCode}");

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
            var list = new List<string>();
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                (root.TryGetProperty("data", out var arr) || root.TryGetProperty("models", out arr)) &&
                arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in arr.EnumerateArray())
                {
                    var n = RpcMapper.Str(m, "id");
                    if (n.Length == 0) n = RpcMapper.Str(m, "name");
                    if (n.Length > 0) list.Add(n);
                }
            }
            return (true, list, "");
        }
        catch (Exception ex) { return (false, new List<string>(), ex is OperationCanceledException ? "tempo esgotado" : ex.Message); }
    }

    // Quais apps de IA locais estão abertos agora (testa as portas padrão em paralelo)
    static async Task<List<string>> LChatProbeAsync()
    {
        var local = LChatPresets.Where(p => p.Local).ToArray();
        var res = await Task.WhenAll(local.Select(async p =>
            (Id: p.Id, Up: (await LChatFetchModelsAsync(p.Base, "", 1200)).Ok)));
        return res.Where(r => r.Up).Select(r => r.Id).ToList();
    }

    async Task LChatInitAsync()
    {
        await LChatPushStateAsync();     // a tela aparece já, a detecção vem depois
        if (lx.WallImg.Length > 0) await LChatPushAsync("wall", lx.WallImg);
        await LAiOnInitAsync();
        await LChatRefreshAsync(true);
    }

    async Task LChatRefreshAsync(bool autoPick)
    {
        var d = lchatData;
        var det = await LChatProbeAsync();
        if (autoPick && d.Base.Length == 0 && det.Count > 0)
        {
            var p = LChatPresets.First(x => x.Id == det[0]);
            d.Provider = p.Id; d.Base = p.Base; d.Save();
        }

        bool up = false; var models = new List<string>(); string err = "";
        if (d.Base.Length > 0)
        {
            (up, models, err) = await LChatFetchModelsAsync(d.Base, d.Key);
            if (up && models.Count > 0 && (d.Model.Length == 0 || (LChatIsLocalPreset(d.Provider) && !models.Contains(d.Model))))
            {
                d.Model = models[0]; d.Save();
            }
        }
        await LChatPushAsync("status", new
        {
            up, models, err, model = d.Model, provider = d.Provider, @base = d.Base,
            hasKey = d.Key.Length > 0, detected = det, preferSite = d.PreferSite
        });
    }

    async Task LChatTestModelsAsync(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var b = LChatNormalizeBase(RpcMapper.Str(doc.RootElement, "base"), out var why);
            if (b == null) { await LChatPushAsync("cfgModels", new { ok = false, models = new List<string>(), err = why }); return; }
            var key = RpcMapper.Str(doc.RootElement, "key").Trim();
            if (key.Length == 0) key = lchatData.Key;
            var (ok, models, err) = await LChatFetchModelsAsync(b, key);
            await LChatPushAsync("cfgModels", new { ok, models, err });
        }
        catch { await LChatPushAsync("cfgModels", new { ok = false, models = new List<string>(), err = "dados inválidos" }); }
    }

    async Task LChatSaveCfgAsync(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var norm = LChatNormalizeBase(RpcMapper.Str(r, "base"), out var why);
            if (norm == null) { await LChatPushAsync("cfgResult", new { ok = false, msg = why }); return; }

            var d = lchatData;
            var prov = RpcMapper.Str(r, "provider");
            var key = RpcMapper.Str(r, "key").Trim();
            var model = RpcMapper.Str(r, "model").Trim();
            bool clear = r.TryGetProperty("clearKey", out var ck) && ck.ValueKind == JsonValueKind.True;

            d.Provider = LChatPresets.Any(p => p.Id == prov) ? prov : "custom";
            d.Base = norm;
            if (clear) d.Key = ""; else if (key.Length > 0 && key.Length <= 400) d.Key = key;
            d.Model = model.Length <= 200 ? model : "";
            d.PreferSite = r.TryGetProperty("preferSite", out var ps) && ps.ValueKind == JsonValueKind.True;
            if (!d.PreferSite) d.SiteKey = ""; // sem a opção ligada, a chave do site não fica guardada aqui
            d.Save();

            await LChatPushAsync("cfgResult", new { ok = true, msg = "" });
            await LChatRefreshAsync(false);
        }
        catch { await LChatPushAsync("cfgResult", new { ok = false, msg = "Dados inválidos." }); }
    }

    // ================= Envio (streaming) =================
    static string LChatDelta(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("choices", out var ch) || ch.ValueKind != JsonValueKind.Array || ch.GetArrayLength() == 0) return "";
        var c0 = ch[0];
        if (c0.TryGetProperty("delta", out var dl) && dl.ValueKind == JsonValueKind.Object) return RpcMapper.Str(dl, "content");
        if (c0.TryGetProperty("message", out var ms) && ms.ValueKind == JsonValueKind.Object) return RpcMapper.Str(ms, "content");
        return RpcMapper.Str(c0, "text");
    }

    static readonly Dictionary<string, string> LChatTones = new()
    {
        ["serio"] = "Use tom sério, objetivo e profissional: sem gírias, sem piadas e sem emojis; respostas diretas e precisas.",
        ["casual"] = "Use tom descontraído e leve, como numa conversa entre amigos, com no máximo um emoji quando fizer sentido.",
        ["humor"] = "Use tom bem-humorado, com trocadilhos e piadas leves, sem exagerar. A precisão vem antes da piada: nunca invente fatos para fazer graça.",
        ["didatico"] = "Explique de forma didática, em passos curtos e com exemplos simples.",
    };

    const string LChatDeep = "MODO APROFUNDADO: dê uma resposta completa e bem organizada, com contexto, explicação em partes (títulos curtos em **negrito**), exemplos práticos e, no fim, um resumo de 1 ou 2 frases. Mais conteúdo útil, sem enrolação, e sem inventar nada que você não saiba.";

    static readonly HashSet<string> LChatWalls = new() { "none", "aurora", "neon", "lotus", "sunset", "forest", "grafite", "ceu", "rosa", "img" };
    static readonly HashSet<string> LChatTopicIds = new() { "musica", "aviacao", "ambiente", "brasil", "geral" };

    // Monta as instruções da IA com tudo o que você configurou (dados do Azul Groove, tom, aprofundado, jeito de falar, memória, 👍)
    string LChatBuildSystem()
    {
        var d = lchatData;
        var sys = lx.Kb == "off"
            ? "Você é a IA do Azul Groove, um assistente de uso geral. Responda em português do Brasil, de forma clara e útil. Não fale do bot de música Azul Groove, a menos que o usuário peça."
            : LocalChatSystem;
        var first = d.Name.Split(' ')[0];
        if (first.Length is > 0 and <= 40) sys += " O nome do usuário é " + first + ".";
        if (LChatTones.TryGetValue(lx.Tone ?? d.Tone, out var tone)) sys += " " + tone;
        if (lx.Deep) sys += " " + LChatDeep;
        if (lx.Style.Length > 0) sys += " Jeito de responder pedido pelo usuário: " + lx.Style;
        if (lx.Mem.Count > 0) sys += " Lembre-se destas informações sobre o usuário: " + string.Join("; ", lx.Mem) + ".";
        if (lx.Learn && lx.Likes.Count > 0)
        {
            sys += " O usuário aprovou estas respostas antigas; imite o estilo delas (tamanho, tom e organização), sem repetir o conteúdo:";
            foreach (var l in lx.Likes) sys += $" [Pergunta: {l.Q} | Resposta: {l.A}]";
        }
        return sys;
    }

    // ---- configurações vindas da página ----
    void LChatSetLook(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var wall = RpcMapper.Str(r, "wall");
            if (LChatWalls.Contains(wall) && (wall != "img" || lx.WallImg.Length > 0)) lx.Wall = wall;
            if (r.TryGetProperty("dim", out var dm) && dm.TryGetInt32(out var dv)) lx.Dim = Math.Clamp(dv, 0, 80);
            var fx = RpcMapper.Str(r, "fx");
            if (fx is "all" or "orbs" or "eq" or "off") lx.Fx = fx;
            if (r.TryGetProperty("anim", out var an) && an.ValueKind is JsonValueKind.True or JsonValueKind.False) { lx.Anim = an.GetBoolean(); lx.AnimSet = true; }
            if (r.TryGetProperty("colors", out var co) && co.ValueKind is JsonValueKind.True or JsonValueKind.False) lx.Colors = co.GetBoolean();
            lx.Save();
        }
        catch { }
    }

    void LChatSetPrefs(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var tone = RpcMapper.Str(r, "tone");
            lx.Tone = tone.Length == 0 || LChatTones.ContainsKey(tone) ? tone : "";
            var kb = RpcMapper.Str(r, "kb");
            lx.Kb = kb == "off" ? "off" : "auto";
            lx.Deep = r.TryGetProperty("deep", out var dp) && dp.ValueKind == JsonValueKind.True;
            lx.Learn = !r.TryGetProperty("learn", out var ln) || ln.ValueKind != JsonValueKind.False;
            var st = RpcMapper.Str(r, "style").Trim();
            lx.Style = st.Length > 400 ? st[..400] : st;
            if (r.TryGetProperty("mem", out var mm) && mm.ValueKind == JsonValueKind.Array)
                lx.Mem = mm.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString()!.Trim()).Where(x => x.Length > 0)
                    .Select(x => x.Length > 200 ? x[..200] : x).Distinct().Take(20).ToList();
            if (r.TryGetProperty("topics", out var tp) && tp.ValueKind == JsonValueKind.Array)
                lx.Topics = tp.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString()!).Where(LChatTopicIds.Contains).Distinct().ToList();
            lx.Save();
        }
        catch { }
    }

    void LChatSetLike(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var a = RpcMapper.Str(r, "a").Trim();
            var q = RpcMapper.Str(r, "q").Trim();
            if (a.Length == 0) return;
            if (a.Length > 400) a = a[..400];
            if (q.Length > 200) q = q[..200];
            lx.Likes.RemoveAll(x => x.A == a);
            if (r.TryGetProperty("on", out var onEl) && onEl.ValueKind == JsonValueKind.True)
            {
                lx.Likes.Add(new LChatLike { Q = q, A = a });
                if (lx.Likes.Count > 6) lx.Likes.RemoveRange(0, lx.Likes.Count - 6);
            }
            lx.Save();
        }
        catch { }
    }

    void LChatExportAll()
    {
        if (lchatData.Convs.Count == 0) return;
        using var dlg = new SaveFileDialog { Filter = "JSON (*.json)|*.json", FileName = "azul-groove-conversas.json" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var all = lchatData.Convs.Select(c => new
            {
                id = c.Id, title = c.Title,
                messages = c.Messages.Select(m => new { role = m.Role, content = m.Content, time = m.Time })
            });
            var opt = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(all, opt), Encoding.UTF8);
        }
        catch { }
    }

    async Task LChatSendAsync(string text)
    {
        text = text.Trim();
        if (text.Length == 0 || text.Length > 8000 || lchatBusy) return;
        lchatBusy = true;
        var d = lchatData;
        var cts = lchatCts = new CancellationTokenSource();
        var reply = new StringBuilder();
        LChatConv? conv = null;
        try
        {
            // IA do próprio app: liga o motor se ainda não estiver de pé
            if (d.Provider == "azul" && !await LAiEnsureRunningAsync())
            {
                // sem nenhum modelo baixado: cartão com opções (sem texto técnico); outro problema: mensagem normal
                if (lai.Active.Length == 0 || !LAiHave(lai.Active)) await LChatPushAsync("needmodel", null);
                else await LChatPushAsync("error", "A IA do Azul Groove não iniciou. " + laiText);
                return;
            }

            // IA local: descobre o modelo se faltar
            if (d.Base.Length > 0 && d.Model.Length == 0)
            {
                var (ok, ms0, _) = await LChatFetchModelsAsync(d.Base, d.Key);
                if (ok && ms0.Count > 0) { d.Model = ms0[0]; d.Save(); }
            }

            // Ordem de tentativa: 1) IA do site (se ligada e houver internet)  2) IA do seu PC
            var profiles = new List<(string Base, string Key, string Model)>();
            if (d.PreferSite && d.SiteModel.Length > 0 && NetworkUp()
                && LChatNormalizeBase(d.SiteBase, out _) is { } sb
                && (d.SiteKey.Length > 0 || (Uri.TryCreate(sb, UriKind.Absolute, out var su) && IsLocalHost(su))))
                profiles.Add((sb, d.SiteKey, d.SiteModel));
            if (d.Base.Length > 0 && d.Model.Length > 0) profiles.Add((d.Base, d.Key, d.Model));

            if (profiles.Count == 0)
            {
                await LChatPushAsync("needmodel", null); // cartão com botões: baixar a IA do app, usar outra IA ou ver modelos
                return;
            }

            conv = LChatCur() ?? LChatNew();
            conv.Messages.Add(new LChatMsg { Role = "user", Content = text, Time = LChatNow() });
            if (conv.Title == "Nova conversa") conv.Title = text.Length > 40 ? text[..40] + "…" : text;
            d.Save();
            await LChatPushAsync("convs", LChatConvsPayload());

            for (var i = 0; i < profiles.Count; i++)
            {
                var pf = profiles[i];
                try { await LChatStreamAsync(pf.Base, pf.Key, pf.Model, conv, reply, cts.Token); break; }
                // se a IA do site falhar antes de mostrar qualquer coisa, tenta a IA do PC
                catch (Exception ex) when (ex is not OperationCanceledException && reply.Length == 0 && i < profiles.Count - 1) { }
            }
        }
        catch (OperationCanceledException) { /* o usuário clicou em Parar */ }
        catch (Exception ex)
        {
            await LChatPushAsync("error", "Não consegui falar com a IA (" + ex.Message + ").");
        }
        finally
        {
            var now = LChatNow();
            if (conv != null && reply.Length > 0)
                conv.Messages.Add(new LChatMsg { Role = "assistant", Content = reply.ToString(), Time = now });
            d.Save();
            lchatBusy = false;
            await LChatPushAsync("done", new { time = now });
        }
    }

    async Task LChatStreamAsync(string baseUrl, string key, string model, LChatConv conv, StringBuilder reply, CancellationToken ct)
    {
        var d = lchatData;
        var sys = LChatBuildSystem();

        var msgs = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = sys } };
        foreach (var m in conv.Messages.TakeLast(20))
            msgs.Add(new JsonObject { ["role"] = m.Role, ["content"] = m.Content });
        var body = new JsonObject { ["model"] = model, ["messages"] = msgs, ["stream"] = true };

        using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/chat/completions")
        { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        if (key.Length > 0) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);

        using var resp = await AiHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var e = await resp.Content.ReadAsStringAsync(ct);
            if (e.Length > 200) e = e[..200] + "…";
            throw new HttpRequestException($"a IA respondeu {(int)resp.StatusCode}: {e}");
        }

        using var sr = new StreamReader(await resp.Content.ReadAsStreamAsync(ct));
        string? line;
        while ((line = await sr.ReadLineAsync(ct)) != null)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var payload = line[5..].Trim();
            if (payload.Length == 0) continue;
            if (payload == "[DONE]") break;
            string c;
            try { c = LChatDelta(payload); } catch { continue; }
            if (c.Length > 0) { reply.Append(c); await LChatPushAsync("chunk", c); }
        }
    }

    // ================= Design da página (mesmo visual do chat do site, sem depender de internet) =================
    const string LocalChatHtml = """
<!doctype html><html lang="pt-BR"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>@TITLE@</title><style>
@property --c1{syntax:'<color>';inherits:true;initial-value:#2438aa}
@property --c2{syntax:'<color>';inherits:true;initial-value:#5865f2}
@property --c3{syntax:'<color>';inherits:true;initial-value:#1fb6ff}
@keyframes cshift{0%{--c1:#2438aa;--c2:#5865f2;--c3:#1fb6ff}30%{--c1:#4c1d95;--c2:#a855f7;--c3:#ec4899}62%{--c1:#0e7490;--c2:#6366f1;--c3:#22d3ee}100%{--c1:#2b2fb0;--c2:#7c3aed;--c3:#38bdf8}}
:root{color-scheme:dark;--tx:#fff;--inp:#1e1f22;--soft:rgba(255,255,255,.06);--hi:#e8eaed;--bg:#0f1117;--side:#12141a;--card:#1c1f26;--line:rgba(255,255,255,.08);--p:#5865F2;--t:#dbdee1;--m:#949ba4;--c1:#2438aa;--c2:#5865f2;--c3:#1fb6ff}
html[data-colors=on]{animation:cshift 28s ease-in-out infinite alternate}
html[data-anim=off]{animation:none!important}
:root[data-theme=light]{--bg:#f4f6fb;--side:#fff;--card:#fff;--line:rgba(0,0,0,.1);--t:#2a2e36;--m:#646b78;--tx:#14161a;--inp:#fff;--soft:rgba(0,0,0,.05);--hi:#14161a;color-scheme:light}
:root[data-theme=light] .orb{opacity:.5}
:root[data-theme=light] .o1{background:#9db0ff}:root[data-theme=light] .o2{background:#c4b5fd}:root[data-theme=light] .o3{background:#7dd3fc}
:root[data-theme=light] .main.empty{background:radial-gradient(ellipse 60% 50% at 50% 62%,rgba(88,101,242,.22) 0%,rgba(244,246,251,0) 70%),var(--bg)}
:root[data-theme=light] .eq i{background:linear-gradient(to top,rgba(88,101,242,.6),rgba(56,189,248,.1))}
:root[data-theme=light] .main.empty .box{box-shadow:0 10px 30px rgba(88,101,242,.16)}
:root[data-theme=light] .m.bot{box-shadow:0 2px 10px rgba(30,40,90,.06)}
:root[data-theme=light] .hero .logo{background:#eef0ff}
*{box-sizing:border-box;margin:0}
html,body{height:100%}
body{background:var(--bg);color:var(--t);font-family:system-ui,-apple-system,"Segoe UI",Roboto,sans-serif;display:flex;overflow:hidden}
button{font-family:inherit;cursor:pointer}
svg{flex-shrink:0}
.logo{position:relative;width:28px;height:28px;border-radius:8px;overflow:hidden;display:inline-grid;place-items:center;color:#fff;font-size:.9rem;flex-shrink:0}
.logo b{display:none}
.logo.fb{background:linear-gradient(135deg,#a855f7,#1fb6ff)}
.logo.fb b{display:block}
.logo img{position:absolute;inset:0;width:100%;height:100%;object-fit:contain}
.brand .logo{width:30px;height:30px;border-radius:0}
.side{width:280px;background:var(--side);border-right:1px solid var(--line);display:flex;flex-direction:column;padding:14px;gap:10px;flex-shrink:0;animation:sideIn .7s cubic-bezier(.2,.7,.2,1) backwards}
.brand{font-weight:700;font-size:1.1rem;color:var(--tx);display:flex;gap:10px;align-items:center;padding:4px 6px}
.new{background:var(--p);color:#fff;border:0;border-radius:12px;padding:11px;font-weight:600;display:flex;gap:8px;align-items:center;justify-content:center;transition:filter .15s}
.new:hover{filter:brightness(1.12)}
.list{flex:1;overflow-y:auto;display:flex;flex-direction:column;gap:4px}
.grp{font-size:.7rem;color:var(--m);padding:10px 12px 2px;letter-spacing:.02em}
.conv{display:flex;align-items:center;gap:8px;padding:10px 12px;border-radius:10px;color:var(--m);cursor:pointer;font-size:.9rem}
.conv svg{opacity:.8}
.conv span{flex:1;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.conv:hover,.conv.on{background:rgba(88,101,242,.15);color:var(--tx)}
.conv em{font-style:normal;font-size:.62rem;border:1px solid var(--line);border-radius:6px;padding:1px 5px;opacity:.75;flex-shrink:0}
.conv button{background:none;border:0;color:var(--m);opacity:0;display:flex}
.conv:hover button{opacity:1}.conv button:hover{color:#ed4245}
.ai{display:flex;align-items:center;gap:8px;font-size:.78rem;color:var(--m);padding:2px 6px;min-height:20px}
.ai i{width:8px;height:8px;border-radius:50%;background:#80848e;flex-shrink:0}
.ai span{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.me{border-top:1px solid var(--line);padding-top:12px;display:flex;align-items:center;gap:8px}
.me img{width:36px;height:36px;border-radius:50%;object-fit:cover;flex-shrink:0}
.me div{flex:1;min-width:0;font-size:.88rem;color:var(--tx);font-weight:600;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.ib{background:none;border:0;color:var(--m);padding:6px;display:flex;align-items:center;border-radius:8px;position:relative}
.ib:hover{color:var(--tx)}
.ib.on{color:var(--p)}
.ib.dot::after{content:'';position:absolute;top:4px;right:3px;width:8px;height:8px;border-radius:50%;background:#ed4245;box-shadow:0 0 0 2px var(--bg)}
.main{flex:1;display:flex;flex-direction:column;min-width:0;height:100%;position:relative;overflow:hidden;animation:appIn .9s cubic-bezier(.2,.7,.2,1) both}
.main>*{position:relative;z-index:1}
.main.empty{background:radial-gradient(ellipse 60% 50% at 50% 62%,color-mix(in srgb,var(--c1) 55%,transparent) 0%,rgba(15,17,23,0) 70%),var(--bg)}
.main.wall::before{content:'';position:absolute;inset:0;background:rgba(0,0,0,var(--dim,.2));z-index:0;pointer-events:none}
.main.wgrad{background-size:300% 300%!important;animation:appIn .9s cubic-bezier(.2,.7,.2,1) both,wallShift 22s ease-in-out infinite alternate}
@keyframes wallShift{from{background-position:0% 50%}to{background-position:100% 50%}}
.main.wall .hero h2{color:#fff;text-shadow:0 2px 14px rgba(0,0,0,.5)}
.main.wall .top{background:rgba(0,0,0,.28);backdrop-filter:blur(8px)}
.main.wall .top h1,.main.wall .top .ib{color:#fff}
.top{padding:14px 20px;border-bottom:1px solid var(--line);display:flex;align-items:center;gap:6px;z-index:3}
.main.empty .top{border-bottom-color:transparent}
.top h1{font-size:1rem;flex:1;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;font-weight:600;color:var(--tx);margin-left:6px}
.hero{display:none;flex-direction:column;align-items:center;gap:14px;text-align:center;padding:0 20px 8px}
.hero .logo{width:56px;height:56px;border-radius:16px;font-size:1.6rem;background:#161a33}
.hero .logo img{padding:9px}
.hero h2{font-size:clamp(1.6rem,4vw,2.4rem);font-weight:300;color:var(--hi)}
.hero h2 .cur{display:block;font-size:.5em;line-height:1.45;max-width:640px;margin:8px auto 0;color:var(--m)}
.hero h2 b{font-weight:600}
.main.empty .hero{display:flex;margin-top:auto}
.main.empty .msgs{display:none}
.msgs{flex:1;overflow-y:auto;padding:24px 20px;display:flex;flex-direction:column;gap:14px}
.m{max-width:min(760px,88%);padding:12px 16px;border-radius:18px;line-height:1.55;font-size:.95rem;word-break:break-word;animation:msgIn .35s cubic-bezier(.2,.7,.2,1) both}
.m .tx{white-space:pre-wrap}
.m.bot{align-self:flex-start;background:var(--card);border:1px solid var(--line);border-top-left-radius:4px}
.m.user{align-self:flex-end;background:var(--p);color:#fff;border-top-right-radius:4px}
.m.err{align-self:center;background:rgba(237,66,69,.14);border:1px solid rgba(237,66,69,.45);color:var(--t);font-size:.86rem}
.m time{display:block;font-size:.68rem;opacity:.5;margin-top:4px;text-align:right}
.act{display:flex;gap:4px;margin-top:6px;flex-wrap:wrap}
.act button{background:none;border:0;color:var(--m);font-size:.78rem;padding:3px 8px;border-radius:8px;display:flex;gap:6px;align-items:center}
.act button:hover{background:var(--soft);color:var(--tx)}
.act button.on{color:var(--p)}
.notice{display:none;align-items:center;justify-content:center;gap:12px;flex-wrap:wrap;margin:0 20px 8px;padding:10px 14px;border-radius:14px;background:rgba(250,166,26,.12);border:1px solid rgba(250,166,26,.4);font-size:.84rem;color:var(--t);text-align:center}
.in{padding:14px 20px 12px;display:flex;justify-content:center}
.box{width:100%;max-width:760px;display:flex;align-items:flex-end;gap:8px;background:var(--inp);border:1px solid var(--line);border-radius:28px;padding:6px 6px 6px 20px}
.main.empty .box{padding:10px 10px 10px 24px;box-shadow:0 8px 30px rgba(0,0,0,.35)}
.box:focus-within{border-color:var(--p)}
.box textarea{flex:1;background:transparent;border:0;padding:10px 0;color:var(--tx);font:inherit;font-size:.97rem;resize:none;outline:none;min-height:40px;max-height:140px;display:block}
.mdl{background:var(--soft);border:0;color:var(--t);border-radius:18px;padding:0 12px;height:40px;font-size:.82rem;font-weight:600;display:flex;align-items:center;gap:6px;flex-shrink:0;max-width:170px}
.mdl span{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.mdl svg{color:var(--p)}
.send{width:40px;height:40px;border:0;border-radius:50%;background:var(--p);color:#fff;flex-shrink:0;display:grid;place-items:center;transition:filter .15s}
.send:hover{filter:brightness(1.12)}
.modes{display:none;gap:8px;flex-wrap:wrap;justify-content:center;align-items:center;padding:0 20px;font-size:.8rem;color:var(--m)}
.main.empty .modes{display:flex;margin-bottom:auto;padding-bottom:20px;max-width:900px;align-self:center}
.modes button{background:var(--soft);border:1px solid var(--line);color:var(--t);padding:7px 14px;border-radius:999px;font-size:.8rem;display:inline-flex;gap:6px;align-items:center;transition:background .15s,color .15s}
.modes button:hover{background:rgba(88,101,242,.3)}
.modes button.on{background:var(--p);border-color:var(--p);color:#fff}
.modes .sep{width:1px;height:20px;background:var(--line)}
.dots i{display:inline-block;width:7px;height:7px;margin:0 2px;border-radius:50%;background:var(--p);animation:b 1.2s infinite both}
.dots i:nth-child(2){animation-delay:.2s}.dots i:nth-child(3){animation-delay:.4s}
@keyframes b{0%,80%,100%{transform:scale(0)}40%{transform:scale(1)}}
.caret{display:inline-block;width:2px;height:1em;background:var(--p);margin-left:2px;vertical-align:-.12em;animation:blink .8s steps(1) infinite}
@keyframes blink{50%{opacity:0}}
html[data-anim=off] *,html[data-anim=off] *::before,html[data-anim=off] *::after{animation:none!important;transition:none!important}
html[data-fx=off] .main.empty .bg{display:none}
html[data-fx=orbs] .eq{display:none}
html[data-fx=eq] .orb{display:none}
.dd{display:none;position:absolute;right:16px;top:calc(100% - 4px);min-width:220px;flex-direction:column;background:var(--card);border:1px solid var(--line);border-radius:14px;padding:6px;box-shadow:0 12px 40px rgba(0,0,0,.35);animation:rise .2s both;z-index:6}
.dd.open{display:flex}
.dd button{display:flex;gap:10px;align-items:center;background:none;border:0;color:var(--t);padding:9px 12px;border-radius:9px;text-align:left;font-size:.9rem}
.dd button:hover{background:var(--soft)}.dd .dng{color:#ed4245}.dd hr{border:0;border-top:1px solid var(--line);margin:4px 0}
.modal{position:fixed;inset:0;background:rgba(0,0,0,.55);display:none;align-items:center;justify-content:center;z-index:20;padding:16px;backdrop-filter:blur(4px)}
.card{background:var(--card);border:1px solid var(--line);border-radius:18px;padding:20px;width:100%;max-width:560px;max-height:92vh;overflow-y:auto;display:flex;flex-direction:column;gap:14px;animation:rise .3s both;box-shadow:0 20px 60px rgba(0,0,0,.35)}
.ch{display:flex;align-items:center;justify-content:space-between;gap:10px}.ch h3{color:var(--tx);font-size:1.1rem}
.chh{display:flex;align-items:center;gap:12px}.chh small{display:block;margin-top:2px}
.chi{width:38px;height:38px;border-radius:12px;display:grid;place-items:center;color:#fff;background:linear-gradient(135deg,var(--c2),var(--c3))}
.card label{font-size:.82rem;color:var(--m);display:flex;flex-direction:column;gap:6px}
.card input[type=password],.card input[type=text],.card select,.card textarea{background:var(--inp);border:1px solid var(--line);border-radius:10px;padding:10px 12px;color:var(--tx);font:inherit;width:100%}
.card textarea{resize:vertical;min-height:70px}
.card input:focus,.card select:focus,.card textarea:focus{outline:none;border-color:var(--p)}
.card input[type=range]{accent-color:var(--p);width:100%;padding:0}
.card .chk2{flex-direction:row;align-items:center;gap:10px;color:var(--t);font-size:.88rem;cursor:pointer}
.chk2 input[type=checkbox]{appearance:none;-webkit-appearance:none;width:38px;height:22px;border-radius:999px;background:var(--soft);border:1px solid var(--line);position:relative;flex-shrink:0;cursor:pointer;transition:background .2s}
.chk2 input[type=checkbox]::after{content:'';position:absolute;top:2px;left:2px;width:16px;height:16px;border-radius:50%;background:var(--m);transition:transform .2s,background .2s}
.chk2 input[type=checkbox]:checked{background:var(--p);border-color:var(--p)}
.chk2 input[type=checkbox]:checked::after{transform:translateX(16px);background:#fff}
.card small{color:var(--m);line-height:1.45}
.pane{display:flex;flex-direction:column;gap:12px}
.pane[hidden]{display:none}
.row{display:flex;gap:8px;justify-content:flex-end;flex-wrap:wrap}
.btn2{border:0;border-radius:10px;padding:9px 16px;font-weight:600;background:var(--soft);color:var(--tx);display:inline-flex;gap:8px;align-items:center}
.btn2:hover{filter:brightness(1.2)}
.btn2.ok{background:var(--p);color:#fff}
.btn2.dng{color:#ed4245}
.tabs{display:flex;gap:4px;background:var(--soft);padding:4px;border-radius:12px;flex-wrap:wrap}
.tabs button,.seg button{flex:1 1 auto;border:0;background:none;color:var(--m);padding:8px 10px;border-radius:9px;font-weight:600;font-size:.85rem;display:flex;gap:6px;align-items:center;justify-content:center;white-space:nowrap}
.seg{display:flex;gap:6px;flex-wrap:wrap}.seg button{background:var(--soft);color:var(--t)}
.tabs button.on,.seg button.on{background:var(--p);color:#fff}
.walls{display:flex;flex-wrap:wrap;gap:8px}
.sw{width:46px;height:46px;border-radius:12px;border:2px solid var(--line);color:var(--tx);display:grid;place-items:center;background-size:cover}
.sw.on{border-color:var(--p);box-shadow:0 0 0 2px var(--p)}
.topics{display:flex;flex-direction:column;gap:10px}
.mlist{display:flex;flex-wrap:wrap;gap:6px;max-height:140px;overflow-y:auto}
.mlist button{border:1px solid var(--line);background:var(--soft);color:var(--t);border-radius:999px;padding:5px 10px;font-size:.78rem}
.mlist button.on{background:var(--p);color:#fff;border-color:var(--p)}
.sec{font-size:.78rem;font-weight:700;color:var(--m);text-transform:uppercase;letter-spacing:.08em;display:flex;gap:8px;align-items:center}
.acct{display:flex;align-items:center;gap:14px}
.acct img{width:54px;height:54px;border-radius:50%;object-fit:cover;background:var(--soft)}
.acct b{color:var(--tx);font-size:1.02rem}
.nl{list-style:none;display:flex;flex-direction:column;gap:8px;padding:0}
.nl li{background:var(--soft);border:1px solid var(--line);border-radius:12px;padding:11px 14px;line-height:1.5;color:var(--t);font-size:.9rem;animation:rise .5s cubic-bezier(.2,.7,.2,1) both;animation-delay:calc(var(--i)*70ms + .05s)}
.nl b{color:var(--tx)}
.pop{position:fixed;display:none;z-index:25;width:330px;max-height:min(60vh,480px);overflow-y:auto;padding:10px;background:color-mix(in srgb,var(--card) 86%,transparent);backdrop-filter:blur(20px) saturate(150%);border:1px solid var(--line);border-radius:24px;box-shadow:0 26px 80px rgba(0,0,0,.6),inset 0 1px 0 rgba(255,255,255,.06)}
.pop.on{display:block;animation:popIn .24s cubic-bezier(.2,.9,.3,1.15)}
@keyframes popIn{from{opacity:0;transform:translateY(12px) scale(.95)}}
.ph{padding:6px 8px 10px;color:var(--tx);font-size:1rem}
.pn{margin:6px 10px;font-size:.8rem;color:var(--m);line-height:1.5}
.it{display:flex;align-items:center;gap:12px;width:100%;border:1px solid transparent;background:none;color:var(--tx);text-align:left;padding:9px 10px;border-radius:16px;font-size:.86rem;transition:background .15s,transform .15s}
.it:hover{background:var(--soft);transform:translateX(3px)}
.it.sel{border-color:color-mix(in srgb,var(--p) 70%,transparent);background:color-mix(in srgb,var(--p) 14%,transparent)}
.tile{width:34px;height:34px;border-radius:11px;display:grid;place-items:center;color:#fff;flex-shrink:0;background:linear-gradient(135deg,var(--c2),var(--c3))}
.toast{position:fixed;bottom:24px;left:50%;transform:translateX(-50%);background:var(--card);color:var(--tx);border:1px solid var(--line);padding:10px 16px;border-radius:12px;z-index:30;opacity:0;transition:.25s;pointer-events:none}.toast.on{opacity:1}
.bg{display:none;position:absolute!important;inset:0;z-index:0!important;pointer-events:none;overflow:hidden}
.main.empty .bg{display:block}
.orb{position:absolute;border-radius:50%;filter:blur(70px);opacity:.55;animation:drift 14s ease-in-out infinite alternate;will-change:transform}
.orb.o1{width:420px;height:420px;background:var(--c1);left:12%;top:18%}
.orb.o2{width:340px;height:340px;background:var(--c2);right:10%;top:34%;animation-duration:18s;animation-delay:-6s}
.orb.o3{width:260px;height:260px;background:var(--c3);left:42%;bottom:6%;opacity:.3;animation-duration:22s;animation-delay:-3s}
@keyframes drift{from{transform:translate(0,0) scale(1)}to{transform:translate(60px,-40px) scale(1.18)}}
.eq{position:absolute;left:0;right:0;bottom:0;height:48%;display:flex;align-items:flex-end;justify-content:center;gap:clamp(4px,.9vw,10px);padding:0 4%;-webkit-mask-image:linear-gradient(to top,#000 0%,transparent 95%);mask-image:linear-gradient(to top,#000 0%,transparent 95%)}
.eq i{flex:1;max-width:18px;min-width:3px;height:100%;border-radius:999px 999px 0 0;background:linear-gradient(to top,color-mix(in srgb,var(--c2) 75%,transparent),color-mix(in srgb,var(--c3) 12%,transparent));transform-origin:bottom;transform:scaleY(.08);animation:bar var(--d) ease-in-out var(--l) infinite alternate,barIn 1.2s cubic-bezier(.2,.7,.2,1) var(--e) backwards}
@keyframes bar{from{transform:scaleY(.08)}to{transform:scaleY(var(--h))}}
@keyframes barIn{from{opacity:0;transform:scaleY(0)}}
.main.empty .hero .logo{animation:pop .9s cubic-bezier(.2,1.2,.3,1) .25s both,float 5s ease-in-out 1.2s infinite alternate;box-shadow:0 0 40px color-mix(in srgb,var(--c2) 60%,transparent)}
.main.empty .hero h2{animation:rise .8s cubic-bezier(.2,.7,.2,1) .45s both}
.main.empty .box{animation:rise .8s cubic-bezier(.2,.7,.2,1) .65s both}
.main.empty .modes{animation:rise .6s cubic-bezier(.2,.7,.2,1) .9s both}
@keyframes rise{from{opacity:0;transform:translateY(22px)}to{opacity:1;transform:none}}
@keyframes pop{from{opacity:0;transform:scale(.4) rotate(-12deg)}to{opacity:1;transform:none}}
@keyframes float{to{transform:translateY(-8px)}}
@keyframes msgIn{from{opacity:0;transform:translateY(10px)}to{opacity:1;transform:none}}
@keyframes appIn{from{opacity:0;transform:scale(.985)}to{opacity:1;transform:none}}
@keyframes sideIn{from{opacity:0;transform:translateX(-24px)}to{opacity:1;transform:none}}
*{scrollbar-width:thin;scrollbar-color:color-mix(in srgb,var(--m) 40%,transparent) transparent}
@media(min-width:761px){.side{transition:margin-left .38s cubic-bezier(.2,.7,.2,1),opacity .3s}body.side-off .side{margin-left:-280px;opacity:0;pointer-events:none}}
@media(max-width:760px){.side{position:fixed;inset:0 auto 0 0;z-index:5;transform:translateX(-100%);transition:.25s}.side.open{transform:none}.box textarea{font-size:16px}.mdl span{display:none}}
@media(prefers-reduced-motion:reduce){*,*::before,*::after{animation-duration:.01ms!important;animation-delay:0s!important;animation-iteration-count:1!important}}
.sup{display:flex;align-items:center;justify-content:center;gap:8px;background:none;border:1px dashed var(--line);color:var(--m);border-radius:12px;padding:9px;font-size:.82rem;font-weight:600;transition:color .15s,border-color .15s,background .15s}
.sup:hover{color:var(--tx);border-color:var(--p);background:rgba(88,101,242,.1)}
.qrbox{background:#fff;border-radius:18px;padding:14px;width:230px;height:230px;margin:0 auto;box-shadow:0 10px 30px rgba(0,0,0,.35)}
.qrbox svg{width:100%;height:100%;display:block}
.card.sm{max-width:380px;text-align:center}
.card.sm .row{justify-content:center}
.card.sm h3{font-size:1.2rem}
.card.sm p{color:var(--t);line-height:1.5;font-size:.9rem}
.card.sm .lnk{font-size:.8rem;color:var(--m);word-break:break-all;user-select:all}
/* ===== menu de configurações (igual ao do site) ===== */
#cfg .card{display:grid;grid-template-columns:216px minmax(0,1fr);grid-template-rows:auto 1fr auto;gap:0;padding:0;max-width:900px;height:min(86vh,690px);max-height:none;overflow:hidden;border-radius:28px;background:color-mix(in srgb,var(--card) 92%,transparent);backdrop-filter:blur(24px) saturate(150%);box-shadow:0 30px 90px rgba(0,0,0,.6),inset 0 1px 0 rgba(255,255,255,.05)}
#cfg .ch{grid-column:1/-1;padding:18px 22px;border-bottom:1px solid var(--line);background:linear-gradient(90deg,color-mix(in srgb,var(--p) 20%,transparent),transparent 70%)}
#cfg .chh{gap:14px}#cfg .chh h3{font-size:1.15rem}#cfg .chh small{font-size:.78rem}
#cfg .chi{width:42px;height:42px;border-radius:14px;background:linear-gradient(135deg,var(--p),#1fb6ff);box-shadow:0 8px 22px color-mix(in srgb,var(--p) 50%,transparent)}
#cfg .tabs{grid-column:1;grid-row:2/4;flex-direction:column;flex-wrap:nowrap;align-content:flex-start;gap:4px;padding:14px 10px;border-radius:0;border-right:1px solid var(--line);background:color-mix(in srgb,var(--soft) 55%,transparent);overflow-y:auto}
#cfg .tabs button{flex:none;justify-content:flex-start;gap:12px;text-align:left;padding:11px 14px;border-radius:14px;font-size:.92rem;transition:background .15s,color .15s,transform .15s}
#cfg .tabs button:hover{background:var(--soft);color:var(--tx);transform:translateX(2px)}
#cfg .tabs button.on{background:linear-gradient(135deg,var(--p),color-mix(in srgb,var(--p) 55%,#1fb6ff));color:#fff;box-shadow:0 8px 22px color-mix(in srgb,var(--p) 45%,transparent);transform:none}
#cfg .card>.pane{grid-column:2;grid-row:2;overflow-y:auto;padding:20px 24px 24px;gap:14px;align-content:flex-start}
#cfg .card>.pane:not([hidden]){animation:paneIn .3s cubic-bezier(.2,.8,.3,1)}
@keyframes paneIn{from{opacity:0;transform:translateX(16px)}}
#cfg .card>.row{grid-column:2;grid-row:3;padding:14px 24px;border-top:1px solid var(--line);background:color-mix(in srgb,var(--soft) 45%,transparent)}
#cfg .sec{font-size:.7rem;letter-spacing:.1em;color:var(--p)}
#cfg .sec:not(:first-child){margin-top:8px;padding-top:16px;border-top:1px solid var(--line)}
#cfg .card input[type=text],#cfg .card input[type=password],#cfg .card select,#cfg .card textarea{border-radius:12px;padding:11px 14px;transition:border-color .15s,box-shadow .15s}
#cfg .card input:focus,#cfg .card select:focus,#cfg .card textarea:focus{box-shadow:0 0 0 3px color-mix(in srgb,var(--p) 25%,transparent)}
#cfg .card>.pane>.chk2{flex-direction:row-reverse;justify-content:space-between;padding:12px 14px;background:var(--soft);border-radius:14px;color:var(--tx);transition:background .15s}
#cfg .card>.pane>.chk2:hover{background:color-mix(in srgb,var(--p) 12%,var(--soft))}
#cfg .chk2 input[type=checkbox]{width:42px;height:24px;border:0;background:color-mix(in srgb,var(--m) 35%,transparent)}
#cfg .chk2 input[type=checkbox]::after{top:3px;left:3px;width:18px;height:18px;background:#fff;box-shadow:0 2px 6px rgba(0,0,0,.35);transition:transform .22s cubic-bezier(.3,1.4,.5,1)}
#cfg .chk2 input[type=checkbox]:checked{background:var(--p)}
#cfg .chk2 input[type=checkbox]:checked::after{transform:translateX(18px)}
#cfg .topics{gap:8px}
#cfg .topics .chk2{flex-direction:row-reverse;justify-content:space-between;padding:12px 14px;background:var(--soft);border-radius:14px}
#cfg .btn2{border-radius:12px;transition:background .15s,transform .15s}#cfg .btn2:hover{transform:translateY(-1px)}
@media(max-width:760px){
#cfg .card{grid-template-columns:1fr;grid-template-rows:auto auto 1fr auto;height:92vh;border-radius:24px}
#cfg .tabs{grid-column:1;grid-row:2;flex-direction:row;overflow-x:auto;overflow-y:hidden;border-right:0;border-bottom:1px solid var(--line);padding:8px}
#cfg .tabs button{white-space:nowrap;padding:9px 13px}
#cfg .card>.pane{grid-column:1;grid-row:3;padding:16px}#cfg .card>.row{grid-column:1;grid-row:4;padding:12px 16px}}
@media(prefers-reduced-motion:reduce){#cfg .card>.pane:not([hidden]){animation:none}}
.mds{display:flex;flex-direction:column;gap:10px}
.mdc{display:flex;gap:12px;align-items:center;background:var(--soft);border:1px solid var(--line);border-radius:16px;padding:12px 14px;transition:border-color .15s,box-shadow .15s}
.mdc.on{border-color:var(--p);box-shadow:0 0 0 1px var(--p)}
.mdc .inf{flex:1;min-width:0}
.mdc b{color:var(--tx);display:flex;gap:8px;align-items:center;flex-wrap:wrap;font-size:.95rem}
.mdc small{display:block;margin-top:3px;line-height:1.4}
.tag{font-size:.64rem;font-weight:700;padding:2px 8px;border-radius:999px;background:color-mix(in srgb,var(--p) 28%,transparent);color:var(--tx)}
.tag.ok{background:rgba(59,165,93,.25)}
.mdc .bt{display:flex;gap:6px;flex-shrink:0;flex-wrap:wrap;justify-content:flex-end}
.mdc .btn2{padding:8px 12px;font-size:.82rem}
.pb{height:6px;border-radius:99px;background:var(--line);overflow:hidden;margin-top:8px}
.pb i{display:block;height:100%;width:0;background:linear-gradient(90deg,var(--c2),var(--c3));transition:width .3s}
.eng{display:flex;gap:10px;align-items:center;background:color-mix(in srgb,var(--p) 12%,transparent);border:1px solid color-mix(in srgb,var(--p) 40%,transparent);border-radius:14px;padding:11px 14px;font-size:.86rem;color:var(--tx);line-height:1.45}
.eng.err{background:rgba(237,66,69,.12);border-color:rgba(237,66,69,.45)}
.eng.ok{background:rgba(59,165,93,.12);border-color:rgba(59,165,93,.45)}
.m.help{align-self:center;width:min(520px,92%);max-width:none;background:var(--card);border:1px solid color-mix(in srgb,var(--p) 45%,transparent);display:flex;flex-direction:column;gap:10px;box-shadow:0 10px 30px rgba(0,0,0,.18)}
.m.help .nmt{color:var(--tx);font-size:1.02rem}
.m.help small{color:var(--m);line-height:1.45}
.m.help .eng{font-size:.82rem}
.nmo{display:flex;gap:12px;align-items:center;width:100%;text-align:left;background:var(--soft);border:1px solid var(--line);border-radius:14px;padding:11px 13px;color:var(--tx);font:inherit;transition:border-color .15s,transform .15s}
.nmo:hover:not(:disabled){border-color:var(--p);transform:translateY(-1px)}
.nmo:disabled{opacity:.5;cursor:default}
.nmo b{display:block;font-size:.9rem;color:var(--tx)}
.nmo small{display:block;margin-top:2px;font-size:.78rem}
.nmr{display:flex;gap:8px;justify-content:center}
.btn2.ok.danger{background:#ed4245}
#askM .card input{text-align:left}
#askM .card p:empty{display:none}
@media(max-width:760px){.mdc{flex-direction:column;align-items:stretch}.mdc .bt{justify-content:flex-start}}
</style></head><body>
<aside class="side" id="side">
 <div class="brand"><span class="logo"><b>A</b><img src="data:image/png;base64,@LOGO@" alt="" onerror="this.remove();this.parentNode.classList.add('fb')"></span> Azul Groove</div>
 <button class="new" id="newBtn"></button>
 <div class="list" id="list"></div>
 <div class="ai" id="aiInfo"></div>
 <button class="sup" id="supBtn"></button>
 <div class="me"><img id="av" alt="" style="display:none" onerror="this.style.display='none'"><div id="nm">Offline</div><button class="ib" id="backBtn" title="Voltar ao painel"></button></div>
</aside>
<main class="main empty" id="main">
 <div class="bg" aria-hidden="true"><div class="orb o1"></div><div class="orb o2"></div><div class="orb o3"></div><div class="eq" id="eq"></div></div>
 <div class="top">
  <button class="ib" id="menuBtn" title="Barra lateral"></button>
  <h1 id="title">Nova conversa</h1>
  <button class="ib" id="themeBtn" title="Modo claro/escuro"></button>
  <button class="ib" id="deepBtn" title="Modo aprofundado"></button>
  <button class="ib" id="newsBtn" title="Novidades"></button>
  <button class="ib" id="cfgBtn" title="Configurações"></button>
  <button class="ib" id="moreBtn" title="Menu da conversa"></button>
  <div class="dd" id="dd">
   <button data-a="rename"></button><button data-a="export"></button><button data-a="copyconv"></button><hr>
   <button data-a="clear"></button><button class="dng" data-a="delcur"></button><button class="dng" data-a="delall"></button>
  </div>
 </div>
 <div class="hero">
  <span class="logo"><b>A</b><img src="data:image/png;base64,@LOGO@" alt="" onerror="this.remove();this.parentNode.classList.add('fb')"></span>
  <h2 id="hello">Olá! Pergunte o que quiser.</h2>
 </div>
 <div class="msgs" id="msgs"></div>
 <div class="notice" id="notice"><span id="nTxt"></span><button class="btn2 ok" id="nBtn">Configurar</button></div>
 <div class="in"><div class="box">
  <textarea id="inp" rows="1" placeholder="Pergunte ao Azul Groove..."></textarea>
  <button class="mdl" id="mdlBtn" title="Escolher o modelo"><span id="mdlName">Auto</span></button>
  <button class="send" id="send" title="Enviar"></button>
 </div></div>
 <div class="modes" id="modes"></div>
</main>
<div class="pop" id="mdlPop"></div>
<div class="modal" id="cfg"><div class="card">
 <div class="ch"><div class="chh"><span class="chi" id="cfgIco"></span><div><h3>Configurações</h3><small>Ajuste a IA, o visual e os seus dados</small></div></div><button class="ib" id="cfgX" title="Fechar"></button></div>
 <div class="tabs" id="tabs"></div>
<div class="pane" id="p_md">
  <small>Rode a IA do Azul Groove direto no app, sem instalar mais nada. Depois de baixar um modelo, ela funciona sem internet.</small>
  <div class="eng" id="engBox"></div>
  <div class="sec" id="secMd"></div>
  <div class="mds" id="mdList"></div>
  <small id="mdRam"></small>
  <div class="sec" id="secAdd"></div>
  <div class="row" style="justify-content:flex-start;flex-wrap:nowrap"><input type="text" id="mdUrl" placeholder="Link de um modelo .gguf (https://huggingface.co/...)" autocomplete="off" style="flex:1;background:var(--inp);border:1px solid var(--line);border-radius:12px;padding:11px 14px;color:var(--tx);font:inherit"><button class="btn2 ok" id="mdUrlBtn"></button></div>
  <div class="row" style="justify-content:flex-start"><button class="btn2" id="mdImpBtn"></button></div>
  <div class="sec" id="secPerf"></div>
  <label class="chk2"><input type="checkbox" id="mdVk"> Usar a placa de vídeo (Vulkan)</label>
  <small>Deixa as respostas bem mais rápidas em PCs com placa de vídeo. Se travar ou der erro, desligue: o processador funciona em qualquer PC.</small>
 </div>
 <div class="pane" id="p_ia" hidden>
  <small>Funciona com qualquer app de IA compatível com o protocolo OpenAI: Ollama, LM Studio, Jan, llama.cpp, KoboldCpp, GPT4All… Tudo fica só neste PC.</small>
  <small id="siteNote"></small>
  <div class="sec" id="secDet"></div>
  <div class="mlist" id="det"></div>
  <div class="sec" id="secCon"></div>
  <label>Provedor<select id="cfgProv"></select></label>
  <label>URL base da API<input type="text" id="cfgBase" placeholder="http://127.0.0.1:11434/v1" autocomplete="off"></label>
  <label>Chave da API (vazio para apps locais)<input type="password" id="cfgKey" autocomplete="off"></label>
  <label class="chk2" id="clrRow" style="display:none"><input type="checkbox" id="cfgClr"> Remover a chave salva</label>
  <label>Modelo<input type="text" id="cfgModel" placeholder="ex.: llama3.2" autocomplete="off"></label>
  <div class="row" style="justify-content:flex-start"><button class="btn2" id="cfgFetch">Buscar modelos</button></div>
  <div class="mlist" id="cfgModels"></div>
  <div class="sec" id="secSite"></div>
  <label class="chk2"><input type="checkbox" id="cfgSite"> Usar a IA do site quando houver internet</label>
  <small id="siteAiNote"></small>
  <small id="cfgMsg"></small>
 </div>
 <div class="pane" id="p_ap" hidden>
  <label>Tema</label>
  <div class="seg" id="thSeg"></div>
  <small>"Automático" segue o tema do Windows.</small>
  <label>Fundo do chat</label>
  <div class="walls" id="walls"></div>
  <div class="row" style="justify-content:flex-start"><button class="btn2" id="wallBtn"></button><input type="file" id="wallFile" accept="image/*" hidden></div>
  <label>Escurecer o fundo<input type="range" id="lookDim" min="0" max="80"></label>
  <label>Efeitos da tela inicial</label>
  <div class="seg" id="fxSeg"></div>
  <label class="chk2"><input type="checkbox" id="lookColors"> Cores do fundo animadas (muda de azul para roxo e rosa aos poucos)</label>
  <label class="chk2"><input type="checkbox" id="lookAnim"> Animações da interface</label>
  <div class="row" style="justify-content:flex-start"><button class="btn2 dng" id="resetBtn"></button></div>
  <small>Volta tema, fundo, escurecimento, efeitos e animações ao original.</small>
 </div>
 <div class="pane" id="p_es" hidden>
  <small>Tudo aqui vale na hora e fica só neste PC.</small>
  <label>Tom das respostas</label>
  <div class="seg" id="toneSeg"></div>
  <label>Dados do Azul Groove</label>
  <div class="seg" id="kbSeg"></div>
  <small>Desligado: a IA vira um assistente geral, sem falar do bot Azul Groove, a menos que você peça.</small>
  <label class="chk2"><input type="checkbox" id="prefDeep"> Modo aprofundado (respostas mais completas e organizadas)</label>
  <small>Respostas mais longas e detalhadas. Fica mais lento. Em cada resposta também há o botão "Aprofundar".</small>
  <label>Curiosidades da tela inicial</label>
  <div class="topics" id="topicBox"></div>
  <label>Seu jeito de falar (instruções para a IA)<textarea id="prefStyle" maxlength="400" placeholder="Ex.: respostas curtas, sem gírias, sempre com um exemplo"></textarea></label>
  <label>Memória: o que a IA deve lembrar</label>
  <div class="row" style="justify-content:flex-start;flex-wrap:nowrap"><input type="text" id="memIn" maxlength="200" placeholder="Ex.: me chame de Léo; gosto de samba" autocomplete="off" style="flex:1;background:var(--inp);border:1px solid var(--line);border-radius:10px;padding:10px 12px;color:var(--tx);font:inherit"><button class="btn2 ok" id="memAddBtn">Adicionar</button></div>
  <div class="mlist" id="memList"></div>
  <label class="chk2"><input type="checkbox" id="prefLearn"> Aprender com 👍 nas respostas</label>
  <div class="row" style="justify-content:flex-start"><button class="btn2 dng" id="likeBtn"></button></div>
 </div>
 <div class="pane" id="p_da" hidden>
  <button class="btn2" id="expAllBtn"></button>
  <button class="btn2 dng" id="delAllBtn"></button>
  <button class="btn2 dng" id="clrCfgBtn"></button>
 </div>
 <div class="pane" id="p_co" hidden>
  <div class="acct"><img id="coAv" alt="" style="display:none"><div><b id="coNm"></b><br><small id="coPr"></small></div></div>
  <small>Seu nome, sua foto, o tema e as preferências são copiados do chat do site quando você abre o site com internet. Para entrar com outra conta ou sair, use o chat do site.</small>
 </div>
 <div class="row"><button class="btn2" id="cfgCancel">Fechar</button><button class="btn2 ok" id="cfgSave">Salvar IA</button></div>
</div></div>
<div class="modal" id="news"><div class="card">
 <div class="ch"><div class="chh"><span class="chi" id="newsIco"></span><div><h3>Novidades</h3><small>Tudo o que mudou no chat do app</small></div></div><button class="ib" id="newsX" title="Fechar"></button></div>
 <ul class="nl" id="newsList"></ul>
 <div class="row"><button class="btn2 ok" id="newsOk">Entendi</button></div>
</div></div>
<div class="modal" id="supM"><div class="card sm">
 <h3>💙 Apoiar o projeto</h3>
 <div class="qrbox" id="qr"><svg viewBox="0 0 25 25" xmlns="http://www.w3.org/2000/svg" shape-rendering="crispEdges"><path fill="#000" d="M0 0h7v1h-7zM8 0h4v1h-4zM16 0h1v1h-1zM18 0h7v1h-7zM0 1h1v1h-1zM6 1h1v1h-1zM9 1h1v1h-1zM11 1h3v1h-3zM16 1h1v1h-1zM18 1h1v1h-1zM24 1h1v1h-1zM0 2h1v1h-1zM2 2h3v1h-3zM6 2h1v1h-1zM8 2h1v1h-1zM10 2h1v1h-1zM13 2h3v1h-3zM18 2h1v1h-1zM20 2h3v1h-3zM24 2h1v1h-1zM0 3h1v1h-1zM2 3h3v1h-3zM6 3h1v1h-1zM8 3h1v1h-1zM10 3h1v1h-1zM12 3h2v1h-2zM15 3h1v1h-1zM18 3h1v1h-1zM20 3h3v1h-3zM24 3h1v1h-1zM0 4h1v1h-1zM2 4h3v1h-3zM6 4h1v1h-1zM8 4h1v1h-1zM12 4h5v1h-5zM18 4h1v1h-1zM20 4h3v1h-3zM24 4h1v1h-1zM0 5h1v1h-1zM6 5h1v1h-1zM12 5h1v1h-1zM14 5h1v1h-1zM16 5h1v1h-1zM18 5h1v1h-1zM24 5h1v1h-1zM0 6h7v1h-7zM8 6h1v1h-1zM10 6h1v1h-1zM12 6h1v1h-1zM14 6h1v1h-1zM16 6h1v1h-1zM18 6h7v1h-7zM10 7h1v1h-1zM12 7h1v1h-1zM15 7h2v1h-2zM0 8h4v1h-4zM6 8h1v1h-1zM8 8h1v1h-1zM10 8h1v1h-1zM12 8h1v1h-1zM14 8h4v1h-4zM20 8h3v1h-3zM24 8h1v1h-1zM0 9h5v1h-5zM7 9h2v1h-2zM11 9h1v1h-1zM19 9h1v1h-1zM23 9h1v1h-1zM1 10h1v1h-1zM5 10h7v1h-7zM14 10h1v1h-1zM17 10h2v1h-2zM20 10h1v1h-1zM2 11h2v1h-2zM7 11h1v1h-1zM9 11h2v1h-2zM12 11h3v1h-3zM16 11h2v1h-2zM20 11h3v1h-3zM0 12h1v1h-1zM2 12h1v1h-1zM4 12h3v1h-3zM9 12h2v1h-2zM13 12h2v1h-2zM16 12h3v1h-3zM20 12h1v1h-1zM22 12h3v1h-3zM3 13h3v1h-3zM8 13h2v1h-2zM12 13h1v1h-1zM15 13h2v1h-2zM18 13h3v1h-3zM24 13h1v1h-1zM1 14h1v1h-1zM3 14h1v1h-1zM6 14h1v1h-1zM10 14h1v1h-1zM17 14h1v1h-1zM20 14h1v1h-1zM22 14h2v1h-2zM0 15h1v1h-1zM3 15h3v1h-3zM9 15h1v1h-1zM11 15h1v1h-1zM14 15h2v1h-2zM19 15h2v1h-2zM24 15h1v1h-1zM0 16h5v1h-5zM6 16h1v1h-1zM9 16h1v1h-1zM11 16h1v1h-1zM13 16h1v1h-1zM15 16h10v1h-10zM8 17h2v1h-2zM11 17h4v1h-4zM16 17h1v1h-1zM20 17h1v1h-1zM22 17h1v1h-1zM24 17h1v1h-1zM0 18h7v1h-7zM9 18h2v1h-2zM14 18h1v1h-1zM16 18h1v1h-1zM18 18h1v1h-1zM20 18h1v1h-1zM22 18h3v1h-3zM0 19h1v1h-1zM6 19h1v1h-1zM9 19h1v1h-1zM12 19h1v1h-1zM14 19h3v1h-3zM20 19h1v1h-1zM24 19h1v1h-1zM0 20h1v1h-1zM2 20h3v1h-3zM6 20h1v1h-1zM9 20h3v1h-3zM14 20h1v1h-1zM16 20h6v1h-6zM23 20h2v1h-2zM0 21h1v1h-1zM2 21h3v1h-3zM6 21h1v1h-1zM8 21h1v1h-1zM11 21h1v1h-1zM13 21h1v1h-1zM15 21h1v1h-1zM17 21h2v1h-2zM20 21h5v1h-5zM0 22h1v1h-1zM2 22h3v1h-3zM6 22h1v1h-1zM8 22h2v1h-2zM11 22h2v1h-2zM17 22h2v1h-2zM20 22h1v1h-1zM22 22h2v1h-2zM0 23h1v1h-1zM6 23h1v1h-1zM8 23h2v1h-2zM11 23h1v1h-1zM13 23h3v1h-3zM17 23h1v1h-1zM20 23h1v1h-1zM22 23h1v1h-1zM0 24h7v1h-7zM8 24h3v1h-3zM13 24h2v1h-2zM17 24h8v1h-8z"/></svg></div>
 <p>Aponte a câmera do celular para o QR Code e faça o apoio pelo LivePix. Ele aparece mesmo com o app sem internet: o apoio é feito pelo seu celular.</p>
 <div class="lnk" id="supUrl">https://livepix.gg/astmsoftware</div>
 <div class="row"><button class="btn2" id="supCopy"></button><button class="btn2 ok" id="supOpen"></button><button class="btn2" id="supClose">Fechar</button></div>
</div></div>
<div class="modal" id="askM"><div class="card sm">
 <h3 id="askT">Confirmar</h3>
 <p id="askP"></p>
 <input type="text" id="askIn" maxlength="80" autocomplete="off" style="display:none">
 <div class="row"><button class="btn2" id="askNo">Cancelar</button><button class="btn2 ok" id="askYes">OK</button></div>
</div></div>
<div class="toast" id="toast"></div>
<script>
var $=function(i){return document.getElementById(i)};
function send(m){try{window.chrome.webview.postMessage(m)}catch(e){}}
var IC={
 plus:'<path d="M12 5v14M5 12h14"/>',
 trash:'<path d="M3 6h18M8 6V4h8v2M19 6l-1 14H6L5 6M10 11v6M14 11v6"/>',
 menu:'<path d="M3 6h18M3 12h18M3 18h18"/>',
 sun:'<circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4"/>',
 moon:'<path d="M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8z"/>',
 sliders:'<path d="M4 21v-7M4 10V3M12 21v-9M12 8V3M20 21v-5M20 12V3M1 14h6M9 8h6M17 16h6"/>',
 gear:'<circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 1 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 1 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 1 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 1 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z"/>',
 layers:'<path d="M12 2L2 7l10 5 10-5z"/><path d="M2 17l10 5 10-5M2 12l10 5 10-5"/>',
 wand:'<path d="M15 4V2M15 16v-2M8 9h2M20 9h2M17.8 11.8L19 13M17.8 6.2L19 5M3 21l9-9M12.2 6.2L11 5"/>',
 chat:'<path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z"/>',
 robot:'<rect x="4" y="8" width="16" height="12" rx="3"/><path d="M12 4v4M9 13h.01M15 13h.01M9 17h6"/>',
 palette:'<circle cx="13.5" cy="6.5" r="1"/><circle cx="17.5" cy="10.5" r="1"/><circle cx="8.5" cy="7.5" r="1"/><circle cx="6.5" cy="12.5" r="1"/><path d="M12 2a10 10 0 0 0 0 20c1.1 0 2-.9 2-2 0-.5-.2-1-.5-1.3-.3-.4-.5-.8-.5-1.3 0-1.1.9-2 2-2h2.4a4 4 0 0 0 4-4C21 6.1 17 2 12 2z"/>',
 db:'<ellipse cx="12" cy="5" rx="9" ry="3"/><path d="M3 5v14c0 1.7 4 3 9 3s9-1.3 9-3V5M3 12c0 1.7 4 3 9 3s9-1.3 9-3"/>',
 user:'<circle cx="12" cy="8" r="4"/><path d="M4 21a8 8 0 0 1 16 0"/>',
 home:'<path d="M3 11l9-8 9 8M5 10v10h14V10"/>',
 image:'<rect x="3" y="3" width="18" height="18" rx="2"/><circle cx="9" cy="9" r="2"/><path d="M21 15l-5-5L5 21"/>',
 rotate:'<path d="M3 12a9 9 0 1 0 3-6.7L3 8M3 3v5h5"/>',
 download:'<path d="M12 3v12M7 10l5 5 5-5M4 21h16"/>',
 ban:'<circle cx="12" cy="12" r="9"/><path d="M5.6 5.6l12.8 12.8"/>',
 thumb:'<path d="M7 11v10H3V11zM7 11l4-8a2 2 0 0 1 2 2v4h6a2 2 0 0 1 2 2.3l-1.2 7A2 2 0 0 1 17.8 21H7"/>',
 key:'<circle cx="8" cy="15" r="4"/><path d="M10.8 12.2L21 2M16 7l3 3"/>',
 send:'<path d="M22 2L11 13M22 2l-7 20-4-9-9-4z"/>',
 stop:'<rect x="6" y="6" width="12" height="12" rx="2"/>',
 chev:'<path d="M6 9l6 6 6-6"/>',
 spark:'<path d="M12 3l1.9 5.1L19 10l-5.1 1.9L12 17l-1.9-5.1L5 10l5.1-1.9z"/>',
 back:'<path d="M15 18l-6-6 6-6"/>',
 x:'<path d="M18 6L6 18M6 6l12 12"/>',
 copy:'<rect x="9" y="9" width="12" height="12" rx="2"/><path d="M5 15V5a2 2 0 0 1 2-2h10"/>',
 pen:'<path d="M12 20h9M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4z"/>',
 file:'<path d="M14 3H6a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V9z"/><path d="M14 3v6h6M8 13h8M8 17h8"/>',
 more:'<circle cx="12" cy="5" r="1.2"/><circle cx="12" cy="12" r="1.2"/><circle cx="12" cy="19" r="1.2"/>',
 qr:'<rect x="3" y="3" width="7" height="7" rx="1"/><rect x="14" y="3" width="7" height="7" rx="1"/><rect x="3" y="14" width="7" height="7" rx="1"/><path d="M14 14h3v3h-3zM20 14v.01M14 20h.01M20 20h.01M17 17v4"/>',
 plug:'<path d="M9 2v6M15 2v6M6 8h12v4a6 6 0 0 1-12 0zM12 18v4"/>',
 link:'<path d="M10 14a5 5 0 0 0 7 0l3-3a5 5 0 0 0-7-7l-1 1M14 10a5 5 0 0 0-7 0l-3 3a5 5 0 0 0 7 7l1-1"/>',
 cpu:'<rect x="6" y="6" width="12" height="12" rx="2"/><path d="M9 2v3M15 2v3M9 19v3M15 19v3M2 9h3M2 15h3M19 9h3M19 15h3"/>'
};
function ic(n,s){s=s||17;return '<svg viewBox="0 0 24 24" width="'+s+'" height="'+s+'" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">'+IC[n]+'</svg>'}
function lbl(n,t){return ic(n)+'<span>'+t+'</span>'}

var TONES={normal:'Equilibrado',serio:'Sério',casual:'Descontraído',humor:'Humor',didatico:'Didático'};
var TOPICS={musica:'🎵 Música',aviacao:'✈️ Aviação',ambiente:'🌱 Meio ambiente',brasil:'🇧🇷 Brasil',geral:'🌍 Gerais'};
var WALLS={none:{n:'Padrão'},aurora:{n:'Aurora',css:'linear-gradient(135deg,#0f2027,#203a43,#2c5364)'},neon:{n:'Neon',css:'linear-gradient(135deg,#12002f,#3a0ca3,#4361ee)'},
 lotus:{n:'Lótus',css:'linear-gradient(135deg,#1e1b4b,#7c3aed,#ec4899)'},sunset:{n:'Pôr do sol',css:'linear-gradient(135deg,#41295a,#c0392b,#f39c12)'},forest:{n:'Floresta',css:'linear-gradient(135deg,#0b3d2e,#1b6b4a,#8fd694)'},
 grafite:{n:'Grafite',css:'#1b1d23'},ceu:{n:'Céu',css:'linear-gradient(135deg,#a1c4fd,#c2e9fb)'},rosa:{n:'Rosa',css:'linear-gradient(135deg,#fbc2eb,#a6c1ee)'}};
var NEWS=[
 '**IA dentro do app:** baixe um modelo em Configurações > Modelos e converse sem internet e sem instalar Ollama ou LM Studio. Você escolhe qual usar e pode baixar outros.',
 '**Visual novo:** o chat do app agora é igual ao do site, com a logo da Azul Groove, fundo com cores que mudam sozinhas, equalizador e esferas animadas.',
 '**Configurações completas:** abas IA, Aparência, Estilo, Dados e Conta, com interruptores no lugar das caixinhas.',
 '**Fundo do chat:** escolha um degradê animado ou use a sua própria imagem, ajuste o escurecimento e os efeitos da tela inicial.',
 '**Modos de resposta:** Equilibrado, Sério, Descontraído, Humor e Didático, direto na tela inicial.',
 '**Dados do Azul Groove e modo aprofundado:** ligue e desligue quando quiser, no topo ou nos botões da tela inicial.',
 '**Jeito de falar, memória e 👍:** diga como a IA deve responder, o que ela deve lembrar e marque as respostas que você gostou.',
 '**Apoiar o projeto com QR Code:** o QR Code aparece mesmo sem internet; é só apontar a câmera do celular. As conversas também ficam separadas por dia (Hoje, Ontem...).'
];

var S={ai:{items:[],active:'',running:'',ramGb:0,vulkan:false,engineReady:false,phase:'idle',text:'',pct:0,dl:'',online:true},convs:[],cur:'',theme:'auto',presets:[],provider:'',base:'',hasKey:false,model:'',models:[],up:null,detected:[],name:'',avatar:'',topics:[],preferSite:false,siteAi:null,curios:null,
 look:{wall:'none',dim:20,fx:'all',anim:true,colors:true},
 prefs:{tone:'normal',kb:'auto',deep:false,style:'',mem:[],learn:true,topics:[],likes:0}};
var busy=false,curMsg=null,CM=[],WALLIMG='',lastQ='',curTab='md';

// ---------- tema e aparência ----------
var mq=window.matchMedia('(prefers-color-scheme: light)');
function effTheme(){return S.theme==='light'||S.theme==='dark'?S.theme:(mq.matches?'light':'dark')}
function applyTheme(){var t=effTheme();document.documentElement.setAttribute('data-theme',t);$('themeBtn').innerHTML=ic(t==='light'?'moon':'sun');paintTheme()}
mq.onchange=applyTheme;
function setTheme(v){S.theme=v;applyTheme();send('lchat:theme:'+v)}
$('themeBtn').onclick=function(){setTheme(effTheme()==='light'?'dark':'light')};
function paintTheme(){
 var o=[['auto','Automático'],['light','Claro'],['dark','Escuro']];
 $('thSeg').innerHTML=o.map(function(x){return '<button data-th="'+x[0]+'" class="'+(S.theme===x[0]?'on':'')+'">'+x[1]+'</button>'}).join('');
}
$('thSeg').onclick=function(e){var b=e.target.closest('button');if(b)setTheme(b.dataset.th)};
function applyLook(){
 var l=S.look,m=$('main'),h=document.documentElement,bg='',w=WALLS[l.wall];
 h.dataset.fx=l.fx;h.dataset.anim=l.anim?'on':'off';h.dataset.colors=(l.colors&&l.anim)?'on':'off';
 if(l.wall==='img'&&WALLIMG)bg='url("'+WALLIMG+'") center/cover no-repeat';
 else if(w&&w.css)bg=w.css;
 m.style.background=bg;m.classList.toggle('wall',!!bg);
 m.classList.toggle('wgrad',!!bg&&bg.indexOf('gradient')>=0);
 m.style.setProperty('--dim',l.dim/100);
 var items=Object.keys(WALLS);if(WALLIMG)items.push('img');
 $('walls').innerHTML=items.map(function(id){var w=WALLS[id]||{n:'Minha imagem'};
  return '<button class="sw '+(l.wall===id?'on':'')+'" data-w="'+id+'" title="'+w.n+'" style="background:'+(w.css||'var(--soft)')+'">'+(id==='none'?ic('ban',18):id==='img'?ic('image',18):'')+'</button>'}).join('');
 $('fxSeg').innerHTML=[['all','Completo'],['orbs','Esferas'],['eq','Equalizador'],['off','Desligado']].map(function(x){return '<button data-f="'+x[0]+'" class="'+(l.fx===x[0]?'on':'')+'">'+x[1]+'</button>'}).join('');
 $('lookAnim').checked=!!l.anim;$('lookColors').checked=!!l.colors;$('lookDim').value=l.dim;
}
function setLook(p){for(var k in p)S.look[k]=p[k];applyLook();send('lchat:look:'+JSON.stringify(S.look))}
$('walls').onclick=function(e){var b=e.target.closest('button');if(b)setLook({wall:b.dataset.w})};
$('fxSeg').onclick=function(e){var b=e.target.closest('button');if(b)setLook({fx:b.dataset.f})};
$('lookDim').oninput=function(){setLook({dim:+this.value})};
$('lookAnim').onchange=function(){setLook({anim:this.checked})};
$('lookColors').onchange=function(){setLook({colors:this.checked})};
$('wallBtn').onclick=function(){$('wallFile').click()};
$('wallFile').onchange=function(){
 var f=this.files[0];this.value='';if(!f)return;
 var fr=new FileReader();
 fr.onload=function(){var im=new Image();im.onerror=function(){toast('Arquivo inválido')};
  im.onload=function(){var s=Math.min(1,1600/Math.max(im.width,im.height)),cv=document.createElement('canvas');
   cv.width=Math.round(im.width*s);cv.height=Math.round(im.height*s);cv.getContext('2d').drawImage(im,0,0,cv.width,cv.height);
   var u=cv.toDataURL('image/jpeg',.8);
   if(u.length>2800000){toast('Imagem grande demais');return}
   WALLIMG=u;send('lchat:wall:'+u);setLook({wall:'img'});toast('🖼️ Fundo aplicado')};
  im.src=fr.result};
 fr.readAsDataURL(f);
};
$('resetBtn').onclick=function(){askBox({title:'Restaurar a aparência?',text:'Tema, fundo, escurecimento, efeitos e animações voltam ao padrão.',ok:'Restaurar'},function(){send('lchat:resetlook')})};

// ---------- preferências (estilo, memória, aprendizado) ----------
function effTopics(){return S.prefs.topics&&S.prefs.topics.length?S.prefs.topics:Object.keys(TOPICS)}
function sendPrefs(){var p=S.prefs;send('lchat:prefs:'+JSON.stringify({tone:p.tone==='normal'?'':p.tone,kb:p.kb,deep:p.deep,style:p.style,mem:p.mem,learn:p.learn,topics:effTopics()}))}
function setPrefs(x){for(var k in x)S.prefs[k]=x[k];paintPrefs();sendPrefs()}
function sg(k,on,label){return '<button data-k="'+k+'" class="'+(on?'on':'')+'">'+label+'</button>'}
function paintPrefs(){
 var p=S.prefs,tb=Object.keys(TONES).map(function(k){return sg(k,p.tone===k,TONES[k])}).join('');
 $('toneSeg').innerHTML=tb;
 $('kbSeg').innerHTML=sg('auto',p.kb!=='off','Ligado (quando fizer sentido)')+sg('off',p.kb==='off','Desligado');
 $('prefDeep').checked=!!p.deep;$('prefLearn').checked=!!p.learn;
 var et=effTopics();
 $('topicBox').innerHTML=Object.keys(TOPICS).map(function(k){return '<label class="chk2"><input type="checkbox" data-t="'+k+'" '+(et.indexOf(k)>=0?'checked':'')+'> '+TOPICS[k]+'</label>'}).join('');
 if(document.activeElement!==$('prefStyle'))$('prefStyle').value=p.style||'';
 $('memList').innerHTML=p.mem.length?p.mem.map(function(t,i){return '<button type="button" title="Clique para remover" data-i="'+i+'">'+esc(t)+' ✕</button>'}).join(''):'<small>Nada por aqui ainda.</small>';
 $('likeBtn').innerHTML=ic('trash',15)+' Limpar aprendizado ('+(p.likes||0)+')';
 $('deepBtn').classList.toggle('on',!!p.deep);$('deepBtn').title='Modo aprofundado: '+(p.deep?'ligado':'desligado');
 paintModes();
}
function paintModes(){
 var p=S.prefs;
 $('modes').innerHTML='<span>Modo:</span>'+Object.keys(TONES).map(function(k){return '<button data-tone="'+k+'" class="'+(p.tone===k?'on':'')+'">'+TONES[k]+'</button>'}).join('')+'<span class="sep"></span>'
  +'<button data-kb class="'+(p.kb==='off'?'':'on')+'" title="Liga ou desliga o assunto do bot Azul Groove nas respostas">Dados do Azul Groove: '+(p.kb==='off'?'desligado':'ligado')+'</button>'
  +'<button data-deep class="'+(p.deep?'on':'')+'" title="Respostas mais completas (mais lento)">'+ic('layers',14)+' Aprofundado: '+(p.deep?'ligado':'desligado')+'</button>'
  +'<button data-news>✨ Novidades</button>';
}
$('modes').onclick=function(e){var b=e.target.closest('button');if(!b)return;
 if(b.dataset.tone)setPrefs({tone:b.dataset.tone});
 else if('kb' in b.dataset)setPrefs({kb:S.prefs.kb==='off'?'auto':'off'});
 else if('deep' in b.dataset)setPrefs({deep:!S.prefs.deep});
 else if('news' in b.dataset)openNews();
};
$('toneSeg').onclick=function(e){var b=e.target.closest('button');if(b)setPrefs({tone:b.dataset.k})};
$('kbSeg').onclick=function(e){var b=e.target.closest('button');if(b)setPrefs({kb:b.dataset.k})};
$('prefDeep').onchange=function(){setPrefs({deep:this.checked})};
$('prefLearn').onchange=function(){setPrefs({learn:this.checked})};
$('deepBtn').onclick=function(){setPrefs({deep:!S.prefs.deep});toast(S.prefs.deep?'🔍 Modo aprofundado ligado':'Modo aprofundado desligado')};
$('topicBox').onchange=function(e){var k=e.target.dataset.t;if(!k)return;var t=effTopics().filter(function(x){return x!==k});if(e.target.checked)t.push(k);if(!t.length){t=Object.keys(TOPICS)}setPrefs({topics:t})};
var styT=null;
$('prefStyle').oninput=function(){var v=this.value;clearTimeout(styT);styT=setTimeout(function(){setPrefs({style:v.slice(0,400)})},400)};
function memAdd(){var i=$('memIn'),v=i.value.trim().slice(0,200),m=S.prefs.mem.slice();if(!v)return;if(m.length>=20)return toast('Limite de 20 lembranças');if(m.indexOf(v)<0)m.push(v);i.value='';setPrefs({mem:m})}
$('memAddBtn').onclick=memAdd;
$('memIn').onkeydown=function(e){if(e.key==='Enter'){e.preventDefault();memAdd()}};
$('memList').onclick=function(e){var b=e.target.closest('button');if(!b)return;var m=S.prefs.mem.slice();m.splice(+b.dataset.i,1);setPrefs({mem:m})};
$('likeBtn').onclick=function(){if(!S.prefs.likes)return toast('Nada para limpar');askBox({title:'Limpar o aprendizado?',text:'A IA esquece as respostas que você aprovou.',ok:'Limpar',danger:true},function(){S.prefs.likes=0;paintPrefs();send('lchat:clearlikes');toast('Aprendizado limpo')})};

// ---------- ícones fixos ----------
$('newBtn').innerHTML=lbl('plus','Nova conversa');
$('backBtn').innerHTML=ic('home');
$('menuBtn').innerHTML=ic('menu');
$('deepBtn').innerHTML=ic('layers');
$('newsBtn').innerHTML=ic('wand');
$('cfgBtn').innerHTML=ic('gear');
$('moreBtn').innerHTML=ic('more');
$('cfgX').innerHTML=ic('x');$('newsX').innerHTML=ic('x');
$('cfgIco').innerHTML=ic('sliders',20);$('newsIco').innerHTML=ic('wand',20);
$('supBtn').innerHTML=ic('qr',16)+' Apoiar o projeto (QR Code)';
$('supCopy').innerHTML=ic('copy',15)+' Copiar link';$('supOpen').innerHTML=ic('link',15)+' Abrir no navegador';
$('secCon').innerHTML=ic('plug',14)+' Conexão';$('secDet').innerHTML=ic('spark',14)+' Detectados agora';$('secSite').innerHTML=ic('link',14)+' IA do site';
$('secMd').innerHTML=ic('cpu',14)+' Modelos de IA';$('secAdd').innerHTML=ic('download',14)+' Adicionar outro modelo';$('secPerf').innerHTML=ic('sliders',14)+' Desempenho';
$('mdUrlBtn').innerHTML=ic('download',15)+' Baixar';$('mdImpBtn').innerHTML=ic('file',15)+' Importar um .gguf do PC';
$('wallBtn').innerHTML=ic('image',16)+' Usar minha imagem';
$('resetBtn').innerHTML=ic('rotate',16)+' Restaurar padrão';
$('expAllBtn').innerHTML=ic('download',16)+' Salvar todas as conversas (.json)';
$('delAllBtn').innerHTML=ic('trash',16)+' Apagar todas as conversas';
$('clrCfgBtn').innerHTML=ic('key',16)+' Remover chave e configurações da IA';
$('mdlBtn').insertAdjacentHTML('afterbegin',ic('wand',15));
$('mdlBtn').insertAdjacentHTML('beforeend',ic('chev',14));
var DD={rename:['pen','Renomear'],export:['file','Salvar como .txt'],copyconv:['copy','Copiar conversa'],clear:['x','Limpar mensagens'],delcur:['trash','Apagar conversa'],delall:['trash','Apagar todas']};
document.querySelectorAll('#dd button').forEach(function(b){var d=DD[b.dataset.a];b.innerHTML=ic(d[0],16)+' '+d[1]});
(function(){var eq=$('eq');for(var i=0;i<46;i++){var b=document.createElement('i');
 b.style.cssText='--d:'+(0.9+Math.random()*1.4).toFixed(2)+'s;--l:-'+(Math.random()*2).toFixed(2)+'s;--h:'+(0.25+Math.random()*0.75).toFixed(2)+';--e:'+(i*18)+'ms';eq.appendChild(b)}})();

var CURIOS = [
  { c: 'musica', t: 'o Lá central (A4) é afinado em 440 Hz, a referência usada pela maioria dos instrumentos.' },
  { c: 'musica', t: 'um disco de vinil de 12 polegadas costuma girar a 33⅓ rotações por minuto.' },
  { c: 'musica', t: 'o ouvido humano percebe sons de cerca de 20 Hz até 20 kHz.' },
  { c: 'musica', t: 'o primeiro CD comercial foi lançado em 1982.' },
  { c: 'musica', t: 'ouvir uma música de que você gosta libera dopamina no cérebro.' },
  { c: 'musica', t: 'o Hino Hurrita nº 6, de cerca de 3.400 anos, é uma das músicas escritas mais antigas que se conhece.' },
  { c: 'musica', t: 'um piano padrão tem 88 teclas.' },
  { c: 'musica', t: 'Beethoven compôs a Nona Sinfonia já quase totalmente surdo.' },
  { c: 'musica', t: 'as sondas Voyager, lançadas em 1977, levam um disco de ouro com músicas e sons de várias culturas.' },
  { c: 'musica', t: 'o Spotify foi lançado em 2008, na Suécia.' },
  { c: 'musica', t: 'o cavaquinho tem 4 cordas, e o violão tem 6.' },
  { c: 'brasil', l: 'música no Brasil', t: 'a bossa nova ganhou força no fim dos anos 1950, e "Chega de Saudade" (1958), com João Gilberto, é vista como seu marco inicial.' },
  { c: 'brasil', l: 'música no Brasil', t: 'o berimbau, instrumento de origem africana, é a alma musical da capoeira.' },
  { c: 'brasil', l: 'música no Brasil', t: 'o samba carioca foi registrado como Patrimônio Cultural do Brasil pelo IPHAN, em 2007.' },
  { c: 'brasil', l: 'música no Brasil', t: 'a primeira edição do Rock in Rio aconteceu em janeiro de 1985, no Rio de Janeiro.' },
  { c: 'brasil', l: 'música no Brasil', t: '"Garota de Ipanema", de Tom Jobim e Vinicius de Moraes, é uma das músicas mais regravadas do mundo.' },
  { c: 'aviacao', t: 'os irmãos Wright fizeram o primeiro voo motorizado e controlado em 17 de dezembro de 1903; o primeiro voo durou apenas 12 segundos.' },
  { c: 'aviacao', t: 'o Concorde cruzava o Atlântico em cerca de 3h30, voando a mais de duas vezes a velocidade do som. Foi aposentado em 2003.' },
  { c: 'aviacao', t: 'aviões comerciais costumam cruzar entre 10.000 e 12.000 metros de altitude.' },
  { c: 'aviacao', t: 'as "caixas-pretas" dos aviões, na verdade, são laranja, para facilitar a localização.' },
  { c: 'aviacao', t: 'o Airbus A380 é o maior avião de passageiros já produzido, com dois andares inteiros.' },
  { c: 'aviacao', t: 'o Antonov An-225 Mriya foi o maior avião de carga do mundo; ele foi destruído em 2022, na Ucrânia.' },
  { c: 'aviacao', t: 'o voo comercial mais curto do mundo liga duas ilhas da Escócia (Westray e Papa Westray) e leva cerca de 2 minutos.' },
  { c: 'aviacao', t: 'estima-se que cada avião comercial seja atingido por um raio cerca de uma vez por ano; a fuselagem protege quem está dentro.' },
  { c: 'brasil', l: 'aviação no Brasil', t: 'Santos-Dumont fez o voo do 14-Bis em 23 de outubro de 1906, em Paris. Por isso, 23 de outubro é o Dia do Aviador no Brasil.' },
  { c: 'brasil', l: 'aviação no Brasil', t: 'a Embraer foi fundada em 1969, em São José dos Campos (SP), e é uma das maiores fabricantes de aviões do mundo.' },
  { c: 'brasil', l: 'aviação no Brasil', t: 'a Varig, fundada em 1927 em Porto Alegre, foi a primeira companhia aérea do Brasil.' },
  { c: 'brasil', l: 'aviação no Brasil', t: 'o Aeroporto de Guarulhos (GRU) é o que mais movimenta passageiros no Brasil.' },
  { c: 'ambiente', t: 'o oceano produz pelo menos metade do oxigênio da atmosfera, em boa parte graças ao fitoplâncton, algas microscópicas que fazem fotossíntese.' },
  { c: 'ambiente', t: 'os recifes de coral cobrem menos de 1% do fundo do oceano, mas abrigam cerca de 25% das espécies marinhas.' },
  { c: 'ambiente', t: 'só cerca de 3% da água do planeta é doce, e a maior parte dela está congelada ou no subsolo.' },
  { c: 'ambiente', t: 'reciclar uma lata de alumínio gasta cerca de 95% menos energia do que produzir uma nova a partir do minério.' },
  { c: 'ambiente', t: 'o vidro pode ser reciclado infinitas vezes sem perder qualidade.' },
  { c: 'ambiente', t: 'o papel pode ser reciclado de 5 a 7 vezes, porque as fibras vão ficando mais curtas a cada ciclo.' },
  { c: 'ambiente', t: 'uma garrafa PET pode levar centenas de anos para se decompor na natureza.' },
  { c: 'ambiente', t: 'cerca de 75% das principais culturas alimentares do mundo dependem, ao menos em parte, de polinizadores como abelhas, borboletas e morcegos.' },
  { c: 'ambiente', t: 'o sexo dos filhotes de tartaruga-marinha é definido pela temperatura da areia do ninho: ninhos mais quentes geram mais fêmeas.' },
  { c: 'ambiente', t: 'a camada de ozônio está em recuperação graças ao Protocolo de Montreal, de 1987, um dos acordos ambientais mais bem-sucedidos da história.' },
  { c: 'ambiente', t: 'lâmpadas de LED chegam a gastar cerca de 80% menos energia do que as incandescentes.' },
  { c: 'ambiente', t: 'o Dia Mundial do Meio Ambiente é comemorado em 5 de junho.' },
  { c: 'ambiente', t: 'a compostagem transforma restos de comida e de poda em adubo, e ajuda a reduzir o lixo que vai para os aterros.' },
  { c: 'ambiente', t: 'estima-se que milhões de toneladas de plástico cheguem aos oceanos todos os anos.' },
  { c: 'brasil', l: 'meio ambiente no Brasil', t: 'a Amazônia é a maior floresta tropical do mundo e abriga cerca de 10% das espécies conhecidas do planeta.' },
  { c: 'brasil', l: 'meio ambiente no Brasil', t: 'as árvores da Amazônia liberam vapor de água que forma os "rios voadores", massas de ar úmido que ajudam a levar chuva a outras regiões do Brasil.' },
  { c: 'brasil', l: 'meio ambiente no Brasil', t: 'o Pantanal é considerado a maior planície alagável do planeta.' },
  { c: 'brasil', l: 'meio ambiente no Brasil', t: 'o Cerrado é considerado a savana mais rica em biodiversidade do mundo e abriga nascentes de grandes bacias hidrográficas do país.' },
  { c: 'brasil', l: 'meio ambiente no Brasil', t: 'restam cerca de 12% da cobertura original da Mata Atlântica, que mesmo assim ainda abriga milhares de espécies que só existem lá.' },
  { c: 'brasil', l: 'meio ambiente no Brasil', t: 'o pau-brasil, árvore que deu nome ao país, foi tão explorado desde o século XVI que hoje é considerado ameaçado de extinção.' },
  { c: 'brasil', l: 'meio ambiente no Brasil', t: 'o mico-leão-dourado, símbolo da Mata Atlântica, foi salvo da beira da extinção por programas de conservação, mas ainda precisa de proteção.' },
  { c: 'brasil', l: 'meio ambiente no Brasil', t: 'o Brasil guarda cerca de 12% da água doce do planeta.' },
  { c: 'brasil', l: 'meio ambiente no Brasil', t: 'mais de 80% da eletricidade brasileira vem de fontes renováveis, como hidrelétricas, ventos, sol e biomassa.' },
  { c: 'brasil', l: 'meio ambiente no Brasil', t: 'a maior parte da energia eólica do Brasil é gerada no Nordeste, graças aos ventos constantes da região.' },
  { c: 'brasil', l: 'meio ambiente no Brasil', t: 'a Eco-92, grande conferência da ONU sobre meio ambiente, aconteceu no Rio de Janeiro, em 1992.' },
  { c: 'brasil', l: 'meio ambiente no Brasil', t: 'a COP30, a conferência do clima da ONU, aconteceu em Belém (PA), em novembro de 2025.' },
  { c: 'brasil', l: 'meio ambiente no Brasil', t: 'o Brasil está entre os países que mais reciclam latas de alumínio, com índices acima de 95% nos últimos anos.' },
  { c: 'geral', t: 'o Discord foi lançado em 2015.' }
];
var GN=['Oi, {n}! Vamos nessa?','Olá, {n}! Como posso ajudar hoje?','E aí, {n}! Qual é a dúvida de hoje?','Bem-vindo de volta, {n}!','{n}, o que vamos resolver hoje?','Pronto para mais um som, {n}? 🎵','Que bom te ver, {n}! 🎶','{n}, pronto para a próxima playlist?','Fala, {n}! O que vamos descobrir hoje?','Seja bem-vindo(a), {n}! ✨','Opa, {n}! Me conta: qual é a missão?'];
var GA=['Como posso ajudar hoje?','Vamos nessa?','Qual é a dúvida de hoje?','Pronto para começar?','Em que posso ajudar?','Bora configurar esse bot? 🎵','Que bom te ver por aqui! 🎶','Pronto para a próxima playlist?','Fala aí! O que vamos descobrir hoje?','Seja bem-vindo(a)! ✨','Me conta: qual é a missão de hoje?'];
var LBL={musica:'música',aviacao:'aviação',ambiente:'meio ambiente'};
function esc(s){return String(s).replace(/[&<>"']/g,function(m){return {'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#039;'}[m]})}
function rand(a){return a[Math.floor(Math.random()*a.length)]}
function clock(){return new Date().toLocaleTimeString('pt-BR',{hour:'2-digit',minute:'2-digit'})}
function period(){var h=new Date().getHours();return h<5?'Boa madrugada':h<12?'Bom dia':h<18?'Boa tarde':'Boa noite'}
function calm(t){return S.prefs.tone==='serio'?t.replace(/\s*[\u{1F300}-\u{1FAFF}\u2728]\uFE0F?/gu,''):t}
function animOn(){return !!S.look.anim&&!window.matchMedia('(prefers-reduced-motion: reduce)').matches}
function pickGreeting(){
 var first=(S.name||'').split(' ')[0],n=first?'<b>'+esc(first)+'</b>':'',r=Math.random();
 var cs=(S.curios&&S.curios.length)?S.curios:CURIOS,et=effTopics();
 var pool=cs.filter(function(x){return et.indexOf(x.c)>=0});
 if(r<.25)return period()+(first?', '+n:'')+'! São <span class="clk">'+clock()+'</span>. '+(first?'Vamos nessa?':'Como posso ajudar?');
 if(r<.55&&pool.length){var x=rand(pool),lb=x.l||LBL[x.c]||'';return '<span class="cur">💡 <b>Curiosidade'+(lb?' de '+lb:'')+':</b> '+esc(x.t)+'</span>'}
 var opts=(first?GN.map(function(f){return f.replace('{n}',n)}):[]).concat(GA);
 return calm(rand(opts));
}
var typeT=null;
function typeInto(el,html){
 clearInterval(typeT);el.classList.remove('typing');el.innerHTML=html;
 if(!animOn())return;
 var w=document.createTreeWalker(el,NodeFilter.SHOW_TEXT),nodes=[],nd;
 while((nd=w.nextNode())){var a=Array.from(nd.nodeValue);if(a.length)nodes.push({n:nd,a:a})}
 var total=nodes.reduce(function(t,x){return t+x.a.length},0);if(!total)return;
 nodes.forEach(function(x){x.n.nodeValue=''});
 var caret=document.createElement('i');caret.className='caret';el.classList.add('typing');
 var per=Math.ceil(total/90),i=0;
 typeT=setInterval(function(){
  i=Math.min(total,i+per);var left=i,last=nodes[0];
  for(var k=0;k<nodes.length;k++){var x=nodes[k],c=Math.min(left,x.a.length);x.n.nodeValue=x.a.slice(0,c).join('');if(left>0)last=x;left-=c}
  last.n.parentNode.insertBefore(caret,last.n.nextSibling);
  if(i>=total){clearInterval(typeT);caret.remove();el.classList.remove('typing')}
 },28);
}
function showHello(){typeInto($('hello'),pickGreeting())}
setInterval(function(){document.querySelectorAll('.clk').forEach(function(e){if(e.closest('.typing'))return;var t=clock();if(e.textContent!==t)e.textContent=t})},1000);
function renderUser(){var av=$('av');if(S.avatar){av.src=S.avatar;av.style.display=''}else av.style.display='none';$('nm').textContent=S.name||'Offline'}
function canSite(){return !!(S.preferSite&&S.siteAi&&(!S.siteAi.needsKey||S.siteAi.hasKey))}
function renderSiteAi(){
 var a=S.siteAi,n=$('siteAiNote');
 if(!a){n.textContent='Abra o chat do site (com internet) para copiar a IA que está configurada lá.';return}
 var p=preset(a.provider);
 n.textContent='IA do site: '+(p?p.name.replace(' (internet)',''):a.provider)+' · '+a.model+
  (a.needsKey&&!a.hasKey?' — ligue a opção, salve e abra o chat do site com internet para copiar a chave.':'. Se a internet cair, volta para a IA do seu PC.');
}

// ---------- caixa de confirmação própria (no lugar do "Esta página diz") ----------
var askCb=null;
function askBox(o,cb){
 $('askT').textContent=o.title||'Confirmar';$('askP').textContent=o.text||'';
 var i=$('askIn');i.style.display=o.input?'':'none';i.value=o.value||'';
 var y=$('askYes');y.textContent=o.ok||'OK';y.className='btn2 ok'+(o.danger?' danger':'');
 askCb=cb;$('askM').style.display='flex';
 setTimeout(function(){var f=o.input?i:y;f.focus();if(o.input)i.select()},30);
}
function askClose(ok){var cb=askCb,v=$('askIn').value;askCb=null;$('askM').style.display='none';if(ok&&cb)cb(v)}
$('askYes').onclick=function(){askClose(true)};
$('askNo').onclick=function(){askClose(false)};
$('askM').onmousedown=function(e){this.dn=e.target===this};
$('askM').onmouseup=function(e){if(this.dn&&e.target===this)askClose(false);this.dn=false};
document.addEventListener('keydown',function(e){
 if($('askM').style.display!=='flex')return;
 if(e.key==='Escape'){e.preventDefault();askClose(false)}
 else if(e.key==='Enter'&&e.target.id==='askIn'){e.preventDefault();askClose(true)}
});

// ---------- util ----------
function toast(t){var e=$('toast');e.textContent=t;e.classList.add('on');clearTimeout(toast.t);toast.t=setTimeout(function(){e.classList.remove('on')},1800)}
function fmtTime(t){var d=new Date((t||0)*1000);return ('0'+d.getHours()).slice(-2)+':'+('0'+d.getMinutes()).slice(-2)}
function scroll(){var m=$('msgs');m.scrollTop=m.scrollHeight}
function setEmpty(){$('main').classList.toggle('empty',$('msgs').children.length===0)}
function preset(id){return S.presets.filter(function(p){return p.id===id})[0]}
function closeSide(){if(window.innerWidth<=760)$('side').classList.remove('open')}
$('menuBtn').onclick=function(){if(window.innerWidth<=760)$('side').classList.toggle('open');else document.body.classList.toggle('side-off')};
function closeSup(){$('supM').style.display='none'}
$('supBtn').onclick=function(){$('supM').style.display='flex'};
$('supClose').onclick=closeSup;
$('supOpen').onclick=function(){send('lchat:support')};
$('supCopy').onclick=function(){send('lchat:copy:https://livepix.gg/astmsoftware');toast('Link copiado')};
$('supM').onmousedown=function(e){this.dn=e.target===this};
$('supM').onmouseup=function(e){if(this.dn&&e.target===this)closeSup();this.dn=false};

// ---------- novidades ----------
function openNews(){
 $('newsList').innerHTML=NEWS.map(function(t,i){return '<li style="--i:'+i+'">'+esc(t).replace(/\*\*(.+?)\*\*/g,'<b>$1</b>')+'</li>'}).join('');
 $('news').style.display='flex';
}
function closeNews(){$('news').style.display='none'}
$('newsBtn').onclick=openNews;$('newsX').onclick=closeNews;$('newsOk').onclick=closeNews;
$('news').onmousedown=function(e){this.dn=e.target===this};
$('news').onmouseup=function(e){if(this.dn&&e.target===this)closeNews();this.dn=false};

// ---------- mensagens ----------
function decorate(d,time,q){
 if(time){var t=document.createElement('time');t.textContent=fmtTime(time);d.appendChild(t)}
 if(d.classList.contains('bot')){
  var a=document.createElement('div');a.className='act';
  var c=document.createElement('button');c.innerHTML=ic('copy',14)+' Copiar';
  c.onclick=function(){send('lchat:copy:'+d.tx.textContent);toast('Copiado')};
  var l=document.createElement('button');l.innerHTML=ic('thumb',14)+' Gostei';l.title='A IA aprende o estilo das respostas que você aprova';
  l.onclick=function(){var on=!l.classList.contains('on');l.classList.toggle('on',on);
   S.prefs.likes=Math.max(0,(S.prefs.likes||0)+(on?1:-1));paintPrefs();
   send('lchat:like:'+JSON.stringify({q:q||'',a:d.tx.textContent.slice(0,400),on:on}));
   toast(on?(S.prefs.learn?'👍 Anotado: vou imitar o estilo dessa resposta':'👍 Salvo'):'Curtida removida')};
  var p=document.createElement('button');p.innerHTML=ic('layers',14)+' Aprofundar';p.title='Pede uma resposta mais completa';
  p.onclick=function(){if(!busy)doSend('Aprofunde sua resposta anterior: mais detalhes, exemplos práticos e um resumo no final.')};
  a.appendChild(c);a.appendChild(l);a.appendChild(p);d.appendChild(a)}
}
function addMsg(role,text,time,live,q){
 var d=document.createElement('div');d.className='m '+role;
 var b=document.createElement('div');b.className='tx';b.textContent=text;d.appendChild(b);d.tx=b;
 if(!live&&role!=='err')decorate(d,time,q);
 $('msgs').appendChild(d);setEmpty();scroll();return d;
}
function renderMsgs(list){
 $('msgs').innerHTML='';var q='';
 (list||[]).forEach(function(m){if(m.role==='user')q=m.content;addMsg(m.role==='user'?'user':'bot',m.content,m.time,false,q)});
 lastQ=q;setEmpty();
 if(!(list&&list.length))showHello();
}
function grpOf(t){
 var n=new Date(),s=new Date(n.getFullYear(),n.getMonth(),n.getDate()).getTime()/1000;
 if(!t||t>=s)return 'Hoje';if(t>=s-86400)return 'Ontem';if(t>=s-6*86400)return 'Últimos 7 dias';return 'Mais antigas';
}
function renderList(){
 var l=$('list');l.innerHTML='';var last='';
 S.convs.forEach(function(c){
  var g=grpOf(c.t);if(g!==last){var h=document.createElement('div');h.className='grp';h.textContent=g;l.appendChild(h);last=g}
  var d=document.createElement('div');d.className='conv'+(c.id===S.cur?' on':'');
  d.insertAdjacentHTML('afterbegin',ic('chat',16));
  var s=document.createElement('span');s.textContent=c.title;d.appendChild(s);
  if(c.site){var e=document.createElement('em');e.textContent='site';e.title='Conversa copiada do chat do site';d.appendChild(e)}
  var b=document.createElement('button');b.innerHTML=ic('trash',15);b.title='Apagar';
  b.onclick=function(ev){ev.stopPropagation();if(busy)return;askBox({title:'Apagar esta conversa?',text:'Ela some do chat offline e não dá para desfazer.',ok:'Apagar',danger:true},function(){send('lchat:del:'+c.id)})};
  d.appendChild(b);
  d.onclick=function(){if(busy||c.id===S.cur)return;send('lchat:open:'+c.id);closeSide()};
  l.appendChild(d)});
 var c=S.convs.filter(function(x){return x.id===S.cur})[0];
 $('title').textContent=c?c.title:'Nova conversa';
}
function setBusy(b){busy=b;$('send').innerHTML=ic(b?'stop':'send');$('send').title=b?'Parar':'Enviar'}
setBusy(false);

function doSend(text){
 var own=typeof text==='string',v=(own?text:$('inp').value).trim();if(!v)return;
 if(!S.base&&!canSite()){if(!own){$('inp').value='';$('inp').style.height='auto'}showNM(v,null);return}
 if(NM){NM.el.remove();NM=null}
 addMsg('user',v,Math.floor(Date.now()/1000));lastQ=v;
 if(!own){$('inp').value='';$('inp').style.height='auto'}
 setBusy(true);
 curMsg=addMsg('bot','',0,true);curMsg.tx.innerHTML='<span class="dots"><i></i><i></i><i></i></span>';curMsg.dots=true;
 send('lchat:send:'+v);
}
$('send').onclick=function(){if(busy)send('lchat:stop');else doSend()};
$('inp').onkeydown=function(e){if(e.key==='Enter'&&!e.shiftKey){e.preventDefault();if(!busy)doSend()}};
$('inp').oninput=function(){this.style.height='auto';this.style.height=Math.min(this.scrollHeight,140)+'px'};
$('newBtn').onclick=function(){if(!busy){send('lchat:new');closeSide()}};
$('backBtn').onclick=function(){send('lchat:back')};

// ---------- cartão "falta uma IA" (no lugar do erro em texto) ----------
var NM=null;
function nmOpt(a,id,icn,t,sub,dis){return '<button class="nmo" data-a="'+a+'" data-id="'+esc(id||'')+'"'+(dis?' disabled':'')+'><span class="tile">'+ic(icn,16)+'</span><span><b>'+esc(t)+'</b><small>'+esc(sub)+'</small></span></button>'}
function nmClick(e){
 var b=e.target.closest('button');if(!b||b.disabled||!NM)return;var x=b.dataset.a,id=b.dataset.id;
 if(x==='dluse'){S.ai.dl=id;send('lchat:mdl_dluse:'+id);paintNM()}
 else if(x==='use'){S.ai.phase='load';S.ai.text='Carregando a IA na memória…';send('lchat:mdl_use:'+id);paintNM()}
 else if(x==='cancel')send('lchat:mdl_cancel');
 else if(x==='ia')openCfg('ia');
 else if(x==='md')openCfg('md');
 else if(x==='retry'){var n=NM;NM=null;n.el.remove();if(n.lu)n.lu.remove();setEmpty();if(n.q)doSend(n.q)}
}
function paintNM(){
 if(!NM)return;
 var a=S.ai,items=a.items||[],rec=items.filter(function(x){return x.rec})[0]||items[0],have=items.filter(function(x){return x.have})[0];
 var h='<b class="nmt">Falta uma IA para responder</b>',working=busyEng()||!!a.dl;
 if(working){
  h+='<small>'+(a.dl?'Baixando '+esc(mname(a.dl))+'…':esc(a.text||'Preparando a IA…'))+'</small>'
   +'<div class="pb"><i id="nmPb" style="width:'+(a.dl?0:(a.pct||0))+'%"></i></div>'
   +'<small id="nmSt">'+(a.dl?'Começando…':'')+'</small><small>Quando terminar, sua pergunta é enviada sozinha.</small>'
   +(a.dl?'<div class="nmr"><button class="btn2" data-a="cancel">Cancelar download</button></div>':'');
 }else{
  h+='<small>Escolha como você quer conversar:</small>';
  if(a.phase==='error'&&a.text)h+='<div class="eng err">⚠️ '+esc(a.text)+'</div>';
  if(have)h+=nmOpt('use',have.id,'spark','Usar '+have.name,'Já está baixada neste PC · funciona sem internet');
  else if(rec)h+=nmOpt('dluse',rec.id,'download','Baixar a IA do Azul Groove',
   a.online===false?'Sem internet agora: conecte-se uma vez para baixar':'Recomendada para o seu PC: '+rec.name+(rec.gb?' · '+rec.gb+' GB':'')+' · depois funciona sem internet',a.online===false);
  h+=nmOpt('ia','','plug','Usar outra IA','Ollama, LM Studio, OpenRouter, OpenAI, Groq…');
  h+=nmOpt('md','','cpu','Ver todos os modelos','Escolher, baixar ou importar um arquivo .gguf');
  h+='<div class="nmr"><button class="btn2" data-a="retry">'+ic('rotate',14)+' Tentar de novo</button></div>';
 }
 NM.el.innerHTML=h;
}
function showNM(q,lu){
 if(NM){NM.el.remove();NM=null}
 var el=document.createElement('div');el.className='m help';el.onclick=nmClick;
 NM={el:el,q:q||'',lu:lu||null};
 $('msgs').appendChild(el);paintNM();setEmpty();scroll();send('lchat:aistate');
}

// ---------- status da IA ----------
function mname(m){var it=(S.ai.items||[]).filter(function(x){return x.id===m})[0];return it?it.name:m}
function fmtGb(b){return (b/1073741824).toFixed(2)+' GB'}
function busyEng(){return ['download','extract','load'].indexOf(S.ai.phase)>=0}
function paintEng(){
 var a=S.ai,e=$('engBox'),h='',c='eng';
 if(busyEng()){h='⏳ '+esc(a.text)+(a.pct?' '+a.pct+'%':'')}
 else if(a.phase==='error'){c+=' err';h='⚠️ '+esc(a.text)}
 else if(a.running&&a.running===a.active){c+=' ok';h='✅ IA pronta, rodando neste PC (funciona sem internet).'}
 else if(!a.engineReady){h='ℹ️ O motor da IA (uns 40 MB) é baixado uma única vez, quando você usar um modelo pela primeira vez.'}
 else{h='ℹ️ Motor instalado. Escolha um modelo abaixo.'}
 e.className=c;e.innerHTML=h;
}
function paintAi(){
 var a=S.ai;
 $('mdVk').checked=!!a.vulkan;
 $('mdRam').textContent=a.ramGb?'Seu PC tem cerca de '+a.ramGb+' GB de RAM. O modelo marcado como Recomendado é o que melhor combina com ele.':'';
 paintEng();
 $('mdList').innerHTML=(a.items||[]).map(function(m){
  var on=a.active===m.id,dl=a.dl===m.id,bt='';
  if(dl)bt='<button class="btn2" data-a="cancel" data-id="'+m.id+'">Cancelar</button>';
  else if(m.have){bt=(on?'<button class="btn2 ok" disabled>Em uso ✓</button>':'<button class="btn2 ok" data-a="use" data-id="'+m.id+'">Usar este</button>')+'<button class="btn2 dng" data-a="del" data-id="'+m.id+'" title="Apagar do PC">'+ic('trash',14)+'</button>'}
  else bt='<button class="btn2 ok" data-a="dl" data-id="'+m.id+'">'+ic('download',14)+' Baixar'+(m.gb?' ('+m.gb+' GB)':'')+'</button>'+(m.custom?'<button class="btn2 dng" data-a="del" data-id="'+m.id+'" title="Tirar da lista">'+ic('trash',14)+'</button>':'');
  var meta=(m.gb?m.gb+' GB':'')+(m.ram?' · pede ~'+m.ram+' GB de RAM':'')+(m.lic?' · '+esc(m.lic):'');
  return '<div class="mdc'+(on?' on':'')+'"><div class="inf"><b>'+esc(m.name)+(m.rec?' <span class="tag">Recomendado</span>':'')+(m.have?' <span class="tag ok">Baixado</span>':'')+'</b><small>'+esc(m.desc)+'</small><small>'+meta+'</small>'
   +(dl?'<div class="pb"><i id="pb_'+m.id+'"></i></div><small id="pt_'+m.id+'">Começando…</small>':'')+'</div><div class="bt">'+bt+'</div></div>';
 }).join('');
}
$('mdList').onclick=function(e){var b=e.target.closest('button');if(!b||b.disabled)return;var id=b.dataset.id,x=b.dataset.a;
 if(x==='dl')send('lchat:mdl_dl:'+id);
 else if(x==='cancel')send('lchat:mdl_cancel');
 else if(x==='use')send('lchat:mdl_use:'+id);
 else if(x==='del'){askBox({title:'Apagar este modelo?',text:'Ele sai do PC. Você pode baixar de novo depois.',ok:'Apagar',danger:true},function(){send('lchat:mdl_del:'+id)})}};
$('mdUrlBtn').onclick=function(){var v=$('mdUrl').value.trim();if(!v)return;send('lchat:mdl_url:'+v);$('mdUrl').value=''};
$('mdImpBtn').onclick=function(){send('lchat:mdl_import')};
$('mdVk').onchange=function(){S.ai.vulkan=this.checked;send('lchat:mdl_vulkan:'+(this.checked?'1':'0'))};
function renderStatus(){
 var p=preset(S.provider),name=p?p.name.replace(' (internet)',''):(S.base?'IA':'');
 var dot=S.up===true?'#3ba55d':(S.up===false?'#ed4245':'#80848e');
 var ai=$('aiInfo');ai.innerHTML='<i></i><span></span>';ai.firstChild.style.background=dot;
 ai.lastChild.textContent=S.base?(name+(S.model?' · '+mname(S.model):'')):'Nenhuma IA configurada';
 $('mdlName').textContent=S.model?mname(S.model):'Auto';
 var msg='',nb='Configurar';
 if(busyEng()){msg=S.ai.text+(S.ai.pct?' '+S.ai.pct+'%':'');nb=''}
 else if(S.ai.phase==='error'&&S.provider==='azul'){msg=S.ai.text;nb='Ver modelos'}
 else if(!S.base&&!canSite()){msg='Nenhuma IA encontrada. Baixe a IA do Azul Groove (funciona sem internet) ou abra o Ollama / LM Studio.';nb='Escolher IA'}
 else if(S.up===false&&S.provider==='azul')msg='A IA do Azul Groove ainda não está ligada neste PC.';
 else if(S.up===false)msg='Não consegui conectar em '+name+'. Ele está aberto?';
 else if(S.up===true&&!S.models.length){msg='Essa IA ainda não tem nenhum modelo pronto.';nb='Escolher IA'}
 $('nTxt').textContent=msg;$('nBtn').textContent=nb||'';$('nBtn').style.display=nb?'':'none';$('notice').style.display=msg?'flex':'none';
}
$('nBtn').onclick=function(){openCfg('md')};

// ---------- menu "..." ----------
$('moreBtn').onclick=function(e){e.stopPropagation();$('dd').classList.toggle('open')};
$('dd').onclick=function(e){
 var b=e.target.closest('button');if(!b)return;$('dd').classList.remove('open');
 var a=b.dataset.a,c=S.convs.filter(function(x){return x.id===S.cur})[0];
 if(a==='rename'&&c){askBox({title:'Renomear conversa',text:'',input:true,value:c.title,ok:'Salvar'},function(t){if(t&&t.trim())send('lchat:rename:'+c.id+'|'+t.trim())})}
 else if(a==='export')send('lchat:export');
 else if(a==='copyconv'){send('lchat:copyconv');toast('Conversa copiada')}
 else if(a==='clear'&&!busy)askBox({title:'Limpar as mensagens?',text:'A conversa continua na lista, só que vazia.',ok:'Limpar',danger:true},function(){send('lchat:clear')});
 else if(a==='delcur'&&c&&!busy)askBox({title:'Apagar esta conversa?',text:'Ela some do chat offline e não dá para desfazer.',ok:'Apagar',danger:true},function(){send('lchat:del:'+c.id)});
 else if(a==='delall'&&!busy)askBox({title:'Apagar todas as conversas?',text:'Não dá para desfazer.',ok:'Apagar tudo',danger:true},function(){send('lchat:delall')});
};

// ---------- escolher o modelo ----------
function closePop(){$('mdlPop').classList.remove('on')}
$('mdlBtn').onclick=function(e){
 e.stopPropagation();var p=$('mdlPop');
 if(p.classList.contains('on')){closePop();return}
 p.innerHTML='';
 var h=document.createElement('div');h.className='ph';h.textContent='Escolher o modelo';p.appendChild(h);
 if(!S.models.length){var n=document.createElement('div');n.className='pn';n.textContent=S.base?'Essa IA ainda não tem modelos.':'Conecte uma IA para ver os modelos.';p.appendChild(n)}
 S.models.forEach(function(m){
  var b=document.createElement('button');b.className='it'+(m===S.model?' sel':'');
  var t=document.createElement('span');t.className='tile';t.innerHTML=ic('spark',16);
  var s=document.createElement('span');s.textContent=mname(m);b.appendChild(t);b.appendChild(s);
  b.onclick=function(){S.model=m;send('lchat:model:'+m);renderStatus();closePop()};
  p.appendChild(b)});
 var c=document.createElement('button');c.className='it';c.innerHTML='<span class="tile">'+ic('sliders',16)+'</span><span>Configurar a conexão…</span>';
 c.onclick=function(){closePop();openCfg('ia')};p.appendChild(c);
 var r=$('mdlBtn').getBoundingClientRect();
 p.style.left=Math.max(8,Math.min(r.right-330,window.innerWidth-338))+'px';
 p.style.bottom=(window.innerHeight-r.top+8)+'px';p.style.top='auto';
 p.classList.add('on');
};
document.addEventListener('click',function(e){$('dd').classList.remove('open');if(!e.target.closest('#mdlPop'))closePop()});

// ---------- configurações ----------
var TABS=[['md','cpu','Modelos'],['ia','robot','Outras IAs'],['ap','palette','Aparência'],['es','wand','Estilo'],['da','db','Dados'],['co','user','Conta']];
function tab(n){
 curTab=n;
 TABS.forEach(function(t){$('p_'+t[0]).hidden=t[0]!==n});
 $('tabs').innerHTML=TABS.map(function(t){return '<button data-t="'+t[0]+'" class="'+(t[0]===n?'on':'')+'">'+ic(t[1],15)+'<span>'+t[2]+'</span></button>'}).join('');
 $('cfgSave').style.display=n==='ia'?'':'none';
 if(n==='co'){var a=$('coAv');if(S.avatar){a.src=S.avatar;a.style.display=''}else a.style.display='none';
  $('coNm').textContent=S.name||'Modo offline';
  $('coPr').textContent=S.name?'Perfil copiado do chat do site':'Abra o chat do site com internet para copiar seu perfil'}
 if(n==='md'){send('lchat:aistate');paintAi()}
 if(n==='ap'){paintTheme();applyLook()}
 if(n==='es')paintPrefs();
}
$('tabs').onclick=function(e){var b=e.target.closest('button');if(b)tab(b.dataset.t)};
$('expAllBtn').onclick=function(){send('lchat:exportall')};
$('delAllBtn').onclick=function(){if(!busy)askBox({title:'Apagar todas as conversas?',text:'Não dá para desfazer.',ok:'Apagar tudo',danger:true},function(){send('lchat:delall')})};
$('clrCfgBtn').onclick=function(){askBox({title:'Remover as configurações da IA?',text:'A chave e a conexão salvas neste app serão apagadas.',ok:'Remover',danger:true},function(){send('lchat:clearcfg');closeCfg();toast('Configurações da IA removidas')})};
function renderDetected(){
 var d=$('det');d.innerHTML='';
 if(!S.detected.length){var s=document.createElement('small');s.textContent='Nenhum app de IA aberto no momento.';d.appendChild(s);return}
 S.detected.forEach(function(id){var p=preset(id);if(!p)return;
  var b=document.createElement('button');b.textContent='● '+p.name;
  b.onclick=function(){$('cfgProv').value=id;provChange()};d.appendChild(b)});
}
function renderCfgModels(){
 var l=$('cfgModels');l.innerHTML='';
 CM.forEach(function(m){var b=document.createElement('button');b.textContent=m;b.className=m===$('cfgModel').value?'on':'';
  b.onclick=function(){$('cfgModel').value=m;renderCfgModels()};l.appendChild(b)});
}
function provChange(){
 var p=preset($('cfgProv').value);
 if(p&&p.base)$('cfgBase').value=p.base;
 $('cfgKey').placeholder=p&&p.key?'cole a chave da API':'vazio para apps locais';
}
function openCfg(t){
 var sel=$('cfgProv');sel.innerHTML='';
 S.presets.forEach(function(p){var o=document.createElement('option');o.value=p.id;o.textContent=p.name+(S.detected.indexOf(p.id)>=0?'  ● aberto':'');sel.appendChild(o)});
 sel.value=S.provider||'custom';
 $('cfgBase').value=S.base||'';$('cfgKey').value='';$('cfgClr').checked=false;
 $('clrRow').style.display=S.hasKey?'flex':'none';
 $('cfgModel').value=S.model||'';$('cfgMsg').textContent='';
 var p=preset(sel.value);$('cfgKey').placeholder=S.hasKey?'chave salva — deixe vazio para manter':(p&&p.key?'cole a chave da API':'vazio para apps locais');
 CM=S.models.slice();renderCfgModels();renderDetected();
 var n=S.convs.filter(function(c){return c.site}).length;
 $('siteNote').textContent=n?('🌐 '+n+' conversa(s) do chat do site copiada(s) para cá.'):'🌐 Com internet, as conversas do chat do site são copiadas para cá automaticamente.';
 $('cfgSite').checked=!!S.preferSite;renderSiteAi();
 tab(typeof t==='string'?t:curTab);
 $('cfg').style.display='flex';send('lchat:probe');
}
function closeCfg(){$('cfg').style.display='none'}
$('cfgProv').onchange=provChange;
$('cfgBtn').onclick=function(e){e.stopPropagation();openCfg()};
$('cfgX').onclick=closeCfg;$('cfgCancel').onclick=closeCfg;
$('cfgModel').oninput=renderCfgModels;
$('cfgFetch').onclick=function(){$('cfgMsg').textContent='Buscando modelos...';send('lchat:cfgmodels:'+JSON.stringify({base:$('cfgBase').value,key:$('cfgKey').value}))};
$('cfgSave').onclick=function(){send('lchat:cfg:'+JSON.stringify({provider:$('cfgProv').value,base:$('cfgBase').value,key:$('cfgKey').value,clearKey:$('cfgClr').checked,model:$('cfgModel').value,preferSite:$('cfgSite').checked}))};
$('cfg').onmousedown=function(e){this.dn=e.target===this};
$('cfg').onmouseup=function(e){if(this.dn&&e.target===this)closeCfg();this.dn=false};

// ---------- digitação suave (fila) ----------
var q='',qt=null,fin=null;
function initOut(){
 var m=curMsg;if(m.txt)return;
 m.tx.textContent='';m.dots=false;m.txt=document.createTextNode('');m.tx.appendChild(m.txt);
 m.car=document.createElement('i');m.car.className='caret';m.tx.appendChild(m.car);
}
function kick(){if(!qt)qt=setTimeout(pump,0)}
function pump(){
 qt=null;
 if(q.length&&curMsg&&curMsg.txt){
  var an=animOn(),n=an?Math.max(1,Math.ceil(q.length/25)):q.length;
  curMsg.txt.nodeValue+=q.slice(0,n);q=q.slice(n);scroll();
  qt=setTimeout(pump,an?16:0);return;
 }
 if(fin){var d=fin;fin=null;q='';finish(d)}
}
function finish(d){
 if(curMsg){
  if(curMsg.car)curMsg.car.remove();
  if(!curMsg.txt||!curMsg.txt.nodeValue)curMsg.remove();else decorate(curMsg,d&&d.time,lastQ);
 }
 curMsg=null;setBusy(false);setEmpty();$('inp').focus();
}

// ---------- chamadas vindas do app ----------
window.__lc={
 state:function(d){
  S.provider=d.provider;S.base=d.base;S.hasKey=d.hasKey;S.model=d.model;S.theme=d.theme;S.presets=d.presets;S.convs=d.convs;S.cur=d.cur;
  S.name=d.name||'';S.avatar=d.avatar||'';S.preferSite=!!d.preferSite;S.siteAi=d.siteAi||null;S.curios=d.curios||null;
  S.look=d.look;S.prefs=d.prefs;S.prefs.tone=S.prefs.tone||'normal';S.prefs.mem=S.prefs.mem||[];S.prefs.topics=S.prefs.topics||[];
  clearTimeout(qt);qt=null;q='';fin=null;
  applyTheme();applyLook();paintPrefs();renderUser();renderList();renderMsgs(d.messages);renderStatus();
 },
 wall:function(u){WALLIMG=u||'';if(!WALLIMG&&S.look.wall==='img')S.look.wall='none';applyLook()},
 convs:function(d){S.convs=d.convs;S.cur=d.cur;renderList()},
 status:function(d){
  S.preferSite=!!d.preferSite;S.up=d.up;S.models=d.models;S.model=d.model;S.provider=d.provider;S.base=d.base;S.hasKey=d.hasKey;S.detected=d.detected;
  renderStatus();
  if($('cfg').style.display==='flex'){renderDetected();if(!CM.length){CM=S.models.slice();renderCfgModels()}}
 },
 cfgModels:function(d){
  CM=d.models||[];renderCfgModels();
  $('cfgMsg').textContent=d.ok?(CM.length?CM.length+' modelo(s) encontrado(s). Clique para escolher.':'Conectou, mas não há modelos instalados.'):'⚠️ Não consegui conectar: '+d.err;
 },
 cfgResult:function(d){if(d.ok){closeCfg();toast('Conexão salva')}else $('cfgMsg').textContent='⚠️ '+d.msg},
 chunk:function(c){
  if(!curMsg)curMsg=addMsg('bot','',0,true);
  initOut();q+=c;kick();
 },
 error:function(m){
  if(curMsg&&curMsg.dots){curMsg.remove();curMsg=null}
  addMsg('err',m,0)
 },
 needcfg:function(){__lc.needmodel()},
 needmodel:function(){
  if(curMsg){curMsg.remove();curMsg=null}
  var us=document.querySelectorAll('#msgs .m.user');showNM(lastQ,us[us.length-1]);
 },
 ai:function(d){S.ai=d;paintAi();renderStatus();paintNM()},
 aiDl:function(d){S.ai.dl=d.id;var nb=$('nmPb');if(nb){var np=d.total>0?Math.round(d.got*100/d.total):0;nb.style.width=np+'%';var ns=$('nmSt');if(ns)ns.textContent=np+'% · '+fmtGb(d.got)+(d.total?' de '+fmtGb(d.total):'')}
  var b=$('pb_'+d.id);if(b){var p=d.total>0?Math.round(d.got*100/d.total):0;b.style.width=p+'%';var t=$('pt_'+d.id);if(t)t.textContent=p+'% · '+fmtGb(d.got)+(d.total?' de '+fmtGb(d.total):'')}else paintAi()},
 engine:function(d){
  S.ai.phase=d.phase;S.ai.text=d.text;S.ai.pct=d.pct;paintEng();renderStatus();paintNM();
  // IA pronta: reenvia sozinho a pergunta que ficou esperando
  if(NM&&d.phase==='ready'){var n=NM;NM=null;n.el.remove();setEmpty();if(n.q&&!busy)setTimeout(function(){doSend(n.q)},250)}
 },
 aiMsg:function(m){toast(m)},
 done:function(d){fin=d||{};kick()}
};
send('lchat:init');
$('inp').focus();
</script></body></html>
""";
}

sealed record LChatPreset(string Id, string Name, string Base, bool Key, bool Local);

// ============================================================================
// Conversas e configuração do chat offline (arquivo próprio: não mexe no AppSettings)
// ============================================================================
sealed class LChatMsg
{
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";
    public long Time { get; set; }
}

sealed class LChatConv
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "Nova conversa";
    public List<LChatMsg> Messages { get; set; } = new();
    public long SiteU { get; set; }      // > 0 = conversa copiada do site (valor "u" do site na última cópia)
    public int SiteCount { get; set; }   // quantas mensagens vieram do site (o resto foi escrito offline)
}

sealed class LChatData
{
    public string Provider { get; set; } = "";
    public string Base { get; set; } = "";
    public string Key { get; set; } = "";
    public string Model { get; set; } = "";
    public string Theme { get; set; } = "auto";
    public string Cur { get; set; } = "";
    public List<LChatConv> Convs { get; set; } = new();

    // ---- copiado do site (com internet) ----
    public string Name { get; set; } = "";
    public string AvatarUrl { get; set; } = "";
    public string AvatarData { get; set; } = "";   // foto de perfil já baixada (data URL), aparece offline
    public string SiteTheme { get; set; } = "";    // último tema visto no site
    public bool AnimOff { get; set; }              // o site está com animações desligadas
    public string Tone { get; set; } = "";         // estilo de resposta escolhido no site
    public List<string> Topics { get; set; } = new();
    public string CuriosJson { get; set; } = "";   // curiosidades do site
    public bool PreferSite { get; set; }           // usar a IA configurada no site quando houver internet
    public string SiteProvider { get; set; } = "";
    public string SiteBase { get; set; } = "";
    public string SiteModel { get; set; } = "";
    public string SiteKey { get; set; } = "";

    // formato antigo (uma conversa só): migrado ao carregar
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<LChatMsg>? Messages { get; set; }

    static string FilePath => Path.Combine(MainForm.DataDir, "localchat.json");

    public static LChatData Load()
    {
        try
        {
            var d = JsonSerializer.Deserialize<LChatData>(File.ReadAllText(FilePath)) ?? new LChatData();
            d.Provider ??= ""; d.Base ??= ""; d.Key ??= ""; d.Model ??= ""; d.Theme ??= "auto"; d.Cur ??= "";
            d.Convs ??= new();
            d.Name ??= ""; d.AvatarUrl ??= ""; d.AvatarData ??= ""; d.SiteTheme ??= ""; d.Tone ??= ""; d.CuriosJson ??= "";
            d.SiteProvider ??= ""; d.SiteBase ??= ""; d.SiteModel ??= ""; d.SiteKey ??= ""; d.Topics ??= new();
            if (d.Messages is { Count: > 0 } && d.Convs.Count == 0)
            {
                var c = new LChatConv { Id = Guid.NewGuid().ToString("N")[..8], Title = "Conversa anterior", Messages = d.Messages };
                d.Convs.Add(c);
                d.Cur = c.Id;
            }
            d.Messages = null;
            return d;
        }
        catch { return new LChatData(); }
    }

    public void Save()
    {
        try
        {
            foreach (var c in Convs)
                if (c.Messages.Count > 300) c.Messages.RemoveRange(0, c.Messages.Count - 300);
            if (Convs.Count > 100) Convs.RemoveRange(100, Convs.Count - 100);
            Directory.CreateDirectory(MainForm.DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch { /* ignora */ }
    }
}

// ============================================================================
// Aparência, estilo, memória e aprendizado do chat offline (arquivo próprio: localchat-look.json)
// ============================================================================
sealed class LChatLike
{
    public string Q { get; set; } = "";
    public string A { get; set; } = "";
}

sealed class LChatLook
{
    public string Wall { get; set; } = "none";      // none, aurora, neon, lotus, sunset, forest, grafite, ceu, rosa, img
    public int Dim { get; set; } = 20;              // escurecer o fundo (0 a 80)
    public string Fx { get; set; } = "all";         // all, orbs, eq, off
    public bool Anim { get; set; } = true;          // animações da interface
    public bool AnimSet { get; set; }               // false = segue o site
    public bool Colors { get; set; } = true;        // cores do fundo mudando sozinhas
    public string WallImg { get; set; } = "";       // imagem própria (data URL)

    public string? Tone { get; set; }               // null = segue o site
    public List<string>? Topics { get; set; }       // null = segue o site
    public string Kb { get; set; } = "auto";        // "off" = sem os dados do bot Azul Groove
    public bool Deep { get; set; }
    public string Style { get; set; } = "";
    public List<string> Mem { get; set; } = new();
    public bool Learn { get; set; } = true;
    public List<LChatLike> Likes { get; set; } = new();

    static string FilePath => Path.Combine(MainForm.DataDir, "localchat-look.json");

    public void ResetLook()
    {
        Wall = "none"; Dim = 20; Fx = "all"; Anim = true; AnimSet = true; Colors = true; WallImg = "";
    }

    public static LChatLook Load()
    {
        try
        {
            var l = JsonSerializer.Deserialize<LChatLook>(File.ReadAllText(FilePath)) ?? new LChatLook();
            l.Wall ??= "none"; l.Fx ??= "all"; l.WallImg ??= ""; l.Kb ??= "auto"; l.Style ??= "";
            l.Mem ??= new(); l.Likes ??= new();
            return l;
        }
        catch { return new LChatLook(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(MainForm.DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch { /* ignora */ }
    }
}

static class LChatAssets
{
    // Logo da Azul Groove (flor de lótus, PNG 160x160 com fundo transparente) em base64
    public const string Lotus = "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";
}
