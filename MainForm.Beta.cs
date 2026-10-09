using System.Diagnostics;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AzulGroove;

/// <summary>
/// Tudo que é específico da versão BETA fica aqui, separado do resto do app:
/// atualização automática, voltar para a versão normal, diálogo de boas-vindas e saudação com o nome.
/// </summary>
public partial class MainForm
{
    // >>> Mude aqui a cada nova beta (e use a MESMA string na tag do GitHub: v1.0.1b, v1.0.2b ...) <<<
    internal const string AppVersion = "1.0.0b";

    const string BetaRepo = "astmsoftware/Azul-Groove-app-beta";   // releases: github.com/astmsoftware/Azul-Groove-app-beta/releases
    const string StableRepo = "andrsodremiranda/Azul-Groove-app";  // versão normal: github.com/andrsodremiranda/Azul-Groove-app/releases

    bool betaBusy, betaGreetPending;
    TaskCompletionSource<BetaWizardOutcome?>? betaWizardTcs; // != null enquanto o assistente de configuração está na tela

    static readonly string[] BetaTypes = { "Ouvinte", "DJ / Produtor", "Streamer", "Dono de servidor", "Outro" };

    static readonly HttpClient BetaHttp = MakeBetaHttp();

    static HttpClient MakeBetaHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("AzulGroove/" + AppVersion); // a API do GitHub exige User-Agent
        h.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return h;
    }

    sealed record BetaRelease(string Tag, string AssetName, string AssetUrl, string Notes, string PageUrl);
    sealed record BetaSetupResult(string Name, string Type, int Theme, int Start, bool Hotkey, int Preset, bool Auto);
    sealed record BetaWizardOutcome(BetaSetupResult? Result, bool Skipped);

    // ================= Mensagens vindas do painel / do assistente =================
    // O painel (página local) pode mandar:  'beta:check' | 'beta:rollback' | 'beta:setup' | 'beta:save:{json}'
    // O assistente de configuração manda um JSON  {"kind":"done|skip|check", ...}
    bool HandleBetaMessage(CoreWebView2WebMessageReceivedEventArgs e)
    {
        // só aceita de páginas locais; páginas https (o site) nunca disparam atualização/rollback
        if (e.Source.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return false;
        string? t;
        try { t = e.TryGetWebMessageAsString(); } catch { return false; }
        if (t == null) return false;

        if (t.StartsWith("{"))
        {
            if (betaWizardTcs == null) return false;
            BetaOnWizardMessage(t);
            return true;
        }
        if (t.StartsWith("beta:save:"))
        {
            BetaSavePanel(t["beta:save:".Length..]);
            return true;
        }
        switch (t)
        {
            case "beta:check": _ = BetaCheckUpdateAsync(true); return true;
            case "beta:rollback": _ = BetaRollbackAsync(); return true;
            case "beta:setup": BeginInvoke(() => _ = BetaReopenSetupAsync()); return true;
            default: return false;
        }
    }

    static string BetaClean(string? s)
    {
        var t = new string((s ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return t.Length > 40 ? t[..40] : t;
    }

    void BetaSavePanel(string json)
    {
        try
        {
            using var d = JsonDocument.Parse(json);
            var r = d.RootElement;
            cfg.UserName = BetaClean(r.GetProperty("name").GetString());
            cfg.UserType = BetaClean(r.GetProperty("type").GetString());
            cfg.AutoUpdate = r.GetProperty("auto").GetBoolean();
            cfg.Save();
        }
        catch { /* mensagem inválida: ignora */ }
    }

    Form? BetaOwner => Form.ActiveForm is { IsDisposed: false } f ? f : (Visible ? this : null);

    DialogResult BetaBox(string text, MessageBoxButtons b = MessageBoxButtons.OK, MessageBoxIcon i = MessageBoxIcon.Information)
        => MessageBox.Show(BetaOwner, text, "Azul Groove Beta", b, i);

    // ================= Atualização automática =================
    async Task BetaAfterStartAsync()
    {
        if (!cfg.AutoUpdate || IsDisposed || IsOffline) return;
        if (DateTime.UtcNow - cfg.LastUpdateCheck < TimeSpan.FromHours(4)) return; // não incomoda a cada abertura
        await Task.Delay(4000);
        if (IsDisposed) return;
        await BetaCheckUpdateAsync(false);
    }

    async Task BetaCheckUpdateAsync(bool manual)
    {
        if (IsOffline)
        {
            if (manual) BetaBox("Você está sem internet ou no modo offline.\n\nConecte-se para verificar atualizações.");
            return;
        }
        if (betaBusy) return;
        betaBusy = true;
        try
        {
            var rel = await BetaFetchLatestAsync(BetaRepo, allowPrerelease: true);
            cfg.LastUpdateCheck = DateTime.UtcNow;
            cfg.Save();

            if (rel == null)
            {
                if (manual) BetaBox("Ainda não há nenhuma versão com instalador publicada na página de releases do beta.");
                return;
            }
            if (BetaCompare(rel.Tag, AppVersion) <= 0)
            {
                if (manual) BetaBox($"Você já está na versão mais recente ({AppVersion}). ✅");
                return;
            }

            var notes = rel.Notes.Length > 500 ? rel.Notes[..500] + "…" : rel.Notes;
            var ask = BetaBox(
                $"Nova versão disponível: {rel.Tag}\nVocê está na {AppVersion}.\n\n{notes}\n\n" +
                "Baixar e instalar agora? O app será fechado e o instalador abre em seguida.",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (ask == DialogResult.Yes) await BetaInstallAsync(rel);
        }
        catch (Exception ex)
        {
            if (manual)
                BetaBox($"Não consegui verificar atualizações agora.\n{ex.Message}\n\nVocê pode olhar manualmente em:\nhttps://github.com/{BetaRepo}/releases",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { betaBusy = false; }
    }

    // ================= Voltar para a versão normal (modo silencioso) =================
    // A confirmação é feita no próprio painel (botão em dois cliques). Aqui não aparece nenhuma janelinha:
    // baixa a versão normal, fecha o app, instala escondido e abre o app de novo sozinho.
    async Task BetaRollbackAsync()
    {
        if (betaBusy) return;
        if (IsOffline) { await BetaPanelStatusAsync("Sem internet (ou modo offline ligado): conecte-se para voltar à versão normal."); return; }
        betaBusy = true;
        try
        {
            await BetaPanelStatusAsync("Procurando a versão normal…");
            var rel = await BetaFetchLatestAsync(StableRepo, allowPrerelease: false);
            if (rel == null)
            {
                await BetaPanelStatusAsync("Não achei o instalador da versão normal. Abrindo a página de downloads no navegador…");
                OpenExternal($"https://github.com/{StableRepo}/releases/latest");
                return;
            }

            var file = await BetaDownloadAsync(rel, pct => _ = BetaPanelStatusAsync($"Baixando a versão normal… {pct}%"));
            await BetaPanelStatusAsync("Pronto! Instalando em segundo plano — o app vai reiniciar sozinho…");
            await Task.Delay(1500);
            BetaRunSilentAndRestart(file);
        }
        catch (Exception ex)
        {
            Text = "Azul Groove";
            await BetaPanelStatusAsync("Não consegui baixar agora (" + ex.Message + "). Abrindo a página de downloads no navegador…");
            OpenExternal($"https://github.com/{StableRepo}/releases/latest");
        }
        finally { betaBusy = false; }
    }

    // Um .cmd escondido espera o app fechar, roda o instalador sem janelas e abre o app de novo.
    void BetaRunSilentAndRestart(string setupFile)
    {
        var exe = Application.ExecutablePath;
        var cmd = Path.Combine(Path.GetDirectoryName(setupFile)!, "reiniciar.cmd");
        var lines = new[]
        {
            "@echo off",
            "chcp 65001 >nul",
            "ping -n 3 127.0.0.1 >nul",   // dá tempo do app fechar
            $"start \"\" /wait \"{setupFile}\" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /CLOSEAPPLICATIONS",
            $"if exist \"{exe}\" start \"\" \"{exe}\""
        };
        File.WriteAllText(cmd, string.Join("\r\n", lines) + "\r\n", new System.Text.UTF8Encoding(false));
        Process.Start(new ProcessStartInfo(cmd) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });

        exiting = true; // fecha de vez (não vai para a bandeja)
        Close();
    }

    async Task BetaPanelStatusAsync(string msg)
    {
        try
        {
            if (web.CoreWebView2 == null || IsDisposed) return;
            await web.CoreWebView2.ExecuteScriptAsync(
                $"window.__azBeta&&window.__azBeta.status({JsonSerializer.Serialize(msg)})");
        }
        catch { /* o painel pode não estar aberto: tudo bem */ }
    }

    // ================= GitHub Releases =================
    static async Task<BetaRelease?> BetaFetchLatestAsync(string repo, bool allowPrerelease)
    {
        var json = await BetaHttp.GetStringAsync($"https://api.github.com/repos/{repo}/releases?per_page=10");
        using var doc = JsonDocument.Parse(json);
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (r.GetProperty("draft").GetBoolean()) continue;
            if (!allowPrerelease && r.GetProperty("prerelease").GetBoolean()) continue;

            string? name = null, url = null;
            foreach (var a in r.GetProperty("assets").EnumerateArray())
            {
                var n = a.GetProperty("name").GetString() ?? "";
                if (n.EndsWith("Setup.exe", StringComparison.OrdinalIgnoreCase)) // AzulGroove-Setup.exe (gerado pelo workflow)
                {
                    name = n;
                    url = a.GetProperty("browser_download_url").GetString();
                    break;
                }
            }
            if (url == null || name == null) continue;

            var tag = r.GetProperty("tag_name").GetString() ?? "";
            var body = r.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
            var page = r.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";
            return new BetaRelease(tag, name, url, body.Trim(), page);
        }
        return null;
    }

    // Compara "v1.0.0b" com "1.0.1b": primeiro a parte numérica; se empatar, a versão sem sufixo (estável) vale mais que "b"
    static int BetaCompare(string a, string b)
    {
        static (Version v, string suffix) Parse(string s)
        {
            s = s.Trim().TrimStart('v', 'V');
            int i = 0;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
            var num = s[..i].Trim('.');
            var suf = s[i..].Trim().ToLowerInvariant();
            if (!Version.TryParse(num, out var v) && !Version.TryParse(num + ".0", out v)) v = new Version(0, 0);
            return (v, suf);
        }
        var (va, sa) = Parse(a);
        var (vb, sb) = Parse(b);
        int c = va.CompareTo(vb);
        if (c != 0) return c;
        if (sa == sb) return 0;
        if (sa.Length == 0) return 1;
        if (sb.Length == 0) return -1;
        return string.CompareOrdinal(sa, sb);
    }

    async Task BetaInstallAsync(BetaRelease rel)
    {
        var oldTitle = Text;
        try
        {
            tray.ShowBalloonTip(2500, "Azul Groove Beta", $"Baixando a versão {rel.Tag}…", ToolTipIcon.Info);
            var file = await BetaDownloadAsync(rel);
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
            exiting = true; // fecha de vez (não vai para a bandeja) para o instalador poder substituir o .exe
            Close();
        }
        catch (Exception ex)
        {
            Text = oldTitle;
            BetaBox($"Não consegui baixar/instalar.\n{ex.Message}\n\nBaixe manualmente em:\n{rel.PageUrl}",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    async Task<string> BetaDownloadAsync(BetaRelease rel, Action<int>? onProgress = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "AzulGrooveUpdate");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, rel.AssetName);

        using var resp = await BetaHttp.GetAsync(rel.AssetUrl, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? -1;

        await using (var src = await resp.Content.ReadAsStreamAsync())
        await using (var dst = File.Create(file))
        {
            var buf = new byte[81920];
            long done = 0; int n, last = -1;
            while ((n = await src.ReadAsync(buf)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n));
                done += n;
                if (total > 0)
                {
                    int pct = (int)(done * 100 / total);
                    if (pct != last) { last = pct; Text = $"Azul Groove — baixando atualização… {pct}%"; onProgress?.Invoke(pct); }
                }
            }
        }
        return file;
    }

    // ================= Saudação com o nome (digitando) =================
    void BetaOnNavigationCompleted()
    {
        if (panelActive && betaWizardTcs == null) _ = BetaInjectPanelAsync(); // opções beta dentro do painel
        if (!betaGreetPending) return;
        betaGreetPending = false;
        _ = BetaGreetAsync();
    }

    async Task BetaGreetAsync()
    {
        try
        {
            bool updated = cfg.LastVersion.Length > 0 && cfg.LastVersion != AppVersion;
            cfg.LastVersion = AppVersion;
            cfg.Save();

            var who = string.IsNullOrWhiteSpace(cfg.UserName) ? "Olá!" : $"Olá, {cfg.UserName}!";
            var type = string.IsNullOrWhiteSpace(cfg.UserType) ? "" : $"  ·  {cfg.UserType}";
            var msg = updated
                ? $"{who}{type}  ·  Atualizado para a versão {AppVersion} ✨"
                : $"{who}{type}  ·  Bem-vindo de volta 🎧";

            await web.CoreWebView2.ExecuteScriptAsync(BetaGreetScript.Replace("@MSG@", JsonSerializer.Serialize(msg)));
        }
        catch { /* a saudação é só enfeite */ }
    }

    // Usa só CSSOM / Web Animations (funciona mesmo em sites com CSP restritivo)
    const string BetaGreetScript = """
(function(msg){try{
 var d=document.createElement('div');
 d.style.cssText='position:fixed;left:50%;top:16px;z-index:2147483647;padding:12px 22px;border-radius:16px;background:rgba(22,25,38,.92);color:#fff;font:600 14px "Segoe UI",system-ui,sans-serif;border:1px solid rgba(255,255,255,.14);box-shadow:0 12px 40px rgba(0,0,0,.45);pointer-events:none;opacity:0;white-space:nowrap;transform:translateX(-50%)';
 (document.body||document.documentElement).appendChild(d);
 d.animate([{opacity:0,transform:'translate(-50%,-24px)'},{opacity:1,transform:'translate(-50%,0)'}],{duration:450,easing:'cubic-bezier(.2,.9,.3,1)',fill:'forwards'});
 var a=Array.from(msg),i=0,t=setInterval(function(){
  d.textContent=a.slice(0,++i).join('');
  if(i>=a.length){clearInterval(t);setTimeout(function(){
   var o=d.animate([{opacity:1},{opacity:0}],{duration:500,fill:'forwards'});o.onfinish=function(){d.remove()};},3200);}
 },32);
}catch(e){}})(@MSG@);
""";

    // ================= Assistente de configuração (dentro da própria janela do app) =================
    // Primeira abertura: aparece SÓ o assistente; quando termina, o app segue para a animação e abre normalmente.
    async Task BetaRunSetupAsync()
    {
        if (betaWizardTcs != null || web.CoreWebView2 == null) return;
        var tcs = betaWizardTcs = new TaskCompletionSource<BetaWizardOutcome?>(TaskCreationOptions.RunContinuationsAsynchronously);
        FormClosingEventHandler onClosing = (_, _) => tcs.TrySetResult(null); // fechou a janela no meio: não trava
        FormClosing += onClosing;
        try
        {
            var labels = JsonSerializer.Serialize(Presets.Select(p => p.Label).ToArray());
            var init = cfg.SetupDone
                ? JsonSerializer.Serialize(new
                {
                    name = cfg.UserName, type = cfg.UserType,
                    theme = cfg.Theme == 1 ? "light" : cfg.Theme == 2 ? "dark" : "auto",
                    start = cfg.StartPage == 1 ? "site" : "panel",
                    hk = cfg.HotkeyOn, preset = cfg.Preset, auto = cfg.AutoUpdate
                })
                : "null";
            var html = BetaWelcomeHtml.Replace("@PRESETS@", labels).Replace("@VER@", AppVersion).Replace("@INIT@", init);

            web.CoreWebView2.NavigateToString(html);
            var outcome = await tcs.Task;

            if (outcome?.Result != null) BetaApplySetup(outcome.Result);
            else if (outcome?.Skipped == true) { cfg.SetupDone = true; cfg.Save(); }
        }
        finally
        {
            FormClosing -= onClosing;
            betaWizardTcs = null;
        }
    }

    // Reabrir o assistente pelo botão do painel: usa a mesma janela e volta para o painel no fim
    async Task BetaReopenSetupAsync()
    {
        if (betaWizardTcs != null) return;
        ShowWindow();
        await BetaRunSetupAsync();
        if (!IsDisposed) ShowPanel();
    }

    void BetaOnWizardMessage(string json)
    {
        var tcs = betaWizardTcs;
        if (tcs == null) return;
        try
        {
            using var d = JsonDocument.Parse(json);
            var r = d.RootElement;
            switch (r.GetProperty("kind").GetString())
            {
                case "check":
                    _ = BetaCheckUpdateAsync(true);
                    break;
                case "skip":
                    tcs.TrySetResult(new BetaWizardOutcome(null, true));
                    break;
                case "done":
                    tcs.TrySetResult(new BetaWizardOutcome(new BetaSetupResult(
                        BetaClean(r.GetProperty("name").GetString()),
                        BetaClean(r.GetProperty("type").GetString()),
                        r.GetProperty("theme").GetInt32(),
                        r.GetProperty("start").GetInt32(),
                        r.GetProperty("hk").GetBoolean(),
                        r.GetProperty("preset").GetInt32(),
                        r.GetProperty("auto").GetBoolean()), false));
                    break;
            }
        }
        catch { /* mensagem inválida: ignora */ }
    }

    void BetaApplySetup(BetaSetupResult r)
    {
        cfg.UserName = r.Name;
        cfg.UserType = r.Type;
        cfg.StartPage = Math.Clamp(r.Start, 0, 2);   // 0 = painel, 1 = site
        cfg.AutoUpdate = r.Auto;
        cfg.Preset = Math.Clamp(r.Preset, 0, Presets.Length - 1);
        cfg.Theme = Math.Clamp(r.Theme, 0, 2);
        cfg.HotkeyOn = r.Hotkey;
        cfg.SetupDone = true;
        cfg.Save();

        // mantém os menus da bandeja iguais ao que foi escolhido
        for (int j = 0; j < miKeys.DropDownItems.Count; j++)
            ((ToolStripMenuItem)miKeys.DropDownItems[j]).Checked = j == cfg.Preset;
        for (int j = 0; j < miTheme.DropDownItems.Count; j++)
            ((ToolStripMenuItem)miTheme.DropDownItems[j]).Checked = j == cfg.Theme;
        miHotkeyOn.Checked = cfg.HotkeyOn;

        ApplyThemeMode(pushToSite: true);
        RegisterHotkey();
        UpdateLabels();
    }

    // ================= Opções beta dentro do painel =================
    // Injeta um bloco "Beta" no fim da página do painel (nome, tipo, atualização, configuração inicial, voltar à versão normal).
    async Task BetaInjectPanelAsync()
    {
        try
        {
            if (web.CoreWebView2 == null || IsDisposed) return;
            var state = JsonSerializer.Serialize(new
            {
                ver = AppVersion, name = cfg.UserName, type = cfg.UserType, auto = cfg.AutoUpdate, types = BetaTypes, offline = cfg.OfflineMode
            });
            await web.CoreWebView2.ExecuteScriptAsync(BetaPanelScript.Replace("@STATE@", state));
        }
        catch { /* o painel funciona sem o bloco */ }
    }

    const string BetaPanelScript = """
(function(S){try{
 if(location.protocol==='https:')return;
 var old=document.getElementById('az-beta');if(old)old.remove();
 var w=document.querySelector('.w');if(!w||typeof ddify!=='function')return;
 function h(t,c,x){var e=document.createElement(t);if(c)e.className=c;if(x!=null)e.textContent=x;return e}
 function post(m){try{window.chrome.webview.postMessage(m)}catch(e){}}
 function row(card,t,sub,ctl){var r=h('div','r'),l=h('div');l.appendChild(document.createTextNode(t));if(sub)l.appendChild(h('small','',sub));r.appendChild(l);r.appendChild(ctl);card.appendChild(r)}
 function toggle(on,cb){var lb=h('label','sw'),i=h('input');i.type='checkbox';i.checked=!!on;i.onchange=function(){cb(i.checked)};lb.appendChild(i);lb.appendChild(h('span'));lb.set=function(v){i.checked=!!v};return lb}
 var root=h('div');root.id='az-beta';
 var timer;
 function save(){clearTimeout(timer);post('beta:save:'+JSON.stringify({name:name.value,type:type.value,auto:S.auto}))}

 root.appendChild(h('h2','','Seu perfil'));
 var c1=h('div','c');root.appendChild(c1);
 var name=h('input','in');name.maxLength=40;name.placeholder='Seu nome';name.value=S.name||'';
 name.oninput=function(){clearTimeout(timer);timer=setTimeout(save,500)};name.onblur=save;
 row(c1,'Nome','Usado na saudação ao abrir o app',name);
 var tl=S.types.slice();if(S.type&&tl.indexOf(S.type)<0)tl.push(S.type);
 var type=h('select');tl.forEach(function(t){var o=h('option','',t);o.value=t;type.appendChild(o)});type.value=S.type||tl[0];
 var tw=h('div');tw.appendChild(type);ddify(type);type.onchange=save;
 row(c1,'Tipo de usuário','Como você usa o Azul Groove',tw);

 root.appendChild(h('h2','','Azul Groove Beta · versão '+S.ver));
 var c2=h('div','c');root.appendChild(c2);
 var osw=toggle(S.offline,function(v){S.offline=v;post('offline:set:'+(v?'1':'0'))});
 row(c2,'Modo offline','Não usa a internet: o painel continua funcionando; site e chat ficam indisponíveis',osw);
 var asw=toggle(S.auto,function(v){S.auto=v;save()});
 row(c2,'Verificar atualização ao abrir o app','Avisa quando sair uma versão nova',asw);
 var chk=h('button','btn','Verificar');chk.onclick=function(){post('beta:check')};
 row(c2,'Atualizações','Procura uma versão nova agora',chk);
 var cfgb=h('button','btn o','Refazer');cfgb.onclick=function(){post('beta:setup')};
 row(c2,'Configuração inicial','Nome, tipo de usuário, tema e atalho',cfgb);
 var box=h('div');box.style.cssText='display:flex;gap:8px;flex-wrap:wrap;justify-content:flex-end';
 var rb=h('button','btn dg','Voltar para a versão normal'),cn=h('button','btn o','Cancelar');cn.style.display='none';
 var armed=false;
 function reset(){armed=false;rb.textContent='Voltar para a versão normal';cn.style.display='none'}
 rb.onclick=function(){
  if(!armed){armed=true;rb.textContent='Confirmar: instalar e reiniciar';cn.style.display='';return}
  rb.disabled=cn.disabled=chk.disabled=cfgb.disabled=true;status('Iniciando…');post('beta:rollback')};
 cn.onclick=reset;box.appendChild(cn);box.appendChild(rb);
 row(c2,'Versão normal','Instala a versão estável em segundo plano e reinicia o app. Seu login e suas configurações ficam salvos.',box);
 var st=h('div');st.id='az-st';root.appendChild(st);
 function status(t){st.style.display='block';st.textContent=t}
 window.__azBeta={status:status,offline:function(v){S.offline=!!v;osw.set(S.offline)}};
 w.insertBefore(root,w.querySelector('.ft'));
}catch(e){}})(@STATE@);
""";

    // Fundo animado estilo Windows 11 ("bloom" em movimento + vidro fosco) e textos com efeito de digitação
    const string BetaWelcomeHtml = """
<!doctype html><html lang="pt-BR" data-theme="dark"><head><meta charset="utf-8"><title>Azul Groove — Configuração</title><meta name="color-scheme" content="dark light"><style>
:root{--bg:#0a0c13;--card:rgba(20,23,36,.58);--bd:rgba(255,255,255,.13);--tx:#f3f5fb;--mu:#a3abc0;--ac:#6d7cff;--ac2:#9a6bff;--in:rgba(255,255,255,.08);--bl:.7;--pop:#1b1f2e}
html[data-theme=light]{--bg:#eaeefb;--card:rgba(255,255,255,.62);--bd:rgba(0,0,0,.08);--tx:#151823;--mu:#5b6277;--in:rgba(0,0,0,.05);--bl:.55;--pop:#fbfcff}
*{box-sizing:border-box}
html,body{height:100%;margin:0;overflow:hidden;background:var(--bg);color:var(--tx);font-family:"Segoe UI Variable","Segoe UI",system-ui,sans-serif;transition:background .5s}
.bg{position:fixed;inset:0;overflow:hidden}
.bg i{position:absolute;border-radius:50%;filter:blur(80px);opacity:var(--bl);will-change:transform}
.bg i:nth-child(1){width:560px;height:560px;left:-120px;top:-140px;background:#5865f2;animation:d1 16s ease-in-out infinite alternate}
.bg i:nth-child(2){width:500px;height:500px;right:-100px;top:-60px;background:#b45cff;animation:d2 19s ease-in-out infinite alternate}
.bg i:nth-child(3){width:520px;height:520px;left:20%;bottom:-220px;background:#1fa8ff;animation:d3 22s ease-in-out infinite alternate}
.bg i:nth-child(4){width:380px;height:380px;right:8%;bottom:-120px;background:#ff5fa8;animation:d4 17s ease-in-out infinite alternate}
@keyframes d1{to{transform:translate(260px,180px) scale(1.2)}}
@keyframes d2{to{transform:translate(-280px,220px) scale(.85)}}
@keyframes d3{to{transform:translate(200px,-200px) scale(1.15)}}
@keyframes d4{to{transform:translate(-220px,-160px) scale(1.25)}}
.wrap{position:relative;height:100%;display:flex;align-items:center;justify-content:center;padding:22px}
.card{width:100%;max-width:620px;min-height:420px;padding:34px 38px 24px;border-radius:24px;background:var(--card);border:1px solid var(--bd);backdrop-filter:blur(30px) saturate(1.5);box-shadow:0 24px 80px rgba(0,0,0,.35);display:flex;flex-direction:column;animation:cin .7s cubic-bezier(.2,.9,.3,1) both}
@keyframes cin{from{opacity:0;transform:translateY(24px) scale(.97)}}
.step{display:none;flex:1;flex-direction:column;gap:14px}
.step.on{display:flex;animation:st .45s ease both}
@keyframes st{from{opacity:0;transform:translateX(24px)}}
h1,h2{margin:0;font-weight:600;letter-spacing:.2px;min-height:1.3em}
h1{font-size:30px}h2{font-size:25px}
.sub{margin:0;color:var(--mu);font-size:15px;min-height:1.4em}
.caret::after{content:'';display:inline-block;width:2px;height:1em;margin-left:3px;vertical-align:-.12em;background:var(--ac);animation:bl 1s steps(1) infinite}
@keyframes bl{50%{opacity:0}}
.logo{width:72px;height:72px;border-radius:20px;background:linear-gradient(135deg,#5865f2,#7a5cff);display:flex;align-items:center;justify-content:center;gap:6px;box-shadow:0 12px 36px rgba(88,101,242,.5)}
.logo i{display:block;width:7px;height:22px;border-radius:4px;background:#fff;animation:eq 1s ease-in-out infinite}
.logo i:nth-child(1){animation-delay:-.9s}.logo i:nth-child(2){animation-delay:-.65s}.logo i:nth-child(3){animation-delay:-.4s}.logo i:nth-child(4){animation-delay:-.75s}.logo i:nth-child(5){animation-delay:-.2s}
@keyframes eq{0%,100%{height:10px}50%{height:38px}}
.badge{align-self:flex-start;padding:4px 12px;border-radius:30px;background:rgba(109,124,255,.25);font-size:12px;font-weight:600;letter-spacing:.6px}
input{width:100%;padding:13px 16px;border-radius:12px;border:1px solid var(--bd);background:var(--in);color:var(--tx);font:inherit;font-size:16px;outline:none;transition:.2s}
input:focus{border-color:var(--ac);box-shadow:0 0 0 3px rgba(109,124,255,.25)}
.chips{display:flex;flex-wrap:wrap;gap:8px}
.chip{padding:9px 15px;border-radius:30px;background:var(--in);border:1px solid transparent;cursor:pointer;font-size:14px;transition:.2s;user-select:none}
.chip:hover{transform:translateY(-1px)}
.chip.sel{border-color:var(--ac);background:rgba(109,124,255,.22)}
.row{display:flex;align-items:center;justify-content:space-between;gap:14px;padding:11px 14px;border-radius:14px;background:var(--in)}
.row b{font-weight:600;font-size:14px;display:block}
.row small{color:var(--mu);font-size:12px}
.ctl{display:flex;gap:10px;align-items:center}
.seg{display:flex;background:rgba(127,127,127,.18);border-radius:10px;padding:3px;gap:2px}
.seg button{border:0;background:transparent;color:var(--tx);padding:6px 12px;border-radius:8px;font:inherit;font-size:13px;cursor:pointer;transition:.2s}
.seg button.sel{background:var(--ac);color:#fff}
.sw{position:relative;width:44px;height:24px;border-radius:24px;background:rgba(127,127,127,.4);cursor:pointer;flex:none;transition:.25s}
.sw::after{content:'';position:absolute;left:3px;top:3px;width:18px;height:18px;border-radius:50%;background:#fff;transition:.25s}
.sw.on{background:var(--ac)}.sw.on::after{left:23px}
.dd{position:relative;min-width:170px}
.ddb{width:100%;text-align:left;border:1px solid var(--bd);background:var(--in);color:var(--tx);font:inherit;font-size:13px;padding:8px 32px 8px 12px;border-radius:10px;cursor:pointer;position:relative;transition:.2s}
.ddb:hover{border-color:var(--ac)}
.ddb::after{content:'';position:absolute;right:12px;top:50%;width:7px;height:7px;border-right:2px solid currentColor;border-bottom:2px solid currentColor;transform:translateY(-70%) rotate(45deg);opacity:.7;transition:.2s}
.dd.open .ddb::after{transform:translateY(-30%) rotate(225deg)}
.dd.dis{opacity:.45;pointer-events:none}
.ddl{display:none;position:absolute;left:0;right:0;bottom:calc(100% + 6px);z-index:20;padding:5px;border-radius:12px;background:var(--pop);border:1px solid var(--bd);box-shadow:0 14px 40px rgba(0,0,0,.4)}
.dd.open .ddl{display:block}
.ddo{padding:8px 11px;border-radius:8px;cursor:pointer;font-size:13px}
.ddo:hover{background:rgba(109,124,255,.22)}.ddo.sel{background:var(--ac);color:#fff}
html[data-theme=dark]{color-scheme:dark}html[data-theme=light]{color-scheme:light}
.sum{display:flex;flex-direction:column;gap:8px;margin-top:6px}
.sum div{padding:10px 14px;border-radius:12px;background:var(--in);font-size:14px;animation:st .5s ease both}
.nav{display:flex;align-items:center;justify-content:space-between;margin-top:auto;padding-top:16px}
.nav>div:last-child{display:flex;gap:8px;align-items:center}
.dots{display:flex;gap:7px}
.dots b{width:7px;height:7px;border-radius:50%;background:rgba(127,127,127,.45);transition:.3s}
.dots b.on{width:22px;background:var(--ac)}
button.pri{border:0;padding:11px 24px;border-radius:12px;background:linear-gradient(135deg,var(--ac),var(--ac2));color:#fff;font:inherit;font-weight:600;font-size:14px;cursor:pointer;box-shadow:0 8px 24px rgba(109,124,255,.4);transition:.2s}
button.pri:hover{transform:translateY(-1px)}
button.pri:disabled{opacity:.4;cursor:default;transform:none}
button.gh{border:0;background:transparent;color:var(--mu);padding:10px 12px;border-radius:10px;font:inherit;font-size:13px;cursor:pointer}
button.gh:hover{color:var(--tx);background:var(--in)}
@media (prefers-reduced-motion:reduce){.bg i{animation:none}}
</style></head><body>
<div class="bg"><i></i><i></i><i></i><i></i></div>
<div class="wrap"><div class="card">
 <section class="step on" id="s0">
  <div class="logo"><i></i><i></i><i></i><i></i><i></i></div>
  <span class="badge">BETA @VER@</span>
  <h1 id="t0"></h1><p class="sub" id="u0"></p>
 </section>
 <section class="step" id="s1">
  <h2 id="t1"></h2><p class="sub" id="u1"></p>
  <input id="name" maxlength="40" placeholder="Seu nome" autocomplete="off" spellcheck="false">
  <p class="sub" style="min-height:0;margin-top:6px">Você usa o Azul Groove como…</p>
  <div class="chips" id="types"></div>
 </section>
 <section class="step" id="s2">
  <h2 id="t2"></h2>
  <div class="row"><div><b>Tema</b><small>Escuro é o padrão</small></div>
   <div class="seg" id="segTheme"><button data-v="auto">Auto</button><button data-v="light">Claro</button><button data-v="dark">Escuro</button></div></div>
  <div class="row"><div><b>Ao abrir o app, mostrar</b><small>Sua página inicial</small></div>
   <div class="seg" id="segStart"><button data-v="panel">Painel</button><button data-v="site">Site</button></div></div>
  <div class="row"><div><b>Atalho para abrir o chat</b><small>Funciona mesmo com o app minimizado</small></div>
   <div class="ctl"><div class="dd" id="preset"></div><div class="sw on" id="swHk"></div></div></div>
  <div class="row"><div><b>Atualização automática</b><small>Avisa quando sair uma versão nova</small></div>
   <div class="ctl"><button class="gh" id="chk">Verificar agora</button><div class="sw on" id="swAuto"></div></div></div>
 </section>
 <section class="step" id="s3">
  <h2 id="t3"></h2><p class="sub" id="u3"></p>
  <div class="sum" id="sum"></div>
 </section>
 <div class="nav">
  <div><button class="gh" id="skip">Pular</button><button class="gh" id="back">Voltar</button></div>
  <div class="dots"><b class="on"></b><b></b><b></b><b></b></div>
  <div><button class="pri" id="next">Começar</button></div>
 </div>
</div></div>
<script>
var PRESETS=@PRESETS@;
var TYPES=[['🎧','Ouvinte'],['🎛️','DJ / Produtor'],['📺','Streamer'],['👑','Dono de servidor'],['✨','Outro']];
var THEMES={auto:0,light:1,dark:2},THEME_NAMES={auto:'Automático',light:'Claro',dark:'Escuro'};
var S={step:0,name:'',type:'Ouvinte',theme:'dark',start:'panel',hk:true,preset:0,auto:true};
var INIT=@INIT@;if(INIT){for(var k in INIT){if(INIT[k]!==''&&INIT[k]!=null)S[k]=INIT[k]}}
var $=function(i){return document.getElementById(i)};
var mq=matchMedia('(prefers-color-scheme: light)');
function applyTheme(){var t=S.theme==='auto'?(mq.matches?'light':'dark'):S.theme;document.documentElement.setAttribute('data-theme',t)}
function send(o){try{window.chrome.webview.postMessage(JSON.stringify(o))}catch(e){}}
function typeText(el,text,speed,done){
 clearInterval(el._t);el.textContent='';el.classList.add('caret');var a=Array.from(text),i=0;
 el._t=setInterval(function(){el.textContent=a.slice(0,++i).join('');
  if(i>=a.length){clearInterval(el._t);setTimeout(function(){el.classList.remove('caret')},900);if(done)done();}},speed);
}
function seg(id,key,cb){
 var box=$(id),bs=box.querySelectorAll('button');
 function mark(){bs.forEach(function(b){b.classList.toggle('sel',b.getAttribute('data-v')===S[key])})}
 bs.forEach(function(b){b.onclick=function(){S[key]=b.getAttribute('data-v');mark();if(cb)cb()}});mark();
}
function sw(id,key,cb){var e=$(id);e.onclick=function(){S[key]=!S[key];e.classList.toggle('on',S[key]);if(cb)cb()}}
TYPES.forEach(function(t){
 var c=document.createElement('div');c.className='chip'+(t[1]===S.type?' sel':'');c.textContent=t[0]+'  '+t[1];
 c.onclick=function(){S.type=t[1];document.querySelectorAll('.chip').forEach(function(x){x.classList.remove('sel')});c.classList.add('sel')};
 $('types').appendChild(c);
});
function ddMark(){var box=$('preset');box.querySelector('.ddb').textContent=PRESETS[S.preset];box.querySelectorAll('.ddo').forEach(function(o,i){o.classList.toggle('sel',i===S.preset)});box.classList.toggle('dis',!S.hk)}
function ddInit(){var box=$('preset');box.innerHTML='';var bt=document.createElement('button');bt.type='button';bt.className='ddb';var l=document.createElement('div');l.className='ddl';
 PRESETS.forEach(function(p,i){var o=document.createElement('div');o.className='ddo';o.textContent=p;o.onclick=function(e){e.stopPropagation();S.preset=i;ddMark();box.classList.remove('open')};l.appendChild(o)});
 bt.onclick=function(e){e.stopPropagation();box.classList.toggle('open')};box.appendChild(bt);box.appendChild(l);
 document.addEventListener('click',function(){box.classList.remove('open')});ddMark()}
ddInit();
seg('segTheme','theme',applyTheme);seg('segStart','start');
sw('swHk','hk',ddMark);sw('swAuto','auto');
$('chk').onclick=function(){send({kind:'check'})};
$('name').oninput=function(){S.name=this.value;check()};
function check(){$('next').disabled=(S.step===1&&!S.name.trim())}
function line(t){var d=document.createElement('div');d.textContent=t;return d}
function enter(n){
 S.step=n;
 document.querySelectorAll('.step').forEach(function(s,i){s.classList.toggle('on',i===n)});
 document.querySelectorAll('.dots b').forEach(function(b,i){b.classList.toggle('on',i===n)});
 $('skip').style.display=n===0?'':'none';
 $('back').style.display=n>0?'':'none';
 $('next').textContent=['Começar','Continuar','Continuar','Abrir o Azul Groove'][n];
 check();
 if(n===0)typeText($('t0'),'Olá! Bem-vindo ao Azul Groove',45,function(){typeText($('u0'),'Vamos deixar tudo com a sua cara — leva menos de um minuto.',22)});
 if(n===1){typeText($('t1'),'Como podemos te chamar?',40,function(){typeText($('u1'),'Usamos o seu nome para te receber toda vez que abrir o app.',20)});setTimeout(function(){$('name').focus()},350)}
 if(n===2)typeText($('t2'),'Configuração básica',40);
 if(n===3){
  var nm=S.name.trim();
  typeText($('t3'),'Tudo pronto, '+nm+'!',45,function(){typeText($('u3'),'Aproveite o Azul Groove Beta.',22)});
  var sm=$('sum');sm.innerHTML='';
  sm.appendChild(line('👤  '+nm+'  ·  '+S.type));
  sm.appendChild(line('🎨  Tema: '+THEME_NAMES[S.theme]));
  sm.appendChild(line('🏠  Ao abrir: '+(S.start==='site'?'Site':'Painel')));
  sm.appendChild(line('⌨️  Atalho: '+(S.hk?PRESETS[S.preset]:'desligado')));
  sm.appendChild(line('🔄  Atualização automática: '+(S.auto?'ligada':'desligada')));
 }
}
$('next').onclick=function(){
 if(S.step<3){enter(S.step+1);return}
 $('next').disabled=true;
 send({kind:'done',name:S.name.trim(),type:S.type,theme:THEMES[S.theme],start:S.start==='site'?1:0,hk:S.hk,preset:S.preset,auto:S.auto});
};
$('back').onclick=function(){if(S.step>0)enter(S.step-1)};
$('skip').onclick=function(){send({kind:'skip'})};
document.addEventListener('keydown',function(e){if(e.key==='Enter'&&!$('next').disabled)$('next').click()});
mq.addEventListener('change',applyTheme);
$('name').value=S.name;$('swHk').classList.toggle('on',S.hk);$('swAuto').classList.toggle('on',S.auto);ddMark();
applyTheme();enter(0);
</script></body></html>
""";
}
