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
    ToolStripMenuItem miRpc = null!, miRpcOn = null!, miRpcStatus = null!;
    CancellationTokenSource? rpcCts;
    DiscordIpc? rpcIpc;                       // conexão principal (APP_ID padrão/customizado): usuário + site
    string rpcMainAppId = "";
    readonly Dictionary<string, DiscordIpc> rpcBots = new(); // uma conexão por bot (app id = id do bot)
    volatile string rpcStatus = "Desligado";
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
        ipc.Status += s => rpcStatus = s;
        ipc.Ready += (id, name) => rpcStatus = $"Conectado ao Discord ({name})";
        rpcStatus = "Procurando o Discord…";
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
        miRpcOn = new ToolStripMenuItem("Ativar Rich Presence") { CheckOnClick = true, Checked = rpcCfg.On };
        miRpcOn.CheckedChanged += (_, _) =>
        {
            rpcCfg.On = miRpcOn.Checked;
            rpcCfg.Save();
            if (!rpcCfg.On) { RpcStop(); rpcStatus = "Desligado"; return; }
            RpcStart();
        };

        var miCfg = new ToolStripMenuItem("Configurar…", null, (_, _) => ShowRpcSettings());
        miRpcStatus = new ToolStripMenuItem("Status: " + rpcStatus) { Enabled = false };

        miRpc = new ToolStripMenuItem("Discord Rich Presence");
        miRpc.DropDownItems.AddRange(new ToolStripItem[] { miRpcOn, miCfg, new ToolStripSeparator(), miRpcStatus });
        miRpc.DropDownOpening += (_, _) => miRpcStatus.Text = "Status: " + rpcStatus; // atualiza o texto ao abrir o menu
        return miRpc;
    }

    // ================= Comandos do PAINEL local (liga/desliga e Configurar) =================
    //  'rpcui:on' | 'rpcui:off' | 'rpcui:config'  — só páginas locais (nunca o site https)
    bool HandleRpcPanelMessage(CoreWebView2WebMessageReceivedEventArgs e)
    {
        string? t;
        try { t = e.TryGetWebMessageAsString(); } catch { return false; }
        if (t == null || !t.StartsWith("rpcui:", StringComparison.Ordinal)) return false;
        if (e.Source.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return true;

        BeginInvoke(() =>
        {
            switch (t)
            {
                case "rpcui:on": if (!miRpcOn.Checked) miRpcOn.Checked = true; break;
                case "rpcui:off": if (miRpcOn.Checked) miRpcOn.Checked = false; break;
                case "rpcui:config": ShowRpcSettings(); break;
            }
            // redesenha o bloco do painel com o estado real (ex.: o usuário cancelou a janela)
            if (panelActive && !IsDisposed) _ = BetaInjectPanelAsync();
        });
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
            if (RpcMapper.TryBuild(doc.RootElement, rpcCfg, ipc.UserId, "", out var activity)) RpcApply(ipc, activity);
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
                    rpcStatus = "Link do websocket inválido (use ws:// ou wss://)";
                    return;
                }

                // Precisa do Discord aberto (para saber o ID do usuário). O modo offline do app NÃO bloqueia o RPC:
                // bot local (localhost / rede local) funciona sem internet; bot remoto só espera a rede voltar.
                while (ipc.UserId.Length == 0 || (!IsLocalHost(uri) && !NetworkUp())) await Task.Delay(1500, ct);

                using var ws = new ClientWebSocket();
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
                rpcStatus = "Conectando ao bot…";
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
                rpcStatus = "Conectado ao Discord e ao bot";
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
                rpcStatus = $"Conexão com o bot encerrada — nova tentativa em {delay}s";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception)
            {
                RpcClearAll(ipc);
                rpcStatus = $"Bot indisponível — nova tentativa em {delay}s";
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
                rpcStatus = "O bot encerrou a conexão: " + RpcMapper.Str(root, "reason");
                return true;
            }
            if (op == "exception")
            {
                rpcStatus = "Erro do bot: " + RpcMapper.Str(root, "message");
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

            if (RpcMapper.TryBuild(root, rpcCfg, ipc.UserId, botId, out var activity)) RpcApply(target, activity);
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
            return (rpcStatus.StartsWith("Conectado"), rpcStatus);

        var ipc = new DiscordIpc(appId);
        var tcs = new TaskCompletionSource<(bool, string)>();
        ipc.Ready += (_, name) => tcs.TrySetResult((true, "Tudo certo! Conectado ao Discord como " + name + "."));
        ipc.Status += st => tcs.TrySetResult((false, st));
        ipc.Start();
        try
        {
            var done = await Task.WhenAny(tcs.Task, Task.Delay(6000));
            return done == tcs.Task ? tcs.Task.Result : (false, "O Discord não respondeu. Ele está aberto neste PC?");
        }
        finally { await Task.Run(() => ipc.Close()); }
    }

    void ShowRpcSettings()
    {
        bool dark = cfg.Theme == 1 ? false : cfg.Theme == 2 ? true : WindowsIsDark();
        var bg = dark ? Color.FromArgb(24, 27, 36) : Color.FromArgb(246, 248, 252);
        var card = dark ? Color.FromArgb(31, 35, 48) : Color.White;
        var fg = dark ? Color.FromArgb(235, 238, 247) : Color.FromArgb(28, 32, 48);
        var mute = dark ? Color.FromArgb(150, 158, 180) : Color.FromArgb(100, 108, 130);
        var fieldBg = dark ? Color.FromArgb(40, 45, 62) : Color.FromArgb(240, 243, 249);
        var line = dark ? Color.FromArgb(48, 54, 72) : Color.FromArgb(226, 230, 240);
        var accent = Color.FromArgb(88, 135, 255);

        using var f = new Form
        {
            Text = "Discord Rich Presence",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            MaximizeBox = false, MinimizeBox = false, TopMost = true,
            ClientSize = new Size(500, 542),
            BackColor = bg, ForeColor = fg,
            Font = new Font("Segoe UI", 9.5f),
            Icon = Icon
        };

        Label Txt(string t, int x, int y, int w, int h, Color c, float size = 9.5f, bool bold = false) => new()
        {
            Text = t, Left = x, Top = y, Width = w, Height = h, ForeColor = c, BackColor = Color.Transparent,
            Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular)
        };
        // Campo sem borda preta: um painel colorido com o TextBox dentro
        TextBox Field(Control parent, int x, int y, int w, string value, bool secret = false)
        {
            var wrap = new Panel { Left = x, Top = y, Width = w, Height = 30, BackColor = fieldBg };
            var tb = new TextBox
            {
                Left = 10, Top = 6, Width = w - 20, Text = value, BackColor = fieldBg, ForeColor = fg,
                BorderStyle = BorderStyle.None, UseSystemPasswordChar = secret
            };
            wrap.Controls.Add(tb);
            wrap.Click += (_, _) => tb.Focus();
            parent.Controls.Add(wrap);
            return tb;
        }
        Button Btn(string t, int x, int y, int w, bool primary)
        {
            var b = new Button
            {
                Text = t, Left = x, Top = y, Width = w, Height = 30, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
                BackColor = primary ? accent : fieldBg, ForeColor = primary ? Color.White : fg
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(112, 152, 255) : line;
            return b;
        }
        CheckBox Opt(Control parent, string t, bool v, int y)
        {
            var cb = new CheckBox { Text = t, Left = 20, Top = y, Width = 428, Height = 26, Checked = v, ForeColor = fg, BackColor = Color.Transparent };
            parent.Controls.Add(cb);
            return cb;
        }
        void Divider(Control parent, int y) =>
            parent.Controls.Add(new Panel { Left = 20, Top = y, Width = 428, Height = 1, BackColor = line });

        // ---- Estado ao vivo (sempre visível no topo) ----
        var st = new Panel { Left = 16, Top = 14, Width = 468, Height = 64, BackColor = card };
        f.Controls.Add(st);
        var dot = new Label { Left = 20, Top = 15, Width = 12, Height = 12, BackColor = Color.Gray };
        var gp = new System.Drawing.Drawing2D.GraphicsPath();
        gp.AddEllipse(0, 0, 12, 12);
        dot.Region = new Region(gp);
        var stTxt = Txt("", 42, 9, 410, 22, fg, 10f, true);
        var nowTxt = Txt("", 42, 34, 410, 22, mute);
        st.Controls.AddRange(new Control[] { dot, stTxt, nowTxt });
        void RefreshState()
        {
            var s = rpcStatus;
            dot.BackColor = s.StartsWith("Conectado") ? Color.FromArgb(67, 181, 129)
                : s == "Desligado" ? Color.Gray
                : s.StartsWith("Procurando") || s.StartsWith("Conectando") ? Color.FromArgb(240, 178, 50)
                : Color.FromArgb(237, 66, 69);
            stTxt.Text = s;
            nowTxt.Text = rpcNow.Length > 0 ? "♪  " + rpcNow : "Nada tocando na presence agora";
        }
        RefreshState();
        var tm = new System.Windows.Forms.Timer { Interval = 1000 };
        tm.Tick += (_, _) => RefreshState();
        tm.Start();

        // ---- Abas (sem rolagem) ----
        var tabsBar = new Panel { Left = 16, Top = 88, Width = 468, Height = 36, BackColor = bg };
        f.Controls.Add(tabsBar);
        var underline = new Panel { Top = 33, Height = 3, Width = 156, BackColor = accent };
        string[] tabNames = { "Conexão", "Opções", "Imagens" };
        var tabBtns = new Button[3];
        var pages = new Panel[3];
        for (int i = 0; i < 3; i++)
        {
            var tabBtn = new Button
            {
                Text = tabNames[i], Left = i * 156, Top = 0, Width = 156, Height = 33, FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand, BackColor = bg, ForeColor = mute, TabStop = false,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold)
            };
            tabBtn.FlatAppearance.BorderSize = 0;
            tabBtn.FlatAppearance.MouseOverBackColor = card;
            tabBtn.FlatAppearance.MouseDownBackColor = card;
            tabBtns[i] = tabBtn;
            tabsBar.Controls.Add(tabBtn);

            var pg = new Panel { Left = 16, Top = 124, Width = 468, Height = 346, BackColor = card, Visible = false };
            pages[i] = pg;
            f.Controls.Add(pg);
        }
        tabsBar.Controls.Add(underline);
        underline.BringToFront();
        void SelectTab(int idx)
        {
            for (int j = 0; j < 3; j++)
            {
                pages[j].Visible = j == idx;
                tabBtns[j].ForeColor = j == idx ? fg : mute;
            }
            underline.Left = idx * 156;
        }
        for (int i = 0; i < 3; i++) { int k = i; tabBtns[k].Click += (_, _) => SelectTab(k); }

        // ================= Aba 1 · Conexão =================
        var pg1 = pages[0];
        pg1.Controls.Add(Txt("Aplicativo do Discord", 20, 14, 300, 22, fg, 10f, true));
        var lnkPortal = new LinkLabel
        {
            Text = "Abrir portal", Left = 348, Top = 14, Width = 100, Height = 22, TextAlign = ContentAlignment.MiddleRight,
            LinkColor = accent, ActiveLinkColor = accent, BackColor = Color.Transparent
        };
        lnkPortal.Click += (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://discord.com/developers/applications") { UseShellExecute = true }); } catch { }
        };
        pg1.Controls.Add(lnkPortal);
        pg1.Controls.Add(Txt("Não precisa configurar nada: o app já usa um ID padrão. Só use o seu se quiser — " +
                             "o nome do aplicativo aparece como “Ouvindo …”.", 20, 38, 428, 36, mute, 8.75f));
        var cbOver = Opt(pg1, "Usar APP_ID customizado", rpcCfg.OverrideAppId, 78);
        var tbApp = Field(pg1, 20, 106, 428, rpcCfg.AppId);
        void ApplyAppState() { tbApp.ReadOnly = !cbOver.Checked; tbApp.ForeColor = cbOver.Checked ? fg : mute; }
        ApplyAppState();
        cbOver.CheckedChanged += (_, _) => ApplyAppState();
        var btnTest = Btn("Testar conexão", 20, 146, 130, true);
        var testRes = Txt("", 160, 151, 288, 22, mute, 8.75f);
        pg1.Controls.AddRange(new Control[] { btnTest, testRes });
        btnTest.Click += async (_, _) =>
        {
            btnTest.Enabled = false; testRes.ForeColor = mute; testRes.Text = "Testando…";
            var id = cbOver.Checked ? tbApp.Text.Trim().Replace(" ", "") : RpcSettings.DummyAppId;
            var (ok, msg) = await RpcTestAsync(id);
            if (f.IsDisposed) return;
            testRes.ForeColor = ok ? Color.FromArgb(67, 181, 129) : Color.FromArgb(237, 66, 69);
            testRes.Text = (ok ? "✔ " : "✖ ") + msg;
            btnTest.Enabled = true;
        };

        Divider(pg1, 188);

        pg1.Controls.Add(Txt("Bot de música (opcional)", 20, 198, 428, 22, fg, 10f, true));
        pg1.Controls.Add(Txt("Link do websocket — ex.: ws://localhost/ws", 20, 222, 428, 20, mute, 8.75f));
        var tbUrl = Field(pg1, 20, 242, 428, rpcCfg.WsUrl);
        pg1.Controls.Add(Txt("Token (comando /rich_presence do bot)", 20, 280, 428, 20, mute, 8.75f));
        var tbTok = Field(pg1, 20, 300, 328, rpcCfg.Token, secret: true);
        var btnEye = Btn("Mostrar", 356, 300, 92, false);
        btnEye.Click += (_, _) => { tbTok.UseSystemPasswordChar = !tbTok.UseSystemPasswordChar; btnEye.Text = tbTok.UseSystemPasswordChar ? "Mostrar" : "Ocultar"; };
        pg1.Controls.Add(btnEye);

        // ================= Aba 2 · Opções (as mesmas do RPC) =================
        var pg2 = pages[1];
        var cbSite = Opt(pg2, "Receber a música do site do Azul Groove", rpcCfg.AcceptSite, 14);
        var cbListen = Opt(pg2, "Botão “Ouvir no navegador/app”", rpcCfg.ShowListenButton, 46);
        var cbAlong = Opt(pg2, "Botão “Ouvir junto” (se o bot enviar o convite da call)", rpcCfg.ShowListenAlong, 78);
        var cbPlBtn = Opt(pg2, "Botão de playlist (quando disponível)", rpcCfg.ShowPlaylistButton, 110);
        var cbPlRef = Opt(pg2, "Incluir o ID da playlist no link do botão (YouTube)", rpcCfg.PlaylistRefs, 142);
        var cbThumb = Opt(pg2, "Exibir a miniatura da música (quando disponível)", rpcCfg.ShowThumbnail, 174);
        var cbQueue = Opt(pg2, "Exibir a quantidade de músicas na fila", rpcCfg.ShowQueue, 206);
        var cbBlock = Opt(pg2, "Ignorar músicas adicionadas por outros membros", rpcCfg.BlockOtherUsers, 238);
        var cbInvite = Opt(pg2, "Botão de convite do bot (quando disponível)", rpcCfg.BotInvite, 270);

        // ================= Aba 3 · Imagens =================
        var pg3 = pages[2];
        TextBox Img(string label, string value, int y)
        {
            pg3.Controls.Add(Txt(label, 20, y, 428, 20, mute, 8.75f));
            return Field(pg3, 20, y + 20, 428, value);
        }
        var tbLoop = Img("Loop/Repetição", rpcCfg.LoopImg, 14);
        var tbLoopQ = Img("Loop/Repetição da fila", rpcCfg.LoopQueueImg, 74);
        var tbPause = Img("Pausa", rpcCfg.PauseImg, 134);
        var tbStream = Img("Stream/Transmissão", rpcCfg.StreamImg, 194);
        var tbIdle = Img("Em espera", rpcCfg.IdleImg, 254);
        pg3.Controls.Add(Txt("Links diretos de PNG/GIF. Deixe vazio para usar a imagem padrão.", 20, 314, 428, 20, mute, 8.75f));

        SelectTab(0);

        // ---- Barra de botões ----
        var bar = new Panel { Dock = DockStyle.Bottom, Height = 56, BackColor = bg };
        f.Controls.Add(bar);
        bar.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 1, BackColor = line });
        var off = Btn("Desligar", 16, 13, 90, false);
        var save = Btn("Salvar", 296, 13, 90, true);
        var cancel = Btn("Cancelar", 394, 13, 90, false);
        cancel.DialogResult = DialogResult.Cancel;
        off.Click += (_, _) => { f.DialogResult = DialogResult.Abort; };

        bool BadUrl(TextBox tb)
        {
            var s = tb.Text.Trim();
            return s.Length > 0 && !(Uri.TryCreate(s, UriKind.Absolute, out var iu) &&
                                     (iu.Scheme == Uri.UriSchemeHttp || iu.Scheme == Uri.UriSchemeHttps));
        }
        save.Click += (_, _) =>
        {
            var app = tbApp.Text.Trim().Replace(" ", "");
            var url = tbUrl.Text.Trim();
            if (cbOver.Checked && !RpcSettings.ValidAppId(app))
            {
                SelectTab(0);
                MessageBox.Show(f, "O ID do aplicativo deve ter só números (17 a 20 dígitos).", "Rich Presence",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (url.Length > 0 && !(Uri.TryCreate(url, UriKind.Absolute, out var wu) && (wu.Scheme == "ws" || wu.Scheme == "wss")))
            {
                SelectTab(0);
                MessageBox.Show(f, "O link do websocket precisa começar com ws:// ou wss://", "Rich Presence",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (BadUrl(tbLoop) || BadUrl(tbLoopQ) || BadUrl(tbPause) || BadUrl(tbStream) || BadUrl(tbIdle))
            {
                SelectTab(2);
                MessageBox.Show(f, "Os links das imagens precisam começar com http:// ou https://\n(deixe vazio para usar a imagem padrão).",
                    "Rich Presence", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            f.DialogResult = DialogResult.OK;
        };
        bar.Controls.AddRange(new Control[] { save, cancel, off });
        f.AcceptButton = save;
        f.CancelButton = cancel;

        var res = f.ShowDialog();
        tm.Stop(); tm.Dispose();

        if (res == DialogResult.Abort)
        {
            rpcCfg.On = false; rpcCfg.Save();
            if (miRpcOn.Checked) miRpcOn.Checked = false;
            return;
        }
        if (res != DialogResult.OK) { miRpcOn.Checked = rpcCfg.On; return; }

        var newApp = tbApp.Text.Trim().Replace(" ", "");
        rpcCfg.OverrideAppId = cbOver.Checked;
        rpcCfg.AppId = RpcSettings.ValidAppId(newApp) ? newApp : RpcSettings.DummyAppId;
        rpcCfg.WsUrl = tbUrl.Text.Trim();
        rpcCfg.Token = tbTok.Text.Replace(" ", "").Replace("\n", "").Replace("\r", "");
        rpcCfg.AcceptSite = cbSite.Checked;
        rpcCfg.ShowListenButton = cbListen.Checked;
        rpcCfg.ShowListenAlong = cbAlong.Checked;
        rpcCfg.ShowPlaylistButton = cbPlBtn.Checked;
        rpcCfg.PlaylistRefs = cbPlRef.Checked;
        rpcCfg.ShowThumbnail = cbThumb.Checked;
        rpcCfg.ShowQueue = cbQueue.Checked;
        rpcCfg.BlockOtherUsers = cbBlock.Checked;
        rpcCfg.BotInvite = cbInvite.Checked;
        rpcCfg.LoopImg = RpcSettings.Or(tbLoop.Text, RpcSettings.DefLoop);
        rpcCfg.LoopQueueImg = RpcSettings.Or(tbLoopQ.Text, RpcSettings.DefLoop);
        rpcCfg.PauseImg = RpcSettings.Or(tbPause.Text, RpcSettings.DefPause);
        rpcCfg.StreamImg = RpcSettings.Or(tbStream.Text, RpcSettings.DefStream);
        rpcCfg.IdleImg = RpcSettings.Or(tbIdle.Text, RpcSettings.DefIdle);
        rpcCfg.On = true;
        rpcCfg.Save();
        if (!miRpcOn.Checked) miRpcOn.Checked = true; // dispara o RpcStart pelo próprio item (evita iniciar duas vezes)
        else RpcStart();
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
    public static bool TryBuild(JsonElement msg, RpcSettings cfg, string userId, string botId, out JsonObject? activity)
    {
        activity = null;
        switch (Str(msg, "op"))
        {
            case "close":
                return true; // activity null = limpa a presence
            case "idle":
                activity = BuildIdle(msg, cfg, botId);
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
            if (srcKey == "twitch") { small = SourceIcons["twitch"]; smallText = "Twitch: Ao vivo"; }
            else { small = cfg.StreamImg; smallText = "Ao vivo"; }
        }

        if (paused)
        {
            small = cfg.PauseImg;
            smallText = "Em pausa";
        }
        else if (!stream)
        {
            if (LoopText(t, out var loopText, out var isQueue))
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
            buttons.Add(Button("Ouvir no navegador/app", u.Replace("www.", "")));
        }

        var state = author.Length > 0 ? "Por: " + author : "Azul Groove";

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
                    state += " | Playlist: " + pName;
                    buttons.Add(Button("Visualizar Playlist", pUrl.Replace("www.", "")));
                }
                else
                {
                    if (pName.Length < 23) { pName = "Playlist: " + pName; desc.Add(pName); }
                    else desc.Add("Playlist: " + pName);
                    buttons.Add(Button(pName, pUrl.Replace("www.", "")));
                }
            }
            else if (pName.Length > 0) desc.Add("Playlist: " + pName);
        }

        if (aUrl != null)
        {
            var albumTxt = "Álbum: " + aName;
            if (buttons.Count < 2)
                buttons.Add(aName.Length > 32 ? Button("Visualizar Álbum", aUrl.Replace("www.", "")) : Button(albumTxt, aUrl));
            desc.Add(albumTxt);
        }

        if (Bool(t, "247")) state += " | ✅24/7";
        int queue = (int)Num(t, "queue");
        if (queue > 0 && cfg.ShowQueue) state += $" | Na fila: {queue}";

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
            buttons.Insert(0, Button("Ouvir junto (canal de voz)", along));
        }

        activity = new JsonObject
        {
            ["type"] = 2, // "Ouvindo"
            ["details"] = Cut(title, 128, "Tocando agora"),
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
    static JsonObject BuildIdle(JsonElement msg, RpcSettings cfg, string botId)
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
            ["details"] = "Aguardando por",
            ["state"] = "novas músicas...",
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
                Button("Me adicione no seu server.",
                       $"https://discord.com/api/oauth2/authorize?client_id={botId}&permissions={perms}&scope=bot%20applications.commands")
            };
            var support = HttpUrl(Str(msg, "support_server"), 512);
            if (support != null) buttons.Add(Button("Servidor de Suporte", support));
            act["buttons"] = buttons;
        }
        return act;
    }

    // "loop" pode ser: "queue" | true | número (repetições restantes) | [atual, total]
    static bool LoopText(JsonElement track, out string text, out bool isQueue)
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
            text = "Repetição da fila: ativada";
            isQueue = true;
        }
        else if (lp.ValueKind == JsonValueKind.Array && lp.GetArrayLength() >= 2)
            text = $"Repetição ativada: {lp[0]}/{lp[1]}.";
        else if (lp.ValueKind == JsonValueKind.Number)
            text = $"Repetições restantes: {lp.GetRawText()}";
        else
            text = "Repetição ativada";
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
            Status?.Invoke(failMsg.Length > 0 ? failMsg : "Discord não encontrado — abra o Discord (tentando de novo…)");
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
