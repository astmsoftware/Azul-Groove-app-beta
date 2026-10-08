using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AzulGroove;

public partial class MainForm : Form
{
    // >>> Troque aqui se o endereço do site mudar <<<
    const string HomeUrl = "https://azul-groove.vercel.app/";
    const string ChatUrl = "https://azul-groove.vercel.app/chat";

    // Hosts que podem abrir DENTRO do app (login do Discord, Google e GitHub via Firebase)
    static readonly string[] InAppHosts =
    {
        "azul-groove.vercel.app", "discord.com", "accounts.google.com",
        "github.com", "azul-groove.firebaseapp.com", "firebaseapp.com"
    };

    // ---------- Atalhos de teclado (globais) ----------
    const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000;
    const int WM_HOTKEY = 0x0312, HOTKEY_ID = 0xA2B1;

    static readonly (string Label, uint Mod, Keys Key)[] Presets =
    {
        ("Ctrl + Alt + A",       MOD_CONTROL | MOD_ALT,   Keys.A),
        ("Ctrl + Alt + Espaço",  MOD_CONTROL | MOD_ALT,   Keys.Space),
        ("Ctrl + Shift + A",     MOD_CONTROL | MOD_SHIFT, Keys.A),
    };

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    internal static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AzulGroove");

    // true só na primeiríssima abertura (a pasta de dados ainda não existe; o WebView2 a cria logo depois)
    static readonly bool firstRun = !Directory.Exists(DataDir);

    readonly WebView2 web = new() { Dock = DockStyle.Fill };
    readonly AppSettings cfg = AppSettings.Load();
    NotifyIcon tray = null!;
    ContextMenuStrip trayMenu = null!;
    ToolStripMenuItem miChat = null!, miHotkeyOn = null!, miKeys = null!, miTray = null!, miTheme = null!;
    bool exiting, tipShown, skipSplash, pendingSitePref;
    string? pageTheme; // "light" | "dark" (enviado pela página)

    public MainForm()
    {
        Text = "Azul Groove";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? Icon;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1200, 800);
        MinimumSize = new Size(480, 600);
        Controls.Add(web);
        BuildTray();
        Load += async (_, _) => await InitWebAsync();
        // Se o usuário troca o tema do Windows e a página ainda não informou o dela, acompanha o Windows
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, _) =>
        {
            if (pageTheme == null && cfg.Theme == 0 && !IsDisposed) BeginInvoke(() => ApplyTheme(WindowsIsDark()));
        };
    }

    // ================= Bandeja + atalho =================
    void BuildTray()
    {
        var menu = new ContextMenuStrip();
        trayMenu = menu;

        miChat = new ToolStripMenuItem("Abrir chat", null, (_, _) => ShowChat());
        var miHome = new ToolStripMenuItem("Abrir início", null, (_, _) => { ShowWindow(); NavigateTo(HomeUrl); });
        var miPanel = new ToolStripMenuItem("Abrir painel", null, (_, _) => { ShowWindow(); ShowPanel(); });

        miHotkeyOn = new ToolStripMenuItem("Atalho de teclado para abrir o chat") { CheckOnClick = true, Checked = cfg.HotkeyOn };
        miHotkeyOn.CheckedChanged += (_, _) => { cfg.HotkeyOn = miHotkeyOn.Checked; cfg.Save(); RegisterHotkey(); UpdateLabels(); };

        miKeys = new ToolStripMenuItem("Tecla do atalho");
        for (int i = 0; i < Presets.Length; i++)
        {
            int idx = i;
            var item = new ToolStripMenuItem(Presets[i].Label) { Checked = i == cfg.Preset };
            item.Click += (_, _) =>
            {
                cfg.Preset = idx; cfg.Save();
                for (int j = 0; j < miKeys.DropDownItems.Count; j++)
                    ((ToolStripMenuItem)miKeys.DropDownItems[j]).Checked = j == idx;
                RegisterHotkey(); UpdateLabels();
            };
            miKeys.DropDownItems.Add(item);
        }

        miTheme = new ToolStripMenuItem("Tema (claro / escuro)");
        string[] themeNames = { "Automático (segue o Windows)", "Claro", "Escuro" };
        for (int i = 0; i < themeNames.Length; i++)
        {
            int idx = i;
            var item = new ToolStripMenuItem(themeNames[i]) { Checked = i == cfg.Theme };
            item.Click += (_, _) =>
            {
                cfg.Theme = idx; cfg.Save();
                for (int j = 0; j < miTheme.DropDownItems.Count; j++)
                    ((ToolStripMenuItem)miTheme.DropDownItems[j]).Checked = j == idx;
                ApplyThemeMode(pushToSite: true);
            };
            miTheme.DropDownItems.Add(item);
        }

        miTray = new ToolStripMenuItem("Continuar na bandeja ao fechar a janela") { CheckOnClick = true, Checked = cfg.TrayOnClose };
        miTray.CheckedChanged += (_, _) => { cfg.TrayOnClose = miTray.Checked; cfg.Save(); };

        var miExit = new ToolStripMenuItem("Sair", null, (_, _) => { exiting = true; Close(); });

        menu.Items.AddRange(new ToolStripItem[]
        {
            miChat, miPanel, miHome, new ToolStripSeparator(),
            miTheme, miHotkeyOn, miKeys, miTray, new ToolStripSeparator(), miExit
        });

        tray = new NotifyIcon { Icon = Icon ?? SystemIcons.Application, Text = "Azul Groove", Visible = true, ContextMenuStrip = menu };
        tray.DoubleClick += (_, _) => ShowChat();
        ApplyMenuTheme(cfg.Theme == 1 ? false : cfg.Theme == 2 ? true : WindowsIsDark());
        UpdateLabels();
    }


    // ================= Menu da bandeja (claro / escuro) =================
    void ApplyMenuTheme(bool dark)
    {
        if (trayMenu == null) return;
        var bg = dark ? Color.FromArgb(24, 27, 36) : Color.FromArgb(250, 251, 254);
        var fg = dark ? Color.FromArgb(235, 238, 247) : Color.FromArgb(28, 32, 48);
        var renderer = new MenuRenderer(dark);
        StyleMenu(trayMenu, renderer, bg, fg);
    }

    static void StyleMenu(ToolStripDropDown dd, ToolStripRenderer renderer, Color bg, Color fg)
    {
        dd.Renderer = renderer;
        dd.BackColor = bg;
        dd.ForeColor = fg;
        dd.Font = new Font("Segoe UI", 9.5f);
        if (dd is ToolStripDropDownMenu ddm) { ddm.ShowImageMargin = false; ddm.ShowCheckMargin = true; }
        if (!dd.IsHandleCreated) dd.HandleCreated += (_, _) => RoundCorners(dd.Handle);
        else RoundCorners(dd.Handle);

        foreach (ToolStripItem it in dd.Items)
        {
            it.BackColor = bg;
            it.ForeColor = fg;
            it.Padding = new Padding(4, 5, 4, 5);
            if (it is ToolStripMenuItem mi && mi.HasDropDownItems)
                StyleMenu(mi.DropDown, renderer, bg, fg);
        }
    }

    static void RoundCorners(IntPtr h)
    {
        try { int round = 2; DwmSetWindowAttribute(h, 33, ref round, sizeof(int)); } catch { }
    }

    sealed class MenuColors : ProfessionalColorTable
    {
        readonly bool dark;
        public MenuColors(bool dark) { this.dark = dark; UseSystemColors = false; }
        Color Bg => dark ? Color.FromArgb(24, 27, 36) : Color.FromArgb(250, 251, 254);
        Color Hover => dark ? Color.FromArgb(46, 52, 80) : Color.FromArgb(224, 229, 250);
        Color Line => dark ? Color.FromArgb(52, 57, 75) : Color.FromArgb(208, 213, 228);
        public override Color ToolStripDropDownBackground => Bg;
        public override Color ImageMarginGradientBegin => Bg;
        public override Color ImageMarginGradientMiddle => Bg;
        public override Color ImageMarginGradientEnd => Bg;
        public override Color MenuBorder => Line;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemPressedGradientBegin => Hover;
        public override Color MenuItemPressedGradientMiddle => Hover;
        public override Color MenuItemPressedGradientEnd => Hover;
        public override Color SeparatorDark => Line;
        public override Color SeparatorLight => Bg;
        public override Color CheckBackground => Hover;
        public override Color CheckSelectedBackground => Hover;
        public override Color CheckPressedBackground => Hover;
    }

    sealed class MenuRenderer : ToolStripProfessionalRenderer
    {
        readonly bool dark;
        readonly Color fg, dim, accent;
        public MenuRenderer(bool dark) : base(new MenuColors(dark))
        {
            this.dark = dark;
            fg = dark ? Color.FromArgb(235, 238, 247) : Color.FromArgb(28, 32, 48);
            dim = dark ? Color.FromArgb(120, 126, 150) : Color.FromArgb(150, 155, 175);
            accent = Color.FromArgb(125, 140, 255);
            RoundedEdges = false;
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? fg : dim;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = fg;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var r = e.ImageRectangle;
            using var pen = new Pen(accent, 2f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
            g.DrawLines(pen, new[]
            {
                new Point(r.Left + 4, r.Top + r.Height / 2),
                new Point(r.Left + r.Width / 2 - 1, r.Bottom - 5),
                new Point(r.Right - 4, r.Top + 5)
            });
        }
    }

    string HotkeyText => cfg.HotkeyOn ? Presets[cfg.Preset].Label : "";

    void UpdateLabels()
    {
        miChat.Text = cfg.HotkeyOn ? $"Abrir chat    ({HotkeyText})" : "Abrir chat";
    }

    void RegisterHotkey()
    {
        if (!IsHandleCreated) return;
        UnregisterHotKey(Handle, HOTKEY_ID);
        hotkeyOk = true;
        if (!cfg.HotkeyOn) return;
        var p = Presets[cfg.Preset];
        hotkeyOk = RegisterHotKey(Handle, HOTKEY_ID, p.Mod | MOD_NOREPEAT, (uint)p.Key);
        if (!hotkeyOk)
            tray.ShowBalloonTip(4000, "Azul Groove",
                $"Não foi possível usar {p.Label} (outro programa já usa). Escolha outra tecla no ícone da bandeja.",
                ToolTipIcon.Warning);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_ID) { ShowChat(); return; }
        base.WndProc(ref m);
    }

    void ShowWindow()
    {
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
        web.Focus();
    }

    // Compara só host + caminho (ignora ?query e #fragmento). Antes, "StartsWith" fazia o botão
    // "Site Azul Groove" não fazer nada quando o app já estava em qualquer página do site.
    static bool SamePage(string? current, string target)
    {
        if (!Uri.TryCreate(current, UriKind.Absolute, out var a)) return false;
        if (!Uri.TryCreate(target, UriKind.Absolute, out var b)) return false;
        return string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.AbsolutePath.TrimEnd('/'), b.AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }

    void NavigateTo(string url)
    {
        skipSplash = true; // se a animação ainda está rolando, pula o "voltar para o início"
        var cw = web.CoreWebView2;
        if (cw == null) return;
        if (!SamePage(cw.Source, url)) cw.Navigate(url);
    }

    /// <summary>Traz o app para a frente e abre o chat (chamado pelo atalho, pela bandeja e por um 2º clique no .exe).</summary>
    public void ShowChat()
    {
        ShowWindow();
        NavigateTo(ChatUrl);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!exiting && cfg.TrayOnClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            if (!tipShown)
            {
                tipShown = true;
                tray.ShowBalloonTip(3500, "Azul Groove",
                    cfg.HotkeyOn ? $"O app continua na bandeja. Aperte {HotkeyText} para abrir o chat."
                                 : "O app continua na bandeja. Clique duas vezes no ícone para abrir.",
                    ToolTipIcon.Info);
            }
            return;
        }
        tray.Visible = false;
        tray.Dispose();
        base.OnFormClosing(e);
    }

    // ================= Tema claro/escuro =================
    // Tema da janela (barra de título + fundo): segue o tema da página; antes de carregar, segue o Windows
    const string ThemeScript = @"(function(){
  if (window.top !== window || location.protocol !== 'https:') return;
  var send = function(){ try {
    var t = document.documentElement.getAttribute('data-theme') === 'light' ? 'light' : 'dark';
    window.chrome.webview.postMessage(t); } catch(e){} };
  document.addEventListener('DOMContentLoaded', function(){
    new MutationObserver(send).observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });
    send(); });
  window.addEventListener('load', send);
})();";

    static bool WindowsIsDark()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return (k?.GetValue("AppsUseLightTheme") as int?) == 0;
        }
        catch { return true; }
    }

    void ApplyTheme(bool dark)
    {
        ApplyMenuTheme(dark);
        BackColor = dark ? Color.FromArgb(15, 17, 23) : Color.FromArgb(244, 246, 251);
        web.DefaultBackgroundColor = BackColor;
        if (!IsHandleCreated) return;
        int v = dark ? 1 : 0;
        // 20 = Windows 10 20H1+ e Windows 11; 19 = Windows 10 mais antigo
        if (DwmSetWindowAttribute(Handle, 20, ref v, sizeof(int)) != 0)
            DwmSetWindowAttribute(Handle, 19, ref v, sizeof(int));
    }

    // Aplica a escolha do menu (Automático / Claro / Escuro)
    void ApplyThemeMode(bool pushToSite)
    {
        if (web.CoreWebView2 != null)
            web.CoreWebView2.Profile.PreferredColorScheme = cfg.Theme switch
            {
                1 => CoreWebView2PreferredColorScheme.Light,
                2 => CoreWebView2PreferredColorScheme.Dark,
                _ => CoreWebView2PreferredColorScheme.Auto
            };

        // Enquanto a página não informar o tema dela, a janela usa a escolha (ou o Windows)
        if (pageTheme == null)
            ApplyTheme(cfg.Theme == 1 ? false : cfg.Theme == 2 ? true : WindowsIsDark());

        if (pushToSite) { pendingSitePref = true; _ = PushSiteThemeAsync(); }
    }

    // Grava a preferência no site (o chat guarda em azul_theme) e atualiza a página aberta
    async Task PushSiteThemeAsync()
    {
        var cw = web.CoreWebView2;
        if (cw == null || !(cw.Source ?? "").StartsWith(HomeUrl)) return; // fica pendente até abrir o site
        var pref = cfg.Theme == 1 ? "light" : cfg.Theme == 2 ? "dark" : "auto";
        try
        {
            await cw.ExecuteScriptAsync(
                $"try{{localStorage.setItem('azul_theme','{pref}');}}catch(e){{}}" +
                "if(typeof applyTheme==='function')applyTheme();else location.reload();");
            pendingSitePref = false;
        }
        catch { /* tenta de novo na próxima página */ }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyTheme(cfg.Theme == 1 ? false : cfg.Theme == 2 ? true : WindowsIsDark());
        RegisterHotkey();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        UnregisterHotKey(Handle, HOTKEY_ID);
        base.OnHandleDestroyed(e);
    }

    // ================= Animação de abertura =================
    const string SplashHtml = """
<!doctype html><html><head><meta charset="utf-8"><meta name="color-scheme" content="dark light"><style>
:root{--bg:#0f1117;--tx:#fff;--mu:#9aa3b2}
@media (prefers-color-scheme:light){:root{--bg:#f4f6fb;--tx:#14161a;--mu:#646b78}}
html,body{height:100%;margin:0;background:var(--bg);overflow:hidden;font-family:"Segoe UI",system-ui,sans-serif}
body{display:flex;flex-direction:column;align-items:center;justify-content:center;gap:24px}
.logo{width:132px;height:132px;border-radius:34px;background:linear-gradient(135deg,#5865f2,#7a5cff);display:flex;align-items:center;justify-content:center;gap:9px;box-shadow:0 20px 60px rgba(88,101,242,.55);animation:pop .8s cubic-bezier(.2,1.4,.4,1) both,glow 2s ease-in-out .8s infinite}
.logo i{display:block;width:12px;height:40px;border-radius:6px;background:#fff;animation:eq 1s ease-in-out infinite}
.logo i:nth-child(1){animation-delay:-.9s}.logo i:nth-child(2){animation-delay:-.65s}.logo i:nth-child(3){animation-delay:-.4s}.logo i:nth-child(4){animation-delay:-.75s}.logo i:nth-child(5){animation-delay:-.2s}
h1{margin:0;color:var(--tx);font-size:34px;letter-spacing:.5px;animation:up .8s .35s both}
p{margin:0;color:var(--mu);animation:up .8s .55s both}
.bar{width:180px;height:4px;border-radius:4px;background:rgba(127,127,127,.25);overflow:hidden;animation:up .8s .7s both}
.bar b{display:block;height:100%;width:40%;border-radius:4px;background:linear-gradient(90deg,#5865f2,#7a5cff);animation:slide 1.1s ease-in-out infinite}
@keyframes pop{from{transform:scale(.3) rotate(-12deg);opacity:0}to{transform:none;opacity:1}}
@keyframes eq{0%,100%{height:18px}50%{height:64px}}
@keyframes glow{50%{box-shadow:0 20px 90px rgba(122,92,255,.85)}}
@keyframes up{from{transform:translateY(14px);opacity:0}to{transform:none;opacity:1}}
@keyframes slide{from{transform:translateX(-110%)}to{transform:translateX(260%)}}
@media (prefers-reduced-motion:reduce){*{animation-duration:.01s!important;animation-iteration-count:1!important}}
</style></head><body><div class="logo"><i></i><i></i><i></i><i></i><i></i></div><h1>Azul Groove</h1><p>Carregando…</p><div class="bar"><b></b></div></body></html>
""";

    // Animação mostrada na primeira abertura depois de uma atualização
    const string UpdateSplashHtml = """
<!doctype html><html><head><meta charset="utf-8"><meta name="color-scheme" content="dark light"><style>
:root{--bg:#0f1117;--tx:#fff;--mu:#9aa3b2}
@media (prefers-color-scheme:light){:root{--bg:#f4f6fb;--tx:#14161a;--mu:#646b78}}
html,body{height:100%;margin:0;background:var(--bg);overflow:hidden;font-family:"Segoe UI",system-ui,sans-serif}
body{display:flex;flex-direction:column;align-items:center;justify-content:center;gap:20px}
.logo{width:120px;height:120px;border-radius:32px;background:linear-gradient(135deg,#5865f2,#7a5cff);display:flex;align-items:center;justify-content:center;gap:8px;box-shadow:0 20px 60px rgba(88,101,242,.55);animation:pop .8s cubic-bezier(.2,1.4,.4,1) both,glow 2s ease-in-out .8s infinite}
.logo i{display:block;width:11px;height:36px;border-radius:6px;background:#fff;animation:eq 1s ease-in-out infinite}
.logo i:nth-child(1){animation-delay:-.9s}.logo i:nth-child(2){animation-delay:-.65s}.logo i:nth-child(3){animation-delay:-.4s}.logo i:nth-child(4){animation-delay:-.75s}.logo i:nth-child(5){animation-delay:-.2s}
h1{margin:0;color:var(--tx);font-size:32px;animation:up .8s .3s both}
.v{color:var(--tx);font-size:18px;font-weight:600;padding:5px 16px;border-radius:30px;background:rgba(88,101,242,.25);animation:up .8s .5s both}
.tx{display:grid;text-align:center;animation:up .8s .6s both}
.tx p{grid-area:1/1;margin:0;color:var(--mu)}
.t1{animation:hide .3s 2.7s forwards}
.t2{opacity:0;color:var(--tx)!important;font-weight:600;animation:show .5s 2.9s forwards}
.bar{width:200px;height:5px;border-radius:5px;background:rgba(127,127,127,.25);overflow:hidden;animation:up .8s .7s both}
.bar b{display:block;height:100%;width:0;border-radius:5px;background:linear-gradient(90deg,#5865f2,#7a5cff);animation:fill 2.6s .4s ease-in-out forwards}
@keyframes pop{from{transform:scale(.3) rotate(-12deg);opacity:0}to{transform:none;opacity:1}}
@keyframes eq{0%,100%{height:16px}50%{height:58px}}
@keyframes glow{50%{box-shadow:0 20px 90px rgba(122,92,255,.85)}}
@keyframes up{from{transform:translateY(14px);opacity:0}to{transform:none;opacity:1}}
@keyframes fill{to{width:100%}}
@keyframes hide{to{opacity:0}}
@keyframes show{to{opacity:1}}
@media (prefers-reduced-motion:reduce){*{animation-duration:.01s!important;animation-delay:0s!important;animation-iteration-count:1!important}}
</style></head><body><div class="logo"><i></i><i></i><i></i><i></i><i></i></div><h1>Azul Groove</h1><div class="v">@FROM@ → @TO@</div>
<div class="tx"><p class="t1">Finalizando a atualização…</p><p class="t2">✅ Atualizado! Você está na versão @TO@</p></div><div class="bar"><b></b></div></body></html>
""";

    // Animação mostrada só na primeira instalação (estilo "configuração" do Windows 11)
    const string SetupSplashHtml = """
<!doctype html><html><head><meta charset="utf-8"><meta name="color-scheme" content="dark light"><style>
:root{--bg:#0f1117;--tx:#fff;--mu:#9aa3b2}
@media (prefers-color-scheme:light){:root{--bg:#f4f6fb;--tx:#14161a;--mu:#646b78}}
html,body{height:100%;margin:0;background:var(--bg);overflow:hidden;font-family:"Segoe UI",system-ui,sans-serif}
body{display:flex;flex-direction:column;align-items:center;justify-content:center;gap:34px}
.logo{width:96px;height:96px;border-radius:26px;background:linear-gradient(135deg,#5865f2,#7a5cff);display:flex;align-items:center;justify-content:center;gap:7px;box-shadow:0 16px 50px rgba(88,101,242,.5);animation:pop .8s cubic-bezier(.2,1.4,.4,1) both}
.logo i{display:block;width:9px;height:30px;border-radius:5px;background:#fff;animation:eq 1s ease-in-out infinite}
.logo i:nth-child(1){animation-delay:-.9s}.logo i:nth-child(2){animation-delay:-.65s}.logo i:nth-child(3){animation-delay:-.4s}.logo i:nth-child(4){animation-delay:-.75s}.logo i:nth-child(5){animation-delay:-.2s}
.msgs{position:relative;width:420px;height:44px;text-align:center}
.msgs p{position:absolute;inset:0;margin:0;opacity:0;color:var(--tx);font-size:26px;font-weight:300;letter-spacing:.3px}
.m1{animation:msg 1.6s .5s both}.m2{animation:msg 1.6s 2.1s both}.m3{animation:msg 1.6s 3.7s both}
.m4{animation:last .6s 5.3s forwards;font-weight:600!important}
.dots{display:flex;gap:10px;height:12px;animation:up .6s .3s both}
.dots b{width:8px;height:8px;border-radius:50%;background:var(--tx);animation:orbit 1.2s ease-in-out infinite}
.dots b:nth-child(2){animation-delay:.15s}.dots b:nth-child(3){animation-delay:.3s}.dots b:nth-child(4){animation-delay:.45s}
.done .dots{display:none}
.sub{color:var(--mu);font-size:13px;margin:0;animation:up .8s .6s both}
@keyframes pop{from{transform:scale(.3) rotate(-12deg);opacity:0}to{transform:none;opacity:1}}
@keyframes eq{0%,100%{height:14px}50%{height:50px}}
@keyframes up{from{transform:translateY(14px);opacity:0}to{transform:none;opacity:1}}
@keyframes msg{0%{opacity:0;transform:translateY(10px)}20%,80%{opacity:1;transform:none}100%{opacity:0;transform:translateY(-10px)}}
@keyframes last{from{opacity:0;transform:translateY(10px)}to{opacity:1;transform:none}}
@keyframes orbit{0%,100%{transform:translateY(0);opacity:.35}50%{transform:translateY(-8px);opacity:1}}
@media (prefers-reduced-motion:reduce){*{animation-duration:.01s!important;animation-iteration-count:1!important}}
</style></head><body><div class="logo"><i></i><i></i><i></i><i></i><i></i></div>
<div class="msgs"><p class="m1">Olá!</p><p class="m2">Estamos preparando tudo para você</p><p class="m3">Quase lá…</p><p class="m4">Tudo pronto!</p></div>
<div class="dots"><b></b><b></b><b></b><b></b></div><p class="sub">Primeira configuração do Azul Groove</p></body></html>
""";

    async Task PlaySplashAsync()
    {
        var upd = ConsumeUpdateMarker(); // existe se o app acabou de ser atualizado pelo próprio painel
        int wait = 2300;
        if (upd != null)
        {
            justUpdated = true;
            wait = 4300;
            web.CoreWebView2.NavigateToString(UpdateSplashHtml.Replace("@FROM@", upd.Value.from).Replace("@TO@", upd.Value.to));
        }
        else if (firstRun)
        {
            wait = 6400; // dura o suficiente para as 4 mensagens
            web.CoreWebView2.NavigateToString(SetupSplashHtml);
        }
        else web.CoreWebView2.NavigateToString(SplashHtml);

        await Task.Delay(wait);
        if (skipSplash || IsDisposed) return;
        try { await web.CoreWebView2.ExecuteScriptAsync("document.body.style.transition='opacity .35s';document.body.style.opacity=0"); } catch { }
        await Task.Delay(380);
        if (skipSplash || IsDisposed) return;
        if (justUpdated) ShowPanel(); // depois de atualizar, abre o painel e mostra as novidades
        else GoStart();
    }

    // ================= WebView2 =================
    async Task InitWebAsync()
    {
        if (!await TryInitAsync())
        {
            // Sem o WebView2 Runtime: baixa e instala sozinho (precisa de internet) e tenta de novo
#if STORE
            var ok = false; // build da Store: não baixa instalador; mostra o aviso abaixo
#else
            var ok = await InstallRuntimeAsync() && await TryInitAsync();
#endif
            if (!ok)
            {
                var r = MessageBox.Show(
                    "Não foi possível iniciar o WebView2 Runtime.\n\nDeseja abrir a página de download?",
                    "Azul Groove", MessageBoxButtons.YesNo, MessageBoxIcon.Error);
                if (r == DialogResult.Yes)
                    OpenExternal("https://developer.microsoft.com/microsoft-edge/webview2/");
                exiting = true;
                Close();
                return;
            }
        }

        var s = web.CoreWebView2.Settings;
        s.AreDevToolsEnabled = false;
        s.AreDefaultContextMenusEnabled = true;
        s.IsStatusBarEnabled = false;
        s.IsZoomControlEnabled = true;

        // prefers-color-scheme da página acompanha o Windows
        ApplyThemeMode(pushToSite: false);
        web.CoreWebView2.NavigationCompleted += (_, _) => { if (pendingSitePref) _ = PushSiteThemeAsync(); };
        await web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(ThemeScript);
        web.CoreWebView2.WebMessageReceived += (_, e) =>
        {
            if (HandlePanelMessage(e)) return;
            var t = e.TryGetWebMessageAsString();
            if (t != "light" && t != "dark") return;
            pageTheme = t;
            ApplyTheme(t == "dark");
        };

        web.CoreWebView2.DocumentTitleChanged += (_, _) =>
            Text = string.IsNullOrWhiteSpace(web.CoreWebView2.DocumentTitle)
                ? "Azul Groove" : web.CoreWebView2.DocumentTitle;

        // Links com target="_blank": login abre em popup do app; o resto vai pro navegador
        web.CoreWebView2.NewWindowRequested += (_, e) =>
        {
            if (!IsInAppHost(e.Uri))
            {
                e.Handled = true;
                OpenExternal(e.Uri);
            }
        };

        // Navegação na mesma janela: sites externos abrem no navegador padrão
        web.CoreWebView2.NavigationStarting += (_, e) =>
        {
            // Saiu do painel local (foi para o site)? Então a página não é mais o painel.
            if (e.Uri.StartsWith("http", StringComparison.OrdinalIgnoreCase)) panelActive = false;
            if (!IsInAppHost(e.Uri) && !e.Uri.StartsWith("about:") && !e.Uri.StartsWith("data:"))
            {
                e.Cancel = true;
                OpenExternal(e.Uri);
            }
        };

        await PlaySplashAsync();
    }

    async Task<bool> TryInitAsync()
    {
        try
        {
            // Dados (login, cookies, configurações) ficam numa pasta do usuário
            var env = await CoreWebView2Environment.CreateAsync(null, DataDir);
            await web.EnsureCoreWebView2Async(env);
            return true;
        }
        catch { return false; }
    }

    async Task<bool> InstallRuntimeAsync()
    {
        try
        {
            Text = "Azul Groove — instalando componente necessário…";
            var exe = Path.Combine(Path.GetTempPath(), "MicrosoftEdgeWebview2Setup.exe");
            using (var http = new HttpClient())
            {
                var bytes = await http.GetByteArrayAsync("https://go.microsoft.com/fwlink/p/?LinkId=2124703");
                await File.WriteAllBytesAsync(exe, bytes);
            }
            var psi = new System.Diagnostics.ProcessStartInfo(exe, "/silent /install") { UseShellExecute = true };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return false;
            await p.WaitForExitAsync();
            Text = "Azul Groove";
            return true;
        }
        catch { return false; }
    }

    static bool IsInAppHost(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        if (u.Scheme != Uri.UriSchemeHttps) return false;
        return InAppHosts.Any(h => u.Host == h || u.Host.EndsWith("." + h));
    }

    static void OpenExternal(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return;
        if (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true }); }
        catch { /* ignora */ }
    }
}

/// <summary>Configurações do app (salvas em %LocalAppData%\AzulGroove\settings.json).</summary>
sealed class AppSettings
{
    public bool HotkeyOn { get; set; } = true;     // atalho global ligado
    public int Preset { get; set; } = 0;           // qual combinação de teclas
    public bool TrayOnClose { get; set; } = true;  // X da janela manda para a bandeja
    public int Theme { get; set; } = 0;            // 0 = automático (Windows), 1 = claro, 2 = escuro
    public int StartPage { get; set; } = 0;        // 0 = painel do app (padrão), 1 = site, 2 = chat

    static string FilePath => Path.Combine(MainForm.DataDir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
            if (s.Preset < 0 || s.Preset > 2) s.Preset = 0;
            if (s.Theme < 0 || s.Theme > 2) s.Theme = 0;
            if (s.StartPage < 0 || s.StartPage > 2) s.StartPage = 0;
            return s;
        }
        catch { return new AppSettings(); }
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
