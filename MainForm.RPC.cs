using System.IO.Pipes;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Web.WebView2.Core;

namespace AzulGroove;

/// <summary>
/// Discord Rich Presence ("Ouvindo ...") dentro do Azul Groove, no mesmo padrão do RPC do MuseHeart
/// (rpc_client.py): NÃO precisa criar aplicativo no Discord nem subir imagens em "Art Assets".
///
///  - O app usa um APP_ID padrão (dummy). Quando a música vem do BOT, usa o ID do próprio bot (o nome
///    dele aparece em "Ouvindo ..."), a não ser que "Usar APP_ID customizado" esteja marcado.
///  - Todas as imagens (loop, pausa, stream, em espera, ícones das fontes) são LINKS.
///
/// A música atual pode vir de DUAS fontes (ligue uma, outra ou as duas):
///  1) O SITE do Azul Groove  -> a página manda  'rpc:{json}'  via window.chrome.webview.postMessage
///  2) O BOT de música        -> o app conecta no websocket do bot (mesmo protocolo do MuseHeart RPC) usando URL + token
///
/// A presence é enviada ao Discord pela pipe local "discord-ipc-N" (o Discord precisa estar aberto).
/// A configuração é salva em  %LocalAppData%\AzulGroove\rpc.json
/// </summary>
public partial class MainForm
{
    // Só o site oficial pode alimentar a presence pela página
    const string RpcSiteHost = "azul-groove.vercel.app";
    // Versão que o servidor do bot espera receber no "rpc_update" (a mesma do app MuseHeart)
    const string RpcProtocolVersion = "2.6.2";

    readonly RpcSettings rpcCfg = RpcSettings.Load();
    ToolStripMenuItem miRpc = null!, miRpcOn = null!, miRpcStatus = null!, miRpcCfg = null!;
    CancellationTokenSource? rpcCts;
    DiscordIpc? rpcIpc;                       // conexão principal (APP_ID padrão/customizado): usuário + site
    string rpcMainAppId = "";
    readonly Dictionary<string, DiscordIpc> rpcBots = new(); // uma conexão por bot (app id = id do bot)
    volatile string rpcStatus = "";
    volatile string rpcNow = "";   // o que está aparecendo na presence agora (mostrado na janela de configuração)

    void RpcApply(DiscordIpc ipc, JsonObject? activity)
    {
        try
        {
            rpcNow = activity == null ? "" :
                (activity["details"]?.ToString() ?? "") + ((activity["state"]?.ToString() ?? "").Length > 0 ? "  ·  " + activity["state"] : "");
        }
        catch { rpcNow = ""; }
        ipc.SetActivity(activity);
    }

    // limpa a presence da conexão principal e de todos os bots
    void RpcClearAll(DiscordIpc main)
    {
        RpcApply(main, null);
        DiscordIpc[] bots;
        lock (rpcBots) bots = rpcBots.Values.ToArray();
        foreach (var b in bots) { try { b.SetActivity(null); } catch { } }
    }

    // Conexão do Discord para um app id (cria na hora se ainda não existir)
    DiscordIpc? RpcIpcFor(string appId)
    {
        var main = rpcIpc;
        if (main == null) return null;
        if (appId == rpcMainAppId) return main;
        lock (rpcBots)
        {
            if (!rpcBots.TryGetValue(appId, out var ipc))
            {
                ipc = new DiscordIpc(appId);
                ipc.Start();
                rpcBots[appId] = ipc;
            }
            return ipc;
        }
    }

    // ================= Início / fim =================
    void RpcInit()
    {
        if (rpcCfg.On) RpcStart();
    }

    void RpcShutdown() => RpcStop();

    void RpcStart()
    {
        RpcStop();

        var cts = rpcCts = new CancellationTokenSource();
        rpcMainAppId = rpcCfg.EffectiveAppId;
        var ipc = rpcIpc = new DiscordIpc(rpcMainAppId);
        ipc.Status += s => rpcStatus = s.StartsWith("rpc.", StringComparison.Ordinal) ? Tr(s) : s;
        ipc.Ready += (id, name) => rpcStatus = TrF("rpc.connected", name);
        rpcStatus = Tr("rpc.search");
        ipc.Start();

        if (rpcCfg.WsUrl.Length > 0)
            _ = Task.Run(() => RpcWsLoopAsync(ipc, cts.Token));
    }

    void RpcStop()
    {
        try { rpcCts?.Cancel(); } catch { }
        rpcCts = null;

        DiscordIpc[] bots;
        lock (rpcBots) { bots = rpcBots.Values.ToArray(); rpcBots.Clear(); }
        foreach (var b in bots) { try { b.Close(); } catch { } }

        try { rpcIpc?.Close(); } catch { }
        rpcIpc = null;
        rpcNow = "";
    }

    // ================= Menu da bandeja =================
    ToolStripMenuItem RpcMenuItem()
    {
        miRpcOn = new ToolStripMenuItem(Tr("tray.rpc.on")) { CheckOnClick = true, Checked = rpcCfg.On };
        miRpcOn.CheckedChanged += (_, _) =>
        {
            rpcCfg.On = miRpcOn.Checked;
            rpcCfg.Save();
            if (!rpcCfg.On) { RpcStop(); rpcStatus = Tr("rpc.off"); return; }
            RpcStart();
        };

        miRpcCfg = new ToolStripMenuItem(Tr("tray.rpc.cfg"), null, (_, _) => ShowRpcSettings());
        miRpcStatus = new ToolStripMenuItem(TrF("tray.rpc.status", rpcStatus)) { Enabled = false };

        miRpc = new ToolStripMenuItem("Discord Rich Presence"); // nome do produto: igual em todos os idiomas
        miRpc.DropDownItems.AddRange(new ToolStripItem[] { miRpcOn, miRpcCfg, new ToolStripSeparator(), miRpcStatus });
        miRpc.DropDownOpening += (_, _) => miRpcStatus.Text = TrF("tray.rpc.status", rpcStatus); // atualiza o texto ao abrir o menu
        return miRpc;
    }

    // Troca ao vivo os textos do submenu quando o idioma muda (o texto do status em si é da etapa 3)
    void RpcTrayRetranslate()
    {
        if (miRpcOn == null) return;
        miRpcOn.Text = Tr("tray.rpc.on");
        miRpcCfg.Text = Tr("tray.rpc.cfg");
        miRpcStatus.Text = TrF("tray.rpc.status", rpcStatus);
    }

    // ================= Comandos do PAINEL local (liga/desliga e Configurar) =================
    //  'rpcui:on' | 'rpcui:off' | 'rpcui:config'  — só páginas locais (nunca o site https)
    bool HandleRpcPanelMessage(CoreWebView2WebMessageReceivedEventArgs e)
    {
        string? t;
        try { t = e.TryGetWebMessageAsString(); } catch { return false; }
        if (t == null || !t.StartsWith("rpcui:", StringComparison.Ordinal)) return false;
        if (e.Source.StartsWith("http", StringComparison.OrdinalIgnoreCase) || t.Length > 16000) return true;
        BeginInvoke(() => RpcUiDispatch(t)); // tratado em MainForm_RpcUi.cs
        return true;
    }

    // ================= Música vinda do SITE =================
    // A página manda:  window.chrome.webview.postMessage('rpc:' + JSON.stringify({ op:'update', track:{...} }))
    bool HandleRpcMessage(CoreWebView2WebMessageReceivedEventArgs e)
    {
        string? t;
        try { t = e.TryGetWebMessageAsString(); } catch { return false; }
        if (t == null || !t.StartsWith("rpc:", StringComparison.Ordinal)) return false;

        // só o site oficial (https) e só se o usuário deixou ligado
        if (!Uri.TryCreate(e.Source, UriKind.Absolute, out var src) ||
            src.Scheme != Uri.UriSchemeHttps ||
            !src.Host.Equals(RpcSiteHost, StringComparison.OrdinalIgnoreCase)) return true;
        var ipc = rpcIpc;
        if (!rpcCfg.On || !rpcCfg.AcceptSite || ipc == null || t.Length > 64 * 1024) return true;

        try
        {
            using var doc = JsonDocument.Parse(t[4..]);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return true;
            if (RpcMapper.TryBuild(doc.RootElement, rpcCfg, ipc.UserId, "", Tr, out var activity)) RpcApply(ipc, activity);
        }
        catch { /* mensagem inválida: ignora */ }
        return true;
    }

    // ================= Música vinda do BOT (websocket) =================
    async Task RpcWsLoopAsync(DiscordIpc ipc, CancellationToken ct)
    {
        int delay = 7;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!Uri.TryCreate(rpcCfg.WsUrl, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != "ws" && uri.Scheme != "wss"))
                {
                    rpcStatus = Tr("rpc.invalid");
                    return;
                }

                // Precisa do Discord aberto (para saber o ID do usuário). O modo offline do app NÃO bloqueia o RPC:
                // bot local (localhost / rede local) funciona sem internet; bot remoto só espera a rede voltar.
                while (ipc.UserId.Length == 0 || (!IsLocalHost(uri) && !NetworkUp())) await Task.Delay(1500, ct);

                using var ws = new ClientWebSocket();
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
                rpcStatus = Tr("rpc.connecting");
                using (var to = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    to.CancelAfter(TimeSpan.FromSeconds(20));
                    await ws.ConnectAsync(uri, to.Token);
                }

                var userIds = new JsonArray();
                if (ulong.TryParse(ipc.UserId, out var uid)) userIds.Add(uid); else userIds.Add(ipc.UserId);
                var hello = new JsonObject
                {
                    ["op"] = "rpc_update",
                    ["user_ids"] = userIds,
                    ["token"] = rpcCfg.Token,
                    ["version"] = RpcProtocolVersion
                };
                await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(hello.ToJsonString())),
                                   WebSocketMessageType.Text, true, ct);
                rpcStatus = Tr("rpc.botConnected");
                delay = 7;

                var buf = new byte[16 * 1024];
                using var ms = new MemoryStream();
                bool fatal = false, closed = false;
                while (!closed && !fatal && ws.State == WebSocketState.Open)
                {
                    ms.SetLength(0);
                    WebSocketReceiveResult r;
                    do
                    {
                        r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                        if (r.MessageType == WebSocketMessageType.Close) { closed = true; break; }
                        ms.Write(buf, 0, r.Count);
                        if (ms.Length > 1024 * 1024) throw new InvalidDataException("mensagem grande demais");
                    } while (!r.EndOfMessage);

                    if (!closed && r.MessageType == WebSocketMessageType.Text)
                        fatal = HandleRpcSocketMessage(ipc, Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length));
                }

                RpcClearAll(ipc);
                if (fatal) return; // o bot mandou "disconnect" (ex.: token inválido): não fica tentando de novo
                rpcStatus = TrF("rpc.botClosed", delay);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception)
            {
                RpcClearAll(ipc);
                rpcStatus = TrF("rpc.botUnavailable", delay);
            }

            try { await Task.Delay(TimeSpan.FromSeconds(delay), ct); }
            catch (OperationCanceledException) { return; }
            delay = Math.Min(delay * 2, 60);
        }
    }

    static bool IsLocalHost(Uri u)
    {
        if (u.IsLoopback) return true;
        if (!IPAddress.TryParse(u.Host, out var ip)) return u.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase);
        var b = ip.GetAddressBytes();
        return b.Length == 4 && (b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168));
    }

    // Retorna true se o servidor pediu para parar de vez
    bool HandleRpcSocketMessage(DiscordIpc ipc, string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            var op = RpcMapper.Str(root, "op");
            if (op == "disconnect")
            {
                rpcStatus = TrF("rpc.botStopped", RpcMapper.Str(root, "reason"));
                return true;
            }
            if (op == "exception")
            {
                rpcStatus = TrF("rpc.botError", RpcMapper.Str(root, "message"));
                return false;
            }

            // a mensagem tem que ser para o usuário do Discord que está aberto neste PC
            if (root.TryGetProperty("user", out var u) && u.ToString() != ipc.UserId) return false;

            // Como no RPC: o aplicativo é o do próprio bot (aparece "Ouvindo <nome do bot>"),
            // a não ser que "Usar APP_ID customizado" esteja marcado.
            var botId = RpcMapper.Id(root, "bot_id");
            var appId = !rpcCfg.OverrideAppId && RpcSettings.ValidAppId(botId) ? botId : rpcMainAppId;
            var target = RpcIpcFor(appId);
            if (target == null) return false;

            if (RpcMapper.TryBuild(root, rpcCfg, ipc.UserId, botId, Tr, out var activity)) RpcApply(target, activity);
        }
        catch { /* ignora mensagem quebrada */ }
        return false;
    }

    // ================= Janela de configuração =================
    // Testa o ID do aplicativo falando com o Discord de verdade (abre uma conexão rápida e fecha)
    async Task<(bool ok, string msg)> RpcTestAsync(string appId)
    {
        if (!RpcSettings.ValidAppId(appId)) return (false, "O ID deve ter só números (17 a 20 dígitos).");
        // já está rodando com esse mesmo ID: usa o estado real (uma 2ª conexão limparia a presence atual)
        if (rpcCfg.On && rpcIpc != null && appId == rpcMainAppId)
            return (rpcStatus == Tr("rpc.botConnected") ||
                rpcStatus.StartsWith(Tr("rpc.connected").Split('(')[0].TrimEnd(), StringComparison.Ordinal), rpcStatus);

        var ipc = new DiscordIpc(appId);
        var tcs = new TaskCompletionSource<(bool, string)>();
        ipc.Ready += (_, name) => tcs.TrySetResult((true, TrF("rpc.connected", name)));
        ipc.Status += st => tcs.TrySetResult((false, st));
        ipc.Start();
        try
        {
            var done = await Task.WhenAny(tcs.Task, Task.Delay(6000));
            return done == tcs.Task ? tcs.Task.Result : (false, "O Discord não respondeu. Ele está aberto neste PC?");
        }
        finally { await Task.Run(() => ipc.Close()); }
    }

}

// ============================================================================
// Configuração do RPC (arquivo próprio: não mexe no AppSettings)
// ============================================================================
sealed class RpcSettings
{
    // APP_ID padrão (o mesmo "dummy" do RPC do MuseHeart) e imagens padrão
    public const string DummyAppId = "921606662467498045";
    public const string DefLoop = "https://i.ibb.co/5Mj4HjT/loop-track.gif";
    public const string DefPause = "https://i.ibb.co/mDBMnH8/pause.png";
    public const string DefStream = "https://i.ibb.co/Qf9BSQb/stream.png";
    public const string DefIdle = "https://i.ibb.co/6XS6qLy/music-img.png";

    public bool On { get; set; }
    public string AppId { get; set; } = DummyAppId;
    public bool OverrideAppId { get; set; }            // "Usar APP_ID customizado"
    public string WsUrl { get; set; } = "";
    public string Token { get; set; } = "";

    public bool AcceptSite { get; set; } = true;
    public bool ShowListenButton { get; set; } = true;
    public bool ShowListenAlong { get; set; } = false;
    public bool ShowPlaylistButton { get; set; } = true;
    public bool PlaylistRefs { get; set; } = true;     // mantém "&list=" no link do botão
    public bool ShowThumbnail { get; set; } = true;
    public bool ShowQueue { get; set; } = true;        // "Na fila: N"
    public bool BlockOtherUsers { get; set; } = false; // ignora músicas pedidas por outros membros
    public bool BotInvite { get; set; } = true;        // botão de convite quando o bot está em espera

    public string LoopImg { get; set; } = DefLoop;
    public string LoopQueueImg { get; set; } = DefLoop;
    public string PauseImg { get; set; } = DefPause;
    public string StreamImg { get; set; } = DefStream;
    public string IdleImg { get; set; } = DefIdle;

    // ID realmente usado pela conexão principal
    [JsonIgnore]
    public string EffectiveAppId => OverrideAppId && ValidAppId(AppId) ? AppId : DummyAppId;

    static string FilePath => Path.Combine(MainForm.DataDir, "rpc.json");

    public static bool ValidAppId(string? s) =>
        !string.IsNullOrEmpty(s) && s.Length is >= 17 and <= 20 && s.All(char.IsAsciiDigit);

    public static string Or(string? v, string def) => string.IsNullOrWhiteSpace(v) ? def : v.Trim();

    public static RpcSettings Load()
    {
        try
        {
            var text = File.ReadAllText(FilePath);
            var s = JsonSerializer.Deserialize<RpcSettings>(text) ?? new RpcSettings();
            s.WsUrl ??= ""; s.Token ??= "";

            if (!ValidAppId(s.AppId)) s.AppId = DummyAppId;
            // configuração antiga (antes do APP_ID padrão): quem já tinha um ID próprio continua usando ele
            else if (!text.Contains("\"OverrideAppId\"") && s.AppId != DummyAppId) s.OverrideAppId = true;

            s.LoopImg = Or(s.LoopImg, DefLoop);
            s.LoopQueueImg = Or(s.LoopQueueImg, DefLoop);
            s.PauseImg = Or(s.PauseImg, DefPause);
            s.StreamImg = Or(s.StreamImg, DefStream);
            s.IdleImg = Or(s.IdleImg, DefIdle);
            return s;
        }
        catch { return new RpcSettings(); }
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

// ============================================================================
// Converte a mensagem (site ou bot) em uma "activity" do Discord — mesma lógica do rpc_client.py
//   {"op":"update","track":{title,author,thumb,url,duration,position,paused,stream,source,loop,queue,"247",
//                           playlist_url,playlist_name,album_url,album_name,requester_id}, "thumb", "listen_along_invite"}
//   {"op":"idle", thumb, public, support_server, invite_permissions, idle_endtime}      {"op":"close"}
// ============================================================================
static class RpcMapper
{
    const string CdGif = "https://i.ibb.co/qD5gvKR/cd.gif";

    // Ícones das fontes (links) — iguais aos do RPC
    static readonly Dictionary<string, string> SourceIcons = new()
    {
        ["deezer"] = "https://i.ibb.co/zxpBbp8/deezer.png",
        ["soundcloud"] = "https://i.ibb.co/CV6NB6w/soundcloud.png",
        ["spotify"] = "https://i.ibb.co/3SWMXj8/spotify.png",
        ["youtube"] = "https://i.ibb.co/LvX7dQL/yt.png",
        ["twitch"] = "https://cdn3.iconfinder.com/data/icons/popular-services-brands-vol-2/512/twitch-512.png",
    };

    // A fonte vem do campo "source" da música; se não vier (site), deduz pelo link (url) ou pela capa (thumb).
    static readonly (string Key, string[] Hints)[] SourceHints =
    {
        ("youtube", new[] { "youtube", "youtu.be", "ytimg", "ytmusic" }),
        ("spotify", new[] { "spotify", "scdn.co" }),
        ("soundcloud", new[] { "soundcloud", "sndcdn" }),
        ("deezer", new[] { "deezer", "dzcdn" }),
        ("twitch", new[] { "twitch" }),
    };

    static string? DetectSource(string source, string? link, string? thumb)
    {
        var hay = (source + " " + link + " " + thumb).ToLowerInvariant();
        foreach (var (key, hints) in SourceHints)
            foreach (var h in hints)
                if (hay.Contains(h)) return key;
        return null;
    }

    // userId = usuário do Discord aberto neste PC; botId = bot que mandou a mensagem ("" se veio do site)
    public static bool TryBuild(JsonElement msg, RpcSettings cfg, string userId, string botId, Func<string, string> tr, out JsonObject? activity)
    {
        activity = null;
        switch (Str(msg, "op"))
        {
            case "close":
                return true; // activity null = limpa a presence
            case "idle":
                activity = BuildIdle(msg, cfg, botId, tr);
                return true;
            case "update":
                break;
            default:
                return false;
        }

        if (!msg.TryGetProperty("track", out var t) || t.ValueKind != JsonValueKind.Object) return false;

        // música adicionada por outro membro: limpa a presence (opção do RPC)
        if (cfg.BlockOtherUsers && userId.Length > 0)
        {
            var req = Id(t, "requester_id");
            if (req.Length > 0 && req != userId) return true;
        }

        var title = Clean(Str(t, "title"));
        var author = Clean(Str(t, "author"));
        var sourceRaw = Str(t, "source");
        bool paused = Bool(t, "paused"), stream = Bool(t, "stream");
        double dur = Num(t, "duration"), pos = Num(t, "position");

        var link = HttpUrl(Str(t, "url"), 512);
        var trackThumbRaw = Str(t, "thumb");
        var srcKey = DetectSource(sourceRaw, link, trackThumbRaw);

        // ---- imagens ----
        var assets = new JsonObject { ["small_image"] = CdGif };
        var large = HttpUrl(Str(msg, "thumb"), 256);
        var trackThumb = HttpUrl(trackThumbRaw, 256)?.Replace("mqdefault", "default");
        if (cfg.ShowThumbnail && trackThumb != null) large = trackThumb;
        assets["large_image"] = large ?? cfg.IdleImg;

        string small = CdGif, smallText = "";
        if (stream)
        {
            if (srcKey == "twitch") { small = SourceIcons["twitch"]; smallText = "Twitch: " + tr("s3.actLive"); }
            else { small = cfg.StreamImg; smallText = tr("s3.actLive"); }
        }

        if (paused)
        {
            small = cfg.PauseImg;
            smallText = tr("s3.actPaused");
        }
        else if (!stream)
        {
            if (LoopText(t, tr, out var loopText, out var isQueue))
            {
                small = isQueue ? cfg.LoopQueueImg : cfg.LoopImg;
                smallText = loopText;
            }
            else
            {
                if (srcKey != null) small = SourceIcons[srcKey];
                smallText = sourceRaw.Length > 0 ? sourceRaw : srcKey ?? "";
            }
        }
        assets["small_image"] = small;
        if (smallText.Length >= 2) assets["small_text"] = smallText;

        // ---- barra de progresso ----
        var ts = new JsonObject();
        if (!paused)
        {
            var now = DateTimeOffset.UtcNow;
            if (!stream && dur > 0)
            {
                pos = Math.Clamp(pos, 0, dur);
                ts["start"] = now.AddMilliseconds(-pos).ToUnixTimeSeconds();
                ts["end"] = now.AddMilliseconds(dur - pos).ToUnixTimeSeconds();
            }
            else ts["start"] = now.ToUnixTimeSeconds();
        }

        // ---- texto + botões ----
        var buttons = new List<JsonObject>();
        if (link != null && cfg.ShowListenButton)
        {
            var u = cfg.PlaylistRefs ? link : link.Split("&list=")[0];
            buttons.Add(Button(tr("s3.actListen"), u.Replace("www.", "")));
        }

        var state = author.Length > 0 ? tr("s3.actBy") + ": " + author : "Azul Groove";

        var pName = Clean(Str(t, "playlist_name"));
        var pUrl = HttpUrl(Str(t, "playlist_url"), 512);
        var aName = Clean(Str(t, "album_name"));
        var aUrl = HttpUrl(Str(t, "album_url"), 512);
        var desc = new List<string>();

        if (cfg.ShowPlaylistButton)
        {
            if (pName.Length > 0 && pUrl != null)
            {
                if (pName.Length > 32)
                {
                    state += " | " + tr("s3.actPlaylist") + ": " + pName;
                    buttons.Add(Button(tr("s3.actViewPlaylist"), pUrl.Replace("www.", "")));
                }
                else
                {
                    if (pName.Length < 23) { pName = tr("s3.actPlaylist") + ": " + pName; desc.Add(pName); }
                    else desc.Add(tr("s3.actPlaylist") + ": " + pName);
                    buttons.Add(Button(pName, pUrl.Replace("www.", "")));
                }
            }
            else if (pName.Length > 0) desc.Add(tr("s3.actPlaylist") + ": " + pName);
        }

        if (aUrl != null)
        {
            var albumTxt = tr("s3.actAlbum") + ": " + aName;
            if (buttons.Count < 2)
                buttons.Add(aName.Length > 32 ? Button(tr("s3.actViewAlbum"), aUrl.Replace("www.", "")) : Button(albumTxt, aUrl));
            desc.Add(albumTxt);
        }

        if (Bool(t, "247")) state += " | ✅24/7";
        int queue = (int)Num(t, "queue");
        if (queue > 0 && cfg.ShowQueue) state += $" | {tr("s3.actQueue")}: {queue}";

        if (desc.Count > 0)
        {
            var lt = string.Join(" | ", desc);
            assets["large_text"] = lt.Length > 128 ? lt[..127] + "…" : lt;
        }

        // convite da call (ouvir junto): vai na frente; se já tem 2 botões, sai o último
        var along = HttpUrl(Str(msg, "listen_along_invite"), 512);
        if (cfg.ShowListenAlong && along != null)
        {
            if (buttons.Count > 1) buttons.RemoveAt(buttons.Count - 1);
            buttons.Insert(0, Button(tr("s3.actListenTogether"), along));
        }

        activity = new JsonObject
        {
            ["type"] = 2, // "Ouvindo"
            ["details"] = Cut(title, 128, tr("s3.actNowPlaying")),
            ["state"] = Cut(state, 128, "Azul Groove"),
            ["assets"] = assets
        };
        if (ts.Count > 0) activity["timestamps"] = ts;

        if (buttons.Count > 0)
        {
            var arr = new JsonArray();
            foreach (var b in buttons.Take(2)) arr.Add(b); // o Discord aceita no máximo 2 botões
            activity["buttons"] = arr;
        }

        return true;
    }

    // Presence "em espera" (bot sem música): "Aguardando por novas músicas..." + convite do bot
    static JsonObject BuildIdle(JsonElement msg, RpcSettings cfg, string botId, Func<string, string> tr)
    {
        var rootThumb = HttpUrl(Str(msg, "thumb"), 256);
        var assets = new JsonObject
        {
            ["small_image"] = CdGif,
            // com APP_ID customizado usa a imagem de "Em espera"; senão a capa/avatar do bot
            ["large_image"] = cfg.OverrideAppId ? cfg.IdleImg : (rootThumb ?? cfg.IdleImg)
        };

        var act = new JsonObject
        {
            ["type"] = 0,
            ["details"] = tr("s3.actWaiting"),
            ["state"] = tr("s3.actNewTracks"),
            ["assets"] = assets
        };

        if (msg.TryGetProperty("idle_endtime", out var ie) && ie.ValueKind == JsonValueKind.Number && ie.TryGetInt64(out var end))
            act["timestamps"] = new JsonObject { ["end"] = end };

        bool isPublic = !(msg.TryGetProperty("public", out var pub) && pub.ValueKind == JsonValueKind.False);
        if (isPublic && cfg.BotInvite && botId.Length > 0)
        {
            long perms = 8;
            if (msg.TryGetProperty("invite_permissions", out var ip) && ip.ValueKind == JsonValueKind.Number && ip.TryGetInt64(out var pv))
                perms = pv;

            var buttons = new JsonArray
            {
                Button(tr("s3.actInviteBot"),
                       $"https://discord.com/api/oauth2/authorize?client_id={botId}&permissions={perms}&scope=bot%20applications.commands")
            };
            var support = HttpUrl(Str(msg, "support_server"), 512);
            if (support != null) buttons.Add(Button(tr("s3.actSupport"), support));
            act["buttons"] = buttons;
        }
        return act;
    }

    // "loop" pode ser: "queue" | true | número (repetições restantes) | [atual, total]
    static bool LoopText(JsonElement track, Func<string, string> tr, out string text, out bool isQueue)
    {
        text = ""; isQueue = false;
        if (!track.TryGetProperty("loop", out var lp)) return false;

        bool truthy = lp.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => (lp.GetString() ?? "").Length > 0,
            JsonValueKind.Number => lp.TryGetDouble(out var d) && d != 0,
            JsonValueKind.Array => lp.GetArrayLength() > 0,
            _ => false
        };
        if (!truthy) return false;

        if (lp.ValueKind == JsonValueKind.String && lp.GetString() == "queue")
        {
            text = tr("s3.actQueueLoop");
            isQueue = true;
        }
        else if (lp.ValueKind == JsonValueKind.Array && lp.GetArrayLength() >= 2)
            text = string.Format(System.Globalization.CultureInfo.InvariantCulture, tr("s3.actLoopEnabled"), lp[0], lp[1]);
        else if (lp.ValueKind == JsonValueKind.Number)
            text = string.Format(System.Globalization.CultureInfo.InvariantCulture, tr("s3.actLoopCount"), lp.GetRawText());
        else
            text = tr("s3.actLoop");
        return true;
    }

    // ---- helpers ----
    public static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    // id pode vir como número, texto ou lista (usa o primeiro)
    public static string Id(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return "";
        if (v.ValueKind == JsonValueKind.Array)
        {
            if (v.GetArrayLength() == 0) return "";
            v = v[0];
        }
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.String => v.GetString() ?? "",
            _ => ""
        };
    }

    static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    static double Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : 0;

    static string Clean(string s) => WebUtility.HtmlDecode(s).Replace("\r", " ").Replace("\n", " ").Trim();

    // O Discord exige de 2 a 128 caracteres em details/state
    static string Cut(string s, int max, string fallback)
    {
        s = s.Trim();
        if (s.Length < 2) return fallback;
        return s.Length > max ? s[..(max - 1)] + "…" : s;
    }

    // O Discord limita o texto do botão a 32 caracteres
    static JsonObject Button(string label, string url) => new()
    {
        ["label"] = label.Length > 32 ? label[..31] + "…" : label,
        ["url"] = url
    };

    static string? HttpUrl(string s, int maxLen) =>
        s.Length is > 0 && s.Length <= maxLen &&
        Uri.TryCreate(s, UriKind.Absolute, out var u) &&
        (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps) ? s : null;
}

// ============================================================================
// Cliente do IPC do Discord (pipe local "discord-ipc-0..9")
//   frame = [int32 opcode][int32 tamanho][json utf-8]   (little-endian)
//   opcodes: 0 handshake, 1 frame, 2 close, 3 ping, 4 pong
// Reconecta sozinho se o Discord for fechado/aberto depois e reenvia a última atividade.
// ============================================================================
sealed class DiscordIpc
{
    readonly string appId;
    readonly CancellationTokenSource cts = new();
    readonly SemaphoreSlim sendLock = new(1, 1);
    NamedPipeClientStream? pipe;
    JsonObject? last;                  // última atividade (reenviada ao reconectar)
    int version;                       // descarta atualizações antigas (limite de taxa do Discord)
    long lastSendTicks;
    string failMsg = "";

    public volatile string UserId = "";
    public event Action<string>? Status;
    public event Action<string, string>? Ready; // (userId, userName)

    public DiscordIpc(string appId) => this.appId = appId;

    // Chame depois de assinar Status/Ready, para não perder nenhum evento
    public void Start() => _ = Task.Run(RunAsync);

    bool Connected => UserId.Length > 0 && pipe is { IsConnected: true };

    // activity == null limpa a presence
    public void SetActivity(JsonObject? activity)
    {
        last = activity;
        var v = Interlocked.Increment(ref version);
        _ = Task.Run(() => PushAsync(v));
    }

    public void Close()
    {
        try
        {
            if (Connected)
            {
                var cmd = new JsonObject
                {
                    ["cmd"] = "SET_ACTIVITY",
                    ["args"] = new JsonObject { ["pid"] = Environment.ProcessId },
                    ["nonce"] = Guid.NewGuid().ToString()
                };
                // Task.Run: evita travar a tela (thread da UI) esperando a si mesma
                Task.Run(() => WriteFrameAsync(1, cmd.ToJsonString(), CancellationToken.None)).Wait(500);
            }
        }
        catch { }
        try { cts.Cancel(); } catch { }
        DropPipe();
    }

    async Task RunAsync()
    {
        var ct = cts.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (await ConnectAsync(ct)) await ReadLoopAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch { }

            DropPipe();
            UserId = "";
            Status?.Invoke(failMsg.Length > 0 ? failMsg : "rpc.notfound");
            try { await Task.Delay(TimeSpan.FromSeconds(10), ct); } catch { break; }
        }
    }

    async Task<bool> ConnectAsync(CancellationToken ct)
    {
        failMsg = "";
        for (int i = 0; i < 10; i++)
        {
            var p = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                using var to = CancellationTokenSource.CreateLinkedTokenSource(ct);
                to.CancelAfter(500);
                await p.ConnectAsync(to.Token);
            }
            catch
            {
                p.Dispose();
                ct.ThrowIfCancellationRequested();
                continue; // essa pipe não existe: tenta a próxima
            }

            pipe = p;
            try
            {
                await WriteFrameAsync(0, new JsonObject { ["v"] = 1, ["client_id"] = appId }.ToJsonString(), ct);
                var (_, json) = await ReadFrameAsync(ct);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("evt", out var evt) && evt.GetString() == "READY")
                {
                    var user = root.GetProperty("data").GetProperty("user");
                    var id = user.GetProperty("id").GetString() ?? "";
                    var name = user.TryGetProperty("username", out var un) ? un.GetString() ?? "" : "";
                    UserId = id;
                    Ready?.Invoke(id, name);
                    SetActivity(last); // reenvia o que estava tocando
                    return true;
                }

                failMsg = "O Discord recusou o ID do aplicativo — confira em Configurar…";
            }
            catch (OperationCanceledException) { throw; }
            catch { }

            DropPipe();
            return false;
        }
        return false;
    }

    async Task ReadLoopAsync(CancellationToken ct)
    {
        while (true)
        {
            var (op, json) = await ReadFrameAsync(ct);
            if (op == 2) throw new IOException("Discord fechou a conexão");
            if (op == 3) await WriteFrameAsync(4, json, ct); // PING -> PONG
        }
    }

    async Task PushAsync(int v)
    {
        try
        {
            // O Discord limita a ~5 atualizações a cada 20 s: espera um pouco e manda só a mais recente
            var wait = TimeSpan.FromTicks(Interlocked.Read(ref lastSendTicks)) + TimeSpan.FromSeconds(4) - TimeSpan.FromTicks(DateTime.UtcNow.Ticks);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, cts.Token);
            if (v != Volatile.Read(ref version) || !Connected) return;

            Interlocked.Exchange(ref lastSendTicks, DateTime.UtcNow.Ticks);
            var args = new JsonObject { ["pid"] = Environment.ProcessId };
            var act = last;
            if (act != null) args["activity"] = act.DeepClone();
            var cmd = new JsonObject { ["cmd"] = "SET_ACTIVITY", ["args"] = args, ["nonce"] = Guid.NewGuid().ToString() };
            await WriteFrameAsync(1, cmd.ToJsonString(), cts.Token);
        }
        catch { /* cancelado ou pipe caiu: o loop principal reconecta */ }
    }

    // ---- frames ----
    async Task WriteFrameAsync(int op, string json, CancellationToken ct)
    {
        var p = pipe ?? throw new IOException("sem conexão");
        var body = Encoding.UTF8.GetBytes(json);
        var buf = new byte[8 + body.Length];
        BitConverter.TryWriteBytes(buf.AsSpan(0, 4), op);
        BitConverter.TryWriteBytes(buf.AsSpan(4, 4), body.Length);
        body.CopyTo(buf, 8);

        await sendLock.WaitAsync(ct);
        try
        {
            await p.WriteAsync(buf, ct);
            await p.FlushAsync(ct);
        }
        finally { sendLock.Release(); }
    }

    async Task<(int op, string json)> ReadFrameAsync(CancellationToken ct)
    {
        var head = new byte[8];
        await ReadExactAsync(head, ct);
        int op = BitConverter.ToInt32(head, 0), len = BitConverter.ToInt32(head, 4);
        if (len < 0 || len > 1024 * 1024) throw new IOException("frame inválido");
        var body = new byte[len];
        await ReadExactAsync(body, ct);
        return (op, Encoding.UTF8.GetString(body));
    }

    async Task ReadExactAsync(byte[] buf, CancellationToken ct)
    {
        var p = pipe ?? throw new IOException("sem conexão");
        int n = 0;
        while (n < buf.Length)
        {
            int r = await p.ReadAsync(buf.AsMemory(n), ct);
            if (r == 0) throw new EndOfStreamException();
            n += r;
        }
    }

    void DropPipe()
    {
        var p = Interlocked.Exchange(ref pipe, null);
        try { p?.Dispose(); } catch { }
    }
}
