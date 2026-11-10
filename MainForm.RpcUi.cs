using System.Text.Json;

namespace AzulGroove;

/// <summary>
/// Menu do Rich Presence dentro do painel (substitui a janela antiga do Windows Forms).
/// Tudo é OPCIONAL: o app já funciona só ligando a chave. O que é avançado (ID próprio, bot de música,
/// imagens) fica recolhido em "Avançado". A janela é montada só quando a pessoa clica em "Configurar".
/// Mensagens do painel:  'rpcui:config' | 'rpcui:get' | 'rpcui:save:{json}' | 'rpcui:test:{json}'
/// </summary>
public partial class MainForm
{
    bool rpcOpenPending; // pedido vindo da bandeja: abre o menu assim que o painel carregar

    // "Configurar…" da bandeja e do painel
    void ShowRpcSettings()
    {
        ShowWindow();
        if (!panelActive || web.CoreWebView2 == null)
        {
            rpcOpenPending = true;
            ShowPanel();
            return;
        }
        _ = RpcUiOpenAsync();
    }

    void RpcUiOnNavigationCompleted()
    {
        if (!rpcOpenPending) return;
        rpcOpenPending = false;
        _ = RpcUiOpenAsync();
    }

    void RpcUiDispatch(string t)
    {
        switch (t)
        {
            case "rpcui:on": if (!miRpcOn.Checked) miRpcOn.Checked = true; break;
            case "rpcui:off": if (miRpcOn.Checked) miRpcOn.Checked = false; break;
            case "rpcui:config": ShowRpcSettings(); return;
            case "rpcui:get": _ = RpcUiPushAsync(); return;
            default:
                if (t.StartsWith("rpcui:save:", StringComparison.Ordinal)) RpcUiSave(t["rpcui:save:".Length..]);
                else if (t.StartsWith("rpcui:test:", StringComparison.Ordinal)) _ = RpcUiTestAsync(t["rpcui:test:".Length..]);
                return;
        }
        // redesenha o bloco do painel com o estado real
        if (panelActive && !IsDisposed) _ = BetaInjectPanelAsync();
    }

    object RpcUiState() => new
    {
        on = rpcCfg.On, status = rpcStatus, now = rpcNow,
        connected = rpcStatus == Tr("rpc.botConnected") ||
            rpcStatus.StartsWith(Tr("rpc.connected").Split('{')[0], StringComparison.Ordinal),
        searching = rpcStatus == Tr("rpc.search") || rpcStatus == Tr("rpc.connecting"),
        custom = rpcCfg.OverrideAppId, appId = rpcCfg.OverrideAppId ? rpcCfg.AppId : "",
        ws = rpcCfg.WsUrl, token = rpcCfg.Token,
        site = rpcCfg.AcceptSite, listen = rpcCfg.ShowListenButton, along = rpcCfg.ShowListenAlong,
        plBtn = rpcCfg.ShowPlaylistButton, plRef = rpcCfg.PlaylistRefs, thumb = rpcCfg.ShowThumbnail,
        queue = rpcCfg.ShowQueue, block = rpcCfg.BlockOtherUsers, invite = rpcCfg.BotInvite,
        loop = rpcCfg.LoopImg == RpcSettings.DefLoop ? "" : rpcCfg.LoopImg,
        loopq = rpcCfg.LoopQueueImg == RpcSettings.DefLoop ? "" : rpcCfg.LoopQueueImg,
        pause = rpcCfg.PauseImg == RpcSettings.DefPause ? "" : rpcCfg.PauseImg,
        stream = rpcCfg.StreamImg == RpcSettings.DefStream ? "" : rpcCfg.StreamImg,
        idle = rpcCfg.IdleImg == RpcSettings.DefIdle ? "" : rpcCfg.IdleImg,
    };

    async Task RpcUiOpenAsync()
    {
        try
        {
            if (web.CoreWebView2 == null || IsDisposed || !panelActive) return;
            var js = RpcUiScript.Replace("@STATE@", JsonSerializer.Serialize(RpcUiState()))
                .Replace("@T@", TextsJson("rpc.", "b.rpc.", "p.", "s3."));
            await web.CoreWebView2.ExecuteScriptAsync(js);
        }
        catch { }
    }

    // Atualiza só o estado ao vivo (status, "tocando agora") e a mensagem; não mexe no que a pessoa está editando
    async Task RpcUiPushAsync(string note = "", string result = "")
    {
        try
        {
            if (web.CoreWebView2 == null || IsDisposed || !panelActive) return;
            var json = JsonSerializer.Serialize(new
            {
                status = rpcStatus,
                now = rpcNow,
                on = rpcCfg.On,
                connected = rpcStatus == Tr("rpc.botConnected") ||
                    rpcStatus.StartsWith(Tr("rpc.connected").Split('{')[0], StringComparison.Ordinal),
                searching = rpcStatus == Tr("rpc.search") || rpcStatus == Tr("rpc.connecting"),
                note,
                result
            });
            await web.CoreWebView2.ExecuteScriptAsync($"window.__azRpc&&window.__azRpc.state({json})");
        }
        catch { }
    }

    async Task RpcUiTestAsync(string json)
    {
        bool ok; string msg;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var custom = r.TryGetProperty("custom", out var c) && c.ValueKind == JsonValueKind.True;
            var id = custom ? JStr(r, "appId").Trim().Replace(" ", "") : RpcSettings.DummyAppId;
            if (custom && !RpcSettings.ValidAppId(id)) { ok = false; msg = Tr("rpc.invalidId"); }
            else (ok, msg) = await RpcTestAsync(id);
        }
        catch { ok = false; msg = Tr("rpc.testError"); }

        try
        {
            if (web.CoreWebView2 == null || IsDisposed || !panelActive) return;
            var res = JsonSerializer.Serialize(new { ok, msg });
            await web.CoreWebView2.ExecuteScriptAsync($"window.__azRpc&&window.__azRpc.test({res})");
        }
        catch { }
    }

    void RpcUiSave(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            bool B(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;

            var custom = B("custom");
            var app = JStr(r, "appId").Trim().Replace(" ", "");
            var ws = JStr(r, "ws").Trim();
            var token = JStr(r, "token").Replace(" ", "").Replace("\n", "").Replace("\r", "");
            string[] imgs = { JStr(r, "loop").Trim(), JStr(r, "loopq").Trim(), JStr(r, "pause").Trim(), JStr(r, "stream").Trim(), JStr(r, "idle").Trim() };

            if (custom && !RpcSettings.ValidAppId(app)) { _ = RpcUiPushAsync(Tr("rpc.invalidId"), "err"); return; }
            if (ws.Length > 0 && !(Uri.TryCreate(ws, UriKind.Absolute, out var wu) && (wu.Scheme == "ws" || wu.Scheme == "wss")))
            { _ = RpcUiPushAsync(Tr("rpc.invalidWs"), "err"); return; }
            foreach (var s in imgs)
                if (s.Length > 0 && !(Uri.TryCreate(s, UriKind.Absolute, out var iu) && (iu.Scheme == Uri.UriSchemeHttp || iu.Scheme == Uri.UriSchemeHttps)))
                { _ = RpcUiPushAsync(Tr("rpc.invalidImages"), "err"); return; }

            rpcCfg.OverrideAppId = custom;
            rpcCfg.AppId = RpcSettings.ValidAppId(app) ? app : RpcSettings.DummyAppId;
            rpcCfg.WsUrl = ws;
            rpcCfg.Token = token;
            rpcCfg.AcceptSite = B("site");
            rpcCfg.ShowListenButton = B("listen");
            rpcCfg.ShowListenAlong = B("along");
            rpcCfg.ShowPlaylistButton = B("plBtn");
            rpcCfg.PlaylistRefs = B("plRef");
            rpcCfg.ShowThumbnail = B("thumb");
            rpcCfg.ShowQueue = B("queue");
            rpcCfg.BlockOtherUsers = B("block");
            rpcCfg.BotInvite = B("invite");
            rpcCfg.LoopImg = RpcSettings.Or(imgs[0], RpcSettings.DefLoop);
            rpcCfg.LoopQueueImg = RpcSettings.Or(imgs[1], RpcSettings.DefLoop);
            rpcCfg.PauseImg = RpcSettings.Or(imgs[2], RpcSettings.DefPause);
            rpcCfg.StreamImg = RpcSettings.Or(imgs[3], RpcSettings.DefStream);
            rpcCfg.IdleImg = RpcSettings.Or(imgs[4], RpcSettings.DefIdle);
            rpcCfg.Save();

            // liga/desliga pelo próprio item da bandeja (ele inicia/para a conexão); se já estava ligado, reinicia para valer as mudanças
            if (B("on")) { if (!miRpcOn.Checked) miRpcOn.Checked = true; else RpcStart(); }
            else if (miRpcOn.Checked) miRpcOn.Checked = false;

            _ = RpcUiPushAsync(Tr("rpc.saved"), "ok");
            if (panelActive && !IsDisposed) _ = BetaInjectPanelAsync();
        }
        catch { _ = RpcUiPushAsync(Tr("rpc.saveError"), "err"); }
    }

    const string RpcUiScript = """
(function(S){try{
 if(location.protocol==='https:')return;
 var T=@T@;function t(k){return T[k]||k}
 var old=document.getElementById('az-rpcm');if(old)old.remove();
 function h(t,c,x){var e=document.createElement(t);if(c)e.className=c;if(x!=null)e.textContent=x;return e}
 function post(m){try{window.chrome.webview.postMessage(m)}catch(e){}}
 function tg(on){var lb=h('label','sw'),i=h('input');i.type='checkbox';i.checked=!!on;lb.appendChild(i);lb.appendChild(h('span'));lb.get=function(){return i.checked};lb.set=function(v){i.checked=!!v};return lb}
 function row(p,t,sub,ctl){var r=h('div','r'),l=h('div');l.appendChild(document.createTextNode(t));if(sub)l.appendChild(h('small','',sub));r.appendChild(l);r.appendChild(ctl);p.appendChild(r)}
 function fld(p,label,ph,val,pw){var d=h('div','fld');d.appendChild(h('label','',label));var i=h('input','in');i.type=pw?'password':'text';i.spellcheck=false;i.placeholder=ph||'';i.value=val||'';d.appendChild(i);p.appendChild(d);return i}
 var poll=0;
 function closeM(){clearInterval(poll);var o=document.getElementById('az-rpcm');if(o)o.remove();window.__azRpc=null;document.removeEventListener('keydown',esc)}
 function esc(e){if(e.key==='Escape')closeM()}
 var ov=h('div','md');ov.id='az-rpcm';ov.onclick=function(e){if(e.target===ov)closeM()};
 var mc=h('div','mc');ov.appendChild(mc);
 var sty=h('style');sty.textContent='#az-rpcm .mc{width:min(600px,100%)}#az-rpcm .rh{display:flex;justify-content:space-between;align-items:center;gap:10px;margin-bottom:12px}#az-rpcm .rh b{font-size:1.12rem}#az-rpcm .rs{display:flex;align-items:center;gap:12px;padding:12px 14px;border-radius:12px;background:rgba(127,127,127,.1);margin-bottom:12px}#az-rpcm .dot{width:11px;height:11px;border-radius:50%;background:#888;flex:none}#az-rpcm .rs small{display:block;color:var(--mu)}#az-rpcm .sec{font-size:.72rem;font-weight:700;letter-spacing:.08em;text-transform:uppercase;color:var(--mu);margin:18px 2px 6px}#az-rpcm .adv{display:none}#az-rpcm .adv.show{display:block}#az-rpcm .in{min-width:0;width:100%}#az-rpcm .fld{margin:10px 0}#az-rpcm .fld label{display:block;font-size:.82rem;color:var(--mu);margin-bottom:4px}#az-rpcm .ft2{display:flex;justify-content:space-between;align-items:center;gap:10px;margin-top:16px;padding-top:14px;border-top:1px solid var(--line)}#az-rpcm .note{font-size:.88rem;color:var(--mu);flex:1}#az-rpcm .note.ok{color:#3ba55d}#az-rpcm .note.err{color:#ed4245}#az-rpcm .tr{display:flex;gap:10px;align-items:center;margin-top:8px}';
 mc.appendChild(sty);

 var hd=h('div','rh');hd.appendChild(h('b', '',t('b.rpc.cfg')));
 var x=h('button','btn o','\u2715');x.type='button';x.onclick=closeM;hd.appendChild(x);mc.appendChild(hd);

 var rs=h('div','rs'),dot=h('div','dot'),rt=h('div'),st=h('b','',''),nw=h('small','','');
 rt.appendChild(st);rt.appendChild(nw);rs.appendChild(dot);rs.appendChild(rt);mc.appendChild(rs);
 function live(d){var s=d.status||'';
 dot.style.background=d.connected?'#43b581':s===t('rpc.off')?'#888':d.searching?'#f0b232':'#ed4245';
  st.textContent=s;nw.textContent=d.now?'\u266A  '+d.now:t('s3.rpcUiNothing')}
 live(S);

 var c0=h('div','c');mc.appendChild(c0);
 var on=tg(S.on);row(c0,t('b.rpc.on'),t('b.rpc.cfg.s'),on);

 mc.appendChild(h('div','sec',t('s3.rpcUiShow')));
 var c1=h('div','c');mc.appendChild(c1);
 var site=tg(S.site),listen=tg(S.listen),thumb=tg(S.thumb),queue=tg(S.queue),plBtn=tg(S.plBtn);
 row(c1,t('s3.rpcUiMusic'),t('s3.rpcDescMusic'),site);
 row(c1,t('s3.rpcUiListen'),t('s3.rpcDescListen'),listen);
 row(c1,t('s3.rpcUiThumbnail'),t('s3.rpcDescThumbnail'),thumb);
 row(c1,t('s3.rpcUiQueue'),t('s3.rpcDescQueue'),queue);
 row(c1,t('s3.rpcUiPlaylist'),t('s3.rpcDescPlaylist'),plBtn);

 var more=h('button','btn o',t('s3.rpcUiMore')+' \u25BE');more.type='button';more.style.marginTop='10px';
 var m2=h('div','adv');
 var c2=h('div','c');m2.appendChild(c2);
 var along=tg(S.along),plRef=tg(S.plRef),block=tg(S.block),invite=tg(S.invite);
 row(c2,t('s3.rpcUiAlong'),t('s3.rpcDescAlong'),along);
 row(c2,t('s3.rpcUiPlaylistId'),t('s3.rpcDescPlaylistId'),plRef);
 row(c2,t('s3.rpcUiBlock'),t('s3.rpcDescBlock'),block);
 row(c2,t('s3.rpcUiInvite'),t('s3.rpcDescInvite'),invite);
 mc.appendChild(more);mc.appendChild(m2);
 more.onclick=function(){var s=m2.classList.toggle('show');more.textContent=t('s3.rpcUiMore')+(s?' \u25B4':' \u25BE')};

 var adv=h('button','btn o',t('s3.rpcUiAdvanced')+' \u25BE');adv.type='button';adv.style.cssText='margin-top:10px;margin-left:8px';
 var a2=h('div','adv');
 var c3=h('div','c');c3.style.padding='6px 18px 14px';a2.appendChild(c3);
 var cust=tg(S.custom);row(c3,t('s3.rpcUiCustomId'),t('s3.rpcUiCustomHint'),cust);
 var app=fld(c3,t('s3.rpcUiAppId'),t('s3.rpcUiAppHint'),S.appId);
 var testBtn=h('button','btn o',t('s3.rpcUiTest')),testMsg=h('small','','');testBtn.type='button';
 var tr=h('div','tr');tr.appendChild(testBtn);tr.appendChild(testMsg);c3.appendChild(tr);
 function appState(){app.disabled=!cust.get();app.style.opacity=cust.get()?'1':'.55'}
 appState();cust.querySelector('input').onchange=appState;
 testBtn.onclick=function(){testBtn.disabled=true;testMsg.style.color='';testMsg.textContent='Testando\u2026';post('rpcui:test:'+JSON.stringify({custom:cust.get(),appId:app.value}))};
 c3.appendChild(h('div','sec',t('s3.rpcUiBot')));
 var ws=fld(c3,t('s3.rpcUiWs'),'ws://localhost/ws',S.ws);
 var tk=fld(c3,t('s3.rpcUiToken'),'',S.token,true);
 c3.appendChild(h('div','sec',t('s3.rpcUiImages')));
 var iL=fld(c3,t('s3.rpcUiLoop'),'',S.loop),iLQ=fld(c3,t('s3.rpcUiQueueLoop'),'',S.loopq),iP=fld(c3,t('s3.rpcUiPause'),'',S.pause),iS=fld(c3,t('s3.rpcUiStream'),'',S.stream),iI=fld(c3,t('s3.rpcUiIdle'),'',S.idle);
 mc.appendChild(adv);mc.appendChild(a2);
 adv.onclick=function(){var s=a2.classList.toggle('show');adv.textContent=t('s3.rpcUiAdvanced')+(s?' \u25B4':' \u25BE')};

 var ft=h('div','ft2'),note=h('div','note','');
 var cn=h('button','btn o',t('s3.rpcUiCancel')),sv=h('button','btn pri');cn.type=sv.type='button';
 sv.innerHTML='<span>'+t('s3.rpcUiSave')+'</span>';cn.onclick=closeM;
 var bx=h('div');bx.style.cssText='display:flex;gap:8px';bx.appendChild(cn);bx.appendChild(sv);
 ft.appendChild(note);ft.appendChild(bx);mc.appendChild(ft);
 sv.onclick=function(){
  sv.disabled=true;note.className='note';note.textContent=t('s3.rpcUiSaving');
  post('rpcui:save:'+JSON.stringify({on:on.get(),custom:cust.get(),appId:app.value,ws:ws.value,token:tk.value,
   site:site.get(),listen:listen.get(),along:along.get(),plBtn:plBtn.get(),plRef:plRef.get(),thumb:thumb.get(),queue:queue.get(),block:block.get(),invite:invite.get(),
   loop:iL.value,loopq:iLQ.value,pause:iP.value,stream:iS.value,idle:iI.value}))};

 window.__azRpc={
  state:function(d){live(d);if(d.note){note.className='note'+(d.result==='ok'?' ok':d.result==='err'?' err':'');note.textContent=d.note;sv.disabled=false;if(d.result==='ok')setTimeout(closeM,900)}},
  test:function(d){testBtn.disabled=false;testMsg.style.color=d.ok?'#3ba55d':'#ed4245';testMsg.textContent=(d.ok?'\u2714 ':'\u2716 ')+d.msg}
 };
 document.addEventListener('keydown',esc);
 poll=setInterval(function(){post('rpcui:get')},2000);
 document.body.appendChild(ov);
}catch(e){}})(@STATE@);
""";
}
