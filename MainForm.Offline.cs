using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace AzulGroove;

/// <summary>
/// Modo offline do Azul Groove.
///  • Manual: item "Modo offline" na bandeja (ou o botão no painel) -> o app não abre o site/chat, só o painel local.
///  • Automático: sem internet (ou site fora do ar) -> mostra a tela "Sem conexão" e reconecta sozinho quando a rede volta.
///  • WebView2 100% offline: pasta "WebView2Runtime" (Fixed Version) ou o instalador offline ao lado do .exe.
/// </summary>
public partial class MainForm
{
    const string OfflineTitle = "Azul Groove — Sem conexão";

    static readonly HttpClient OfflineHttp = new() { Timeout = TimeSpan.FromSeconds(5) };

    ToolStripMenuItem miOffline = null!;
    string? lastWebTarget;   // última página do site que o app tentou abrir (volta para ela ao reconectar)
    bool reconnecting;

    // ================= Estado =================
    bool IsOffline => cfg.OfflineMode || !NetworkUp();

    // A tela offline se identifica pelo título (o painel local e as animações usam outros títulos)
    bool OnOfflinePage => !IsDisposed && web.CoreWebView2 != null && web.CoreWebView2.DocumentTitle == OfflineTitle;

    static bool NetworkUp()
    {
        try { return NetworkInterface.GetIsNetworkAvailable(); }
        catch { return true; }
    }

    // Qualquer resposta HTTP já prova que o servidor está acessível
    static async Task<bool> ProbeAsync()
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, HomeUrl);
            using var resp = await OfflineHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            return true;
        }
        catch { return false; }
    }

    // ================= Menu da bandeja =================
    ToolStripMenuItem OfflineMenuItem()
    {
        miOffline = new ToolStripMenuItem("Modo offline (sem internet)") { CheckOnClick = true, Checked = cfg.OfflineMode };
        miOffline.Click += (_, _) => SetOfflineMode(miOffline.Checked);
        return miOffline;
    }

    void OfflineInit() => NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;

    void OfflineShutdown() => NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;

    void SetOfflineMode(bool on)
    {
        cfg.OfflineMode = on;
        cfg.Save();
        if (miOffline != null && miOffline.Checked != on) miOffline.Checked = on;

        var cw = web.CoreWebView2;
        if (cw == null) return;
        var src = cw.Source ?? "";
        if (on)
        {
            if (src.StartsWith("http", StringComparison.OrdinalIgnoreCase)) ShowOfflinePage(src);
            else if (OnOfflinePage) ShowOfflinePage(); // só troca o texto para "modo offline ativado"
        }
        else if (OnOfflinePage) _ = LeaveManualOfflineAsync();

        _ = PushOfflineToPanelAsync(on);
    }

    async Task LeaveManualOfflineAsync()
    {
        if (await TryReconnectAsync(false)) return;
        if (!IsDisposed && OnOfflinePage)
            ShowOfflinePage(null, "Modo offline desligado, mas ainda não há conexão com a internet.");
    }

    // Mantém o botão do painel igual ao item da bandeja
    async Task PushOfflineToPanelAsync(bool on)
    {
        try
        {
            var cw = web.CoreWebView2;
            if (cw == null || IsDisposed || (cw.Source ?? "").StartsWith("http", StringComparison.OrdinalIgnoreCase)) return;
            await cw.ExecuteScriptAsync(
                $"window.__azBeta&&window.__azBeta.offline&&window.__azBeta.offline({(on ? "true" : "false")})");
        }
        catch { /* o painel pode não estar aberto */ }
    }

    // ================= Tela "Sem conexão" =================
    void ShowOfflinePage(string? target = null, string note = "")
    {
        var cw = web.CoreWebView2;
        if (cw == null || IsDisposed) return;
        if (target != null && target.StartsWith(HomeUrl, StringComparison.OrdinalIgnoreCase)) lastWebTarget = target;
        panelActive = false; // não injetar o bloco do painel nesta página
        skipSplash = true;   // se a animação de abertura ainda está rolando, ela não deve sobrescrever a tela
        var html = OfflineHtml
            .Replace("@TITLE@", OfflineTitle)
            .Replace("@MODE@", cfg.OfflineMode ? "manual" : "auto")
            .Replace("@NOTE@", note);
        cw.NavigateToString(html);
    }

    // Falhas de rede na navegação principal (sem internet, DNS, servidor fora do ar...)
    void OfflineOnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess || IsDisposed) return;
        switch (e.WebErrorStatus)
        {
            case CoreWebView2WebErrorStatus.ServerUnreachable:
            case CoreWebView2WebErrorStatus.Timeout:
            case CoreWebView2WebErrorStatus.ConnectionAborted:
            case CoreWebView2WebErrorStatus.ConnectionReset:
            case CoreWebView2WebErrorStatus.Disconnected:
            case CoreWebView2WebErrorStatus.CannotConnect:
            case CoreWebView2WebErrorStatus.HostNameNotResolved:
            {
                var src = web.CoreWebView2.Source ?? "";
                if (src.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    BeginInvoke(() => ShowOfflinePage(src));
                break;
            }
        }
    }

    // Mensagens enviadas pela tela offline e pelo botão "Modo offline" do painel
    bool HandleOfflineMessage(CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (e.Source.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return false; // o site nunca controla isto
        string? t;
        try { t = e.TryGetWebMessageAsString(); } catch { return false; }
        if (t == null || !t.StartsWith("offline:", StringComparison.Ordinal)) return false;

        switch (t)
        {
            case "offline:retry": _ = TryReconnectAsync(false); break;
            case "offline:panel": ShowPanel(); break;
            case "offline:online":
            case "offline:set:0": SetOfflineMode(false); break;
            case "offline:set:1": SetOfflineMode(true); break;
        }
        return true;
    }

    // ================= Reconexão =================
    void OnNetworkChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (IsDisposed || !IsHandleCreated || cfg.OfflineMode) return;
        BeginInvoke(async () =>
        {
            if (IsDisposed) return;
            if (e.IsAvailable)
            {
                if (OnOfflinePage) await TryReconnectAsync(true);
                else await OfflineToastAsync("");
            }
            else if (!OnOfflinePage)
            {
                await OfflineToastAsync("Sem conexão com a internet — reconectando quando voltar…");
            }
        });
    }

    async Task<bool> TryReconnectAsync(bool silent)
    {
        if (reconnecting || cfg.OfflineMode || IsDisposed) return false;
        reconnecting = true;
        try
        {
            // ao voltar a rede o DNS pode demorar alguns segundos: no modo silencioso tenta algumas vezes
            for (int i = 0; i < (silent ? 4 : 1); i++)
            {
                if (cfg.OfflineMode || IsDisposed) return false;
                if (await ProbeAsync())
                {
                    if (OnOfflinePage) web.CoreWebView2.Navigate(lastWebTarget ?? HomeUrl);
                    else await OfflineToastAsync("");
                    return true;
                }
                if (silent) await Task.Delay(2500);
            }
            if (!silent) await OfflinePageStatusAsync("Ainda sem conexão. Verifique o Wi‑Fi ou o cabo e tente de novo.");
            return false;
        }
        finally { reconnecting = false; }
    }

    async Task OfflinePageStatusAsync(string msg)
    {
        try
        {
            if (web.CoreWebView2 == null || IsDisposed) return;
            await web.CoreWebView2.ExecuteScriptAsync($"window.__azOff&&window.__azOff.status({JsonSerializer.Serialize(msg)})");
        }
        catch { }
    }

    // Aviso discreto na parte de baixo do site enquanto a internet está fora (msg vazia = remove)
    async Task OfflineToastAsync(string msg)
    {
        try
        {
            var cw = web.CoreWebView2;
            if (cw == null || IsDisposed || !(cw.Source ?? "").StartsWith("http", StringComparison.OrdinalIgnoreCase)) return;
            await cw.ExecuteScriptAsync(OfflineToastScript.Replace("@MSG@", JsonSerializer.Serialize(msg)));
        }
        catch { }
    }

    const string OfflineToastScript = """
(function(msg){try{
 var o=document.getElementById('az-off-toast');if(o)o.remove();
 if(!msg)return;
 var d=document.createElement('div');d.id='az-off-toast';d.textContent=msg;
 d.style.cssText='position:fixed;left:50%;bottom:22px;transform:translateX(-50%);z-index:2147483647;padding:11px 20px;border-radius:14px;background:rgba(22,25,38,.94);color:#fff;font:600 13px "Segoe UI",system-ui,sans-serif;border:1px solid rgba(255,255,255,.14);box-shadow:0 12px 40px rgba(0,0,0,.45);pointer-events:none';
 (document.body||document.documentElement).appendChild(d);
}catch(e){}})(@MSG@);
""";

    // ================= WebView2 sem internet =================
    // Opção A (a melhor): runtime "Fixed Version" extraído numa pasta  WebView2Runtime  ao lado do .exe.
    //   -> o app usa esse navegador embutido e não depende do que está instalado no Windows.
    static string? FixedRuntimeDir()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "WebView2Runtime");
        return File.Exists(Path.Combine(dir, "msedgewebview2.exe")) ? dir : null;
    }

    // Opção B: instalador offline da Microsoft (Evergreen Standalone) junto do app (ou numa subpasta "redist").
    static string? FindLocalWebViewInstaller()
    {
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "ARM64",
            Architecture.X86 => "X86",
            _ => "X64"
        };
        foreach (var dir in new[] { AppContext.BaseDirectory, Path.Combine(AppContext.BaseDirectory, "redist") })
        {
            var f = Path.Combine(dir, $"MicrosoftEdgeWebView2RuntimeInstaller{arch}.exe");
            if (File.Exists(f)) return f;
        }
        return null;
    }

    // ================= Design da tela offline =================
    const string OfflineHtml = """
<!doctype html><html lang="pt-BR"><head><meta charset="utf-8"><title>@TITLE@</title><meta name="color-scheme" content="dark light"><style>
:root{--bg:#0a0c13;--card:rgba(20,23,36,.62);--bd:rgba(255,255,255,.13);--tx:#f3f5fb;--mu:#a3abc0;--ac:#6d7cff;--ac2:#9a6bff;--in:rgba(255,255,255,.08);--bl:.6}
@media (prefers-color-scheme:light){:root{--bg:#eaeefb;--card:rgba(255,255,255,.66);--bd:rgba(0,0,0,.08);--tx:#151823;--mu:#5b6277;--in:rgba(0,0,0,.05);--bl:.45}}
*{box-sizing:border-box}
html,body{height:100%;margin:0;overflow:hidden;background:var(--bg);color:var(--tx);font-family:"Segoe UI Variable","Segoe UI",system-ui,sans-serif}
.bg{position:fixed;inset:0;overflow:hidden}
.bg i{position:absolute;border-radius:50%;filter:blur(85px);opacity:var(--bl);will-change:transform}
.bg i:nth-child(1){width:540px;height:540px;left:-130px;top:-150px;background:#5865f2;animation:d1 16s ease-in-out infinite alternate}
.bg i:nth-child(2){width:480px;height:480px;right:-110px;top:-50px;background:#b45cff;animation:d2 19s ease-in-out infinite alternate}
.bg i:nth-child(3){width:500px;height:500px;left:22%;bottom:-230px;background:#1fa8ff;animation:d3 22s ease-in-out infinite alternate}
@keyframes d1{to{transform:translate(240px,170px) scale(1.2)}}
@keyframes d2{to{transform:translate(-250px,210px) scale(.85)}}
@keyframes d3{to{transform:translate(190px,-190px) scale(1.15)}}
.wrap{position:relative;height:100%;display:flex;align-items:center;justify-content:center;padding:22px}
.card{width:100%;max-width:520px;padding:34px 36px 28px;border-radius:24px;background:var(--card);border:1px solid var(--bd);backdrop-filter:blur(30px) saturate(1.5);box-shadow:0 24px 80px rgba(0,0,0,.35);display:flex;flex-direction:column;gap:14px;animation:cin .7s cubic-bezier(.2,.9,.3,1) both}
@keyframes cin{from{opacity:0;transform:translateY(24px) scale(.97)}}
.ico{width:76px;height:76px;border-radius:22px;background:linear-gradient(135deg,#5865f2,#7a5cff);display:flex;align-items:center;justify-content:center;box-shadow:inset 0 1px 0 rgba(255,255,255,.35),0 14px 40px rgba(88,101,242,.5);animation:fl 3.2s ease-in-out infinite}
@keyframes fl{50%{transform:translateY(-6px)}}
h1{margin:6px 0 0;font-size:28px;font-weight:600;letter-spacing:.2px}
.sub{margin:0;color:var(--mu);font-size:15px;line-height:1.5}
.list{display:flex;flex-direction:column;gap:8px;margin:6px 0 2px}
.list div{display:flex;gap:10px;padding:10px 14px;border-radius:12px;background:var(--in);font-size:14px}
.list .no{color:var(--mu)}
.st{margin:0;min-height:1.3em;font-size:13px;color:var(--mu);opacity:0;transition:opacity .3s}
.btns{display:flex;gap:10px;flex-wrap:wrap}
button{font:inherit;font-size:14px;cursor:pointer;border:0;border-radius:12px;padding:11px 22px;transition:.2s}
button.pri{background:linear-gradient(135deg,var(--ac),var(--ac2));color:#fff;font-weight:600;box-shadow:0 8px 24px rgba(109,124,255,.4)}
button.pri:hover{transform:translateY(-1px)}
button.pri:disabled{opacity:.45;cursor:default;transform:none}
button.gh{background:var(--in);color:var(--tx)}
button.gh:hover{background:rgba(127,127,127,.22)}
@media (prefers-reduced-motion:reduce){.bg i,.ico{animation:none}}
</style></head><body data-mode="@MODE@">
<div class="bg"><i></i><i></i><i></i></div>
<div class="wrap"><div class="card">
 <div class="ico"><svg viewBox="0 0 24 24" width="38" height="38" fill="none" stroke="#fff" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M2 8.8a15 15 0 0 1 4.2-2.5"/><path d="M22 8.8a15 15 0 0 0-9.4-3.7"/><path d="M5 12.9a10 10 0 0 1 3.4-2.1"/><path d="M19 12.9a10 10 0 0 0-4-2.4"/><path d="M8.5 16.4a5 5 0 0 1 7 0"/><path d="M12 20h.01"/><path d="M3 3l18 18"/></svg></div>
 <h1 id="t"></h1><p class="sub" id="u"></p>
 <div class="list">
  <div>✅  Painel, configurações e atalhos</div>
  <div>✅  Tema claro / escuro</div>
  <div class="no">🌐  Chat e login precisam de internet</div>
 </div>
 <p class="st" id="st">@NOTE@</p>
 <div class="btns">
  <button class="pri" id="retry">Tentar novamente</button>
  <button class="pri" id="online">Desativar modo offline</button>
  <button class="gh" id="panel">Abrir painel</button>
 </div>
</div></div>
<script>
var $=function(i){return document.getElementById(i)};
var manual=document.body.getAttribute('data-mode')==='manual';
function send(m){try{window.chrome.webview.postMessage(m)}catch(e){}}
function status(m){var s=$('st');s.textContent=m;s.style.opacity=1}
$('t').textContent=manual?'Modo offline ativado':'Você está sem internet';
$('u').textContent=manual?'O app não está usando a internet. Desative o modo offline quando quiser voltar ao chat.':'Reconectamos sozinhos assim que a conexão voltar. Enquanto isso, o painel continua funcionando.';
$('retry').style.display=manual?'none':'';
$('online').style.display=manual?'':'none';
$('retry').onclick=function(){var b=this;b.disabled=true;status('Verificando a conexão…');send('offline:retry');setTimeout(function(){b.disabled=false},3500)};
$('online').onclick=function(){status('Reconectando…');send('offline:online')};
$('panel').onclick=function(){send('offline:panel')};
if($('st').textContent)$('st').style.opacity=1;
window.__azOff={status:status};
</script></body></html>
""";
}
