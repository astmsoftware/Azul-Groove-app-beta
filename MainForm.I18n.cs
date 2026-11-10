using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AzulGroove;

/// <summary>
/// Vários idiomas.
///  • O idioma é o do Windows (CultureInfo.CurrentUICulture) até a pessoa escolher outro (cfg.Lang = "" significa automático).
///  • Os textos ficam em dicionários "chave → frase". Frase que falta cai para o inglês (e, no pt-PT, antes para o pt-BR).
///  • QUALQUER idioma novo entra sem recompilar: crie  %LocalAppData%\AzulGroove\lang\&lt;código&gt;.json  (ex.: fr-CA.json, he.json)
///    com as mesmas chaves + "_name" (nome mostrado na lista) e, se a escrita for da direita para a esquerda, "_rtl": "1".
///    Um arquivo com o código de um idioma que já existe só sobrescreve as chaves que ele traz.
/// Etapa 1: assistente de configuração inicial + lista "Idioma" no painel. (Etapas 2 e 3: painel, bandeja, chat, IA, feedback, RPC.)
/// </summary>
public partial class MainForm
{
    const string DropdownScrollbarCss = """
*{scrollbar-width:thin;scrollbar-color:transparent transparent}
*:hover,*:focus-within{scrollbar-color:var(--scroll-thumb) transparent}
*::-webkit-scrollbar{width:7px;height:7px}
*::-webkit-scrollbar-track,*::-webkit-scrollbar-corner{background:transparent}
*::-webkit-scrollbar-thumb{background:transparent;border:2px solid transparent;background-clip:padding-box;border-radius:999px}
*:hover::-webkit-scrollbar-thumb,*:focus-within::-webkit-scrollbar-thumb{background:var(--scroll-thumb);border:2px solid transparent;background-clip:padding-box}
*::-webkit-scrollbar-thumb:hover{background:var(--scroll-thumb-hover);border:2px solid transparent;background-clip:padding-box}
*::-webkit-scrollbar-button{display:none;width:0;height:0}
.ddl{overscroll-behavior:contain}
.ddo:focus-visible{outline:2px solid var(--scroll-thumb-hover);outline-offset:-2px}
""";

    const string DropdownViewportScript = """
function fitDropdown(box,list,limit){
 var r=box.getBoundingClientRect(),below=window.innerHeight-r.bottom-12,above=r.top-12;
 var openAbove=list.scrollHeight>below&&above>below,room=openAbove?above:below;
 if(room<below&&room<above)openAbove=above>below;
 room=openAbove?above:below;
 list.style.maxHeight=Math.max(48,Math.min(limit,room-12))+'px';
 list.style.top=openAbove?'auto':'calc(100% + 6px)';
 list.style.bottom=openAbove?'calc(100% + 6px)':'auto';
}
function wireDropdownKeyboard(box,button,list){
 if(list.dataset.keyboardReady)return;
 list.dataset.keyboardReady='1';list.setAttribute('role','listbox');list.tabIndex=-1;
 button.setAttribute('aria-haspopup','listbox');button.setAttribute('aria-expanded','false');
 new MutationObserver(function(){button.setAttribute('aria-expanded',box.classList.contains('open')?'true':'false')}).observe(box,{attributes:true,attributeFilter:['class']});
 var options=function(){return Array.from(list.children).filter(function(x){return x.classList.contains('ddo')})};
 var refresh=function(){options().forEach(function(x){x.setAttribute('role','option');x.tabIndex=-1;x.setAttribute('aria-selected',x.classList.contains('sel')?'true':'false')})};
 var focusAt=function(i){var a=options();if(!a.length)return;refresh();i=Math.max(0,Math.min(a.length-1,i));a[i].focus();a[i].scrollIntoView({block:'nearest'})};
 button.addEventListener('click',function(){var open=box.classList.contains('open');button.setAttribute('aria-expanded',open?'true':'false');if(open){refresh();var a=options(),i=a.findIndex(function(x){return x.classList.contains('sel')});requestAnimationFrame(function(){focusAt(i<0?0:i)})}});
 list.addEventListener('click',function(){var open=box.classList.contains('open');button.setAttribute('aria-expanded',open?'true':'false');if(!open)button.focus()});
 button.addEventListener('keydown',function(e){if(e.key!=='ArrowDown'&&e.key!=='ArrowUp')return;e.preventDefault();if(!box.classList.contains('open'))button.click();var a=options(),i=a.findIndex(function(x){return x.classList.contains('sel')});focusAt(i<0?0:i)});
 list.addEventListener('keydown',function(e){
  var a=options();if(!a.length)return;var i=a.indexOf(document.activeElement),next=i;
  if(e.key==='Escape'){e.preventDefault();box.classList.remove('open');button.setAttribute('aria-expanded','false');button.focus();return}
  if(e.key==='ArrowDown')next=i<0?0:Math.min(a.length-1,i+1);
  else if(e.key==='ArrowUp')next=i<0?a.length-1:Math.max(0,i-1);
  else if(e.key==='Home')next=0;
  else if(e.key==='End')next=a.length-1;
  else if(e.key==='PageDown')next=Math.min(a.length-1,(i<0?0:i)+8);
  else if(e.key==='PageUp')next=Math.max(0,(i<0?a.length-1:i)-8);
  else if(e.key==='Enter'||e.key===' '){e.preventDefault();if(i>=0)a[i].click();return}
  else return;
  e.preventDefault();focusAt(next)
 });
 refresh()
}
""";

    // (código, nome na própria língua, escrita da direita para a esquerda?)
    static readonly (string Code, string Name, bool Rtl)[] I18nBuiltin =
    {
        ("pt-BR", "Português (Brasil)", false), ("pt-PT", "Português (Portugal)", false), ("en", "English", false),
        ("es", "Español", false), ("fr", "Français", false), ("de", "Deutsch", false), ("it", "Italiano", false),
        ("nl", "Nederlands", false), ("pl", "Polski", false), ("tr", "Türkçe", false), ("ru", "Русский", false),
        ("uk", "Українська", false), ("ar", "العربية", true), ("ja", "日本語", false), ("ko", "한국어", false),
        ("zh-CN", "简体中文", false), ("zh-TW", "繁體中文", false), ("hi", "हिन्दी", false),
        ("id", "Bahasa Indonesia", false), ("vi", "Tiếng Việt", false),
    };

    static string I18nDir => Path.Combine(DataDir, "lang");

    static readonly Regex I18nCodeRx = new(@"^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})?$", RegexOptions.Compiled);

    static Dictionary<string, Dictionary<string, string>>? i18nAll;
    static readonly Dictionary<string, Dictionary<string, string>> i18nResolved = new(StringComparer.OrdinalIgnoreCase);

    static Dictionary<string, Dictionary<string, string>> I18nAll()
    {
        if (i18nAll != null) return i18nAll;
        var all = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in I18nRaw)
        {
            try { var d = JsonSerializer.Deserialize<Dictionary<string, string>>(kv.Value); if (d != null) all[kv.Key] = d; }
            catch { /* um idioma com defeito não derruba os outros */ }
        }
        // etapa 2 (painel, bandeja, mensagens de atualização): junta as chaves novas ao mesmo idioma
        foreach (var kv in I18nRawPanel)
        {
            try
            {
                var d = JsonSerializer.Deserialize<Dictionary<string, string>>(kv.Value);
                if (d == null) continue;
                if (all.TryGetValue(kv.Key, out var cur)) { foreach (var x in d) cur[x.Key] = x.Value; }
                else all[kv.Key] = d;
            }
            catch { /* idioma com defeito não derruba os outros */ }
        }
        // etapa 3 (chat offline, IA local, feedback e Rich Presence)
        foreach (var kv in I18nRawStage3)
        {
            try
            {
                var d = JsonSerializer.Deserialize<Dictionary<string, string>>(kv.Value);
                if (d == null) continue;
                if (all.TryGetValue(kv.Key, out var cur)) { foreach (var x in d) cur[x.Key] = x.Value; }
                else all[kv.Key] = d;
            }
            catch { /* idioma com defeito não derruba os outros */ }
        }
        foreach (var kv in I18nRawStage3Ui
            .Concat(I18nRawStage3Ai)
            .Concat(I18nRawStage3RpcUi)
            .Concat(I18nRawStage3Feedback)
            .Concat(I18nRawStage3FeedbackTypes)
            .Concat(I18nRawStage3Activity)
            .Concat(I18nRawStage3FeedbackMessages)
            .Concat(I18nRawStage3FeedbackUi)
            .Concat(I18nRawStage3RpcDescriptions)
            .Concat(I18nRawStage3AiErrors)
            .Concat(I18nRawStage3ChatUi)
            .Concat(I18nRawStage3ChatPrompts)
            .Concat(I18nRawStage3SupportUi)
            .Concat(I18nRawStage3Fixes)
            .Concat(I18nRawMusic))
        {
            try
            {
                var d = JsonSerializer.Deserialize<Dictionary<string, string>>(kv.Value);
                if (d == null) continue;
                if (all.TryGetValue(kv.Key, out var cur)) { foreach (var x in d) cur[x.Key] = x.Value; }
                else all[kv.Key] = d;
            }
            catch { /* idioma com defeito não derruba os outros */ }
        }
        // idiomas extras / correções feitas pela própria pessoa
        try
        {
            if (Directory.Exists(I18nDir))
            {
                foreach (var f in Directory.EnumerateFiles(I18nDir, "*.json").Take(80))
                {
                    try
                    {
                        var code = Path.GetFileNameWithoutExtension(f);
                        if (!I18nCodeRx.IsMatch(code) || new FileInfo(f).Length > 300_000) continue;
                        var d = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(f));
                        if (d == null) continue;
                        if (all.TryGetValue(code, out var cur)) { foreach (var kv in d) cur[kv.Key] = kv.Value; }
                        else all[code] = d;
                    }
                    catch { /* arquivo inválido: ignora */ }
                }
            }
        }
        catch { }
        return i18nAll = all;
    }

    /// <summary>Lista para os menus: os 20 de fábrica e depois os extras que a pessoa adicionou.</summary>
    static List<(string Code, string Name, bool Rtl)> I18nList()
    {
        var all = I18nAll();
        var list = new List<(string Code, string Name, bool Rtl)>();
        foreach (var b in I18nBuiltin) if (all.ContainsKey(b.Code)) list.Add(b);
        foreach (var kv in all.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (list.Any(x => x.Code.Equals(kv.Key, StringComparison.OrdinalIgnoreCase))) continue;
            var name = kv.Value.TryGetValue("_name", out var n) && n.Trim().Length > 0 ? n.Trim() : kv.Key;
            list.Add((kv.Key, name, kv.Value.TryGetValue("_rtl", out var r) && r == "1"));
        }
        return list;
    }

    /// <summary>Todas as frases de um idioma, já completadas com o inglês (e pt-BR para o pt-PT).</summary>
    static Dictionary<string, string> I18nResolve(string code)
    {
        lock (i18nResolved)
        {
            if (i18nResolved.TryGetValue(code, out var cached)) return cached;
            var all = I18nAll();
            var res = new Dictionary<string, string>();
            void Add(string c)
            {
                if (!all.TryGetValue(c, out var d)) return;
                foreach (var kv in d) if (!kv.Key.StartsWith('_')) res[kv.Key] = kv.Value;
            }
            Add("en");
            if (code.Equals("pt-PT", StringComparison.OrdinalIgnoreCase)) Add("pt-BR");
            if (!code.Equals("en", StringComparison.OrdinalIgnoreCase)) Add(code);
            return i18nResolved[code] = res;
        }
    }

    /// <summary>Idioma do Windows, escolhido entre os que o app tem (senão, inglês).</summary>
    static string I18nDetect()
    {
        try
        {
            var all = I18nAll();
            var ci = CultureInfo.CurrentUICulture;
            var n = ci.Name;
            if (all.ContainsKey(n)) return all.Keys.First(k => k.Equals(n, StringComparison.OrdinalIgnoreCase));
            var two = ci.TwoLetterISOLanguageName;
            if (two == "zh") return n.Contains("TW") || n.Contains("HK") || n.Contains("MO") || n.Contains("Hant") ? "zh-TW" : "zh-CN";
            if (two == "pt") return "pt-BR";
            if (all.ContainsKey(two)) return two;
        }
        catch { }
        return "en";
    }

    /// <summary>Código vindo de fora (painel/assistente) → o que vai para o arquivo de configuração.
    /// "" = automático (vazio, "auto", desconhecido ou igual ao idioma do Windows).</summary>
    static string I18nNormalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Equals("auto", StringComparison.OrdinalIgnoreCase)) return "";
        var all = I18nAll();
        var hit = all.Keys.FirstOrDefault(k => k.Equals(code.Trim(), StringComparison.OrdinalIgnoreCase));
        if (hit == null) return "";
        return hit.Equals(I18nDetect(), StringComparison.OrdinalIgnoreCase) ? "" : hit;
    }

    /// <summary>Idioma em uso agora.</summary>
    string LangCode
    {
        get
        {
            var l = cfg.Lang ?? "";
            return l.Length > 0 && I18nAll().ContainsKey(l) ? l : I18nDetect();
        }
    }

    /// <summary>Texto traduzido (para as próximas etapas: painel, bandeja, avisos...).</summary>
    string Tr(string key) => I18nResolve(LangCode).TryGetValue(key, out var v) ? v : key;

    // JSON para o HTML do assistente: { código: {chave: frase} } e a lista { code, name, rtl }
    static string I18nLangsJson() =>
        JsonSerializer.Serialize(I18nList().ToDictionary(x => x.Code, x => I18nResolve(x.Code)));

    static string I18nListJson() =>
        JsonSerializer.Serialize(I18nList().Select(x => new { code = x.Code, name = x.Name, rtl = x.Rtl }).ToArray());

    // ================= Textos de fábrica (assistente de configuração inicial) =================
    static readonly Dictionary<string, string> I18nRaw = new()
    {
        ["pt-BR"] = """{"hello":"Olá! Bem-vindo ao Azul Groove","hello.sub":"Vamos deixar tudo com a sua cara — leva menos de um minuto.","name.t":"Como podemos te chamar?","name.sub":"Usamos o seu nome para te receber toda vez que abrir o app.","name.ph":"Seu nome","use":"Você usa o Azul Groove como…","ty.listener":"Ouvinte","ty.dj":"DJ / Produtor","ty.streamer":"Streamer","ty.owner":"Dono de servidor","ty.other":"Outro","basic":"Configuração básica","theme":"Tema","th.auto":"Auto","th.light":"Claro","th.dark":"Escuro","start":"Ao abrir o app, mostrar","st.panel":"Painel","st.site":"Site","hk":"Atalho para abrir o chat","hk.sub":"Funciona mesmo com o app minimizado","upd":"Atualização automática","upd.sub":"Avisa quando sair uma versão nova","chk":"Verificar agora","done.t":"Tudo pronto, {name}!","done.sub":"Aproveite o Azul Groove Beta.","v.on":"ligado","v.off":"desligado","n.skip":"Pular","n.back":"Voltar","n.begin":"Começar","n.cont":"Continuar","n.open":"Abrir o Azul Groove"}""",
        ["pt-PT"] = """{"hello.sub":"Vamos personalizar tudo — demora menos de um minuto.","name.t":"Como te devemos chamar?","name.sub":"Usamos o teu nome para te receber sempre que abres a aplicação.","use":"Usas o Azul Groove como…","start":"Ao abrir a aplicação, mostrar","hk.sub":"Funciona mesmo com a aplicação minimizada","done.sub":"Aproveita o Azul Groove Beta."}""",
        ["en"] = """{"hello":"Hello! Welcome to Azul Groove","hello.sub":"Let's make everything yours — it takes less than a minute.","name.t":"What should we call you?","name.sub":"We use your name to greet you every time you open the app.","name.ph":"Your name","use":"You use Azul Groove as…","ty.listener":"Listener","ty.dj":"DJ / Producer","ty.streamer":"Streamer","ty.owner":"Server owner","ty.other":"Other","basic":"Basic setup","theme":"Theme","th.auto":"Auto","th.light":"Light","th.dark":"Dark","start":"When the app opens, show","st.panel":"Panel","st.site":"Website","hk":"Shortcut to open the chat","hk.sub":"Works even when the app is minimized","upd":"Automatic updates","upd.sub":"Notifies you when a new version is out","chk":"Check now","done.t":"All set, {name}!","done.sub":"Enjoy Azul Groove Beta.","v.on":"on","v.off":"off","n.skip":"Skip","n.back":"Back","n.begin":"Get started","n.cont":"Continue","n.open":"Open Azul Groove"}""",
        ["es"] = """{"hello":"¡Hola! Te damos la bienvenida a Azul Groove","hello.sub":"Vamos a dejarlo todo a tu gusto: lleva menos de un minuto.","name.t":"¿Cómo te llamamos?","name.sub":"Usamos tu nombre para recibirte cada vez que abras la app.","name.ph":"Tu nombre","use":"Usas Azul Groove como…","ty.listener":"Oyente","ty.dj":"DJ / Productor","ty.streamer":"Streamer","ty.owner":"Dueño de servidor","ty.other":"Otro","basic":"Configuración básica","theme":"Tema","th.auto":"Auto","th.light":"Claro","th.dark":"Oscuro","start":"Al abrir la app, mostrar","st.panel":"Panel","st.site":"Sitio web","hk":"Atajo para abrir el chat","hk.sub":"Funciona incluso con la app minimizada","upd":"Actualización automática","upd.sub":"Te avisa cuando salga una versión nueva","chk":"Buscar ahora","done.t":"¡Todo listo, {name}!","done.sub":"Disfruta de Azul Groove Beta.","v.on":"activado","v.off":"desactivado","n.skip":"Omitir","n.back":"Atrás","n.begin":"Empezar","n.cont":"Continuar","n.open":"Abrir Azul Groove"}""",
        ["fr"] = """{"hello":"Bonjour ! Bienvenue sur Azul Groove","hello.sub":"Personnalisons tout ça — ça prend moins d'une minute.","name.t":"Comment peut-on vous appeler ?","name.sub":"Nous utilisons votre nom pour vous accueillir à chaque ouverture de l'app.","name.ph":"Votre nom","use":"Vous utilisez Azul Groove en tant que…","ty.listener":"Auditeur","ty.dj":"DJ / Producteur","ty.streamer":"Streamer","ty.owner":"Propriétaire de serveur","ty.other":"Autre","basic":"Configuration de base","theme":"Thème","th.auto":"Auto","th.light":"Clair","th.dark":"Sombre","start":"À l'ouverture de l'app, afficher","st.panel":"Panneau","st.site":"Site web","hk":"Raccourci pour ouvrir le chat","hk.sub":"Fonctionne même avec l'app réduite","upd":"Mise à jour automatique","upd.sub":"Vous prévient quand une nouvelle version sort","chk":"Vérifier maintenant","done.t":"Tout est prêt, {name} !","done.sub":"Profitez d'Azul Groove Beta.","v.on":"activé","v.off":"désactivé","n.skip":"Passer","n.back":"Retour","n.begin":"Commencer","n.cont":"Continuer","n.open":"Ouvrir Azul Groove"}""",
        ["de"] = """{"hello":"Hallo! Willkommen bei Azul Groove","hello.sub":"Machen wir alles passend für dich – das dauert weniger als eine Minute.","name.t":"Wie sollen wir dich nennen?","name.sub":"Wir nutzen deinen Namen, um dich bei jedem Start der App zu begrüßen.","name.ph":"Dein Name","use":"Du nutzt Azul Groove als…","ty.listener":"Hörer","ty.dj":"DJ / Produzent","ty.streamer":"Streamer","ty.owner":"Server-Besitzer","ty.other":"Andere","basic":"Grundeinstellungen","theme":"Design","th.auto":"Auto","th.light":"Hell","th.dark":"Dunkel","start":"Beim Start der App anzeigen","st.panel":"Übersicht","st.site":"Webseite","hk":"Tastenkürzel zum Öffnen des Chats","hk.sub":"Funktioniert auch bei minimierter App","upd":"Automatische Updates","upd.sub":"Benachrichtigt dich bei neuen Versionen","chk":"Jetzt prüfen","done.t":"Alles bereit, {name}!","done.sub":"Viel Spaß mit Azul Groove Beta.","v.on":"an","v.off":"aus","n.skip":"Überspringen","n.back":"Zurück","n.begin":"Los geht's","n.cont":"Weiter","n.open":"Azul Groove öffnen"}""",
        ["it"] = """{"hello":"Ciao! Benvenuto in Azul Groove","hello.sub":"Rendiamo tutto a tua misura: ci vuole meno di un minuto.","name.t":"Come possiamo chiamarti?","name.sub":"Usiamo il tuo nome per darti il benvenuto ogni volta che apri l'app.","name.ph":"Il tuo nome","use":"Usi Azul Groove come…","ty.listener":"Ascoltatore","ty.dj":"DJ / Produttore","ty.streamer":"Streamer","ty.owner":"Proprietario di server","ty.other":"Altro","basic":"Configurazione di base","theme":"Tema","th.auto":"Auto","th.light":"Chiaro","th.dark":"Scuro","start":"All'apertura dell'app, mostra","st.panel":"Pannello","st.site":"Sito web","hk":"Scorciatoia per aprire la chat","hk.sub":"Funziona anche con l'app ridotta a icona","upd":"Aggiornamento automatico","upd.sub":"Ti avvisa quando esce una nuova versione","chk":"Controlla ora","done.t":"Tutto pronto, {name}!","done.sub":"Goditi Azul Groove Beta.","v.on":"attivo","v.off":"disattivato","n.skip":"Salta","n.back":"Indietro","n.begin":"Inizia","n.cont":"Continua","n.open":"Apri Azul Groove"}""",
        ["nl"] = """{"hello":"Hallo! Welkom bij Azul Groove","hello.sub":"We maken alles helemaal van jou — het duurt minder dan een minuut.","name.t":"Hoe mogen we je noemen?","name.sub":"We gebruiken je naam om je te begroeten telkens als je de app opent.","name.ph":"Je naam","use":"Je gebruikt Azul Groove als…","ty.listener":"Luisteraar","ty.dj":"DJ / Producer","ty.streamer":"Streamer","ty.owner":"Servereigenaar","ty.other":"Anders","basic":"Basisinstellingen","theme":"Thema","th.auto":"Auto","th.light":"Licht","th.dark":"Donker","start":"Bij het openen van de app tonen","st.panel":"Paneel","st.site":"Website","hk":"Sneltoets om de chat te openen","hk.sub":"Werkt ook als de app geminimaliseerd is","upd":"Automatische updates","upd.sub":"Meldt het als er een nieuwe versie is","chk":"Nu controleren","done.t":"Alles klaar, {name}!","done.sub":"Veel plezier met Azul Groove Beta.","v.on":"aan","v.off":"uit","n.skip":"Overslaan","n.back":"Terug","n.begin":"Beginnen","n.cont":"Doorgaan","n.open":"Azul Groove openen"}""",
        ["pl"] = """{"hello":"Cześć! Witamy w Azul Groove","hello.sub":"Dopasujmy wszystko do Ciebie — zajmie to mniej niż minutę.","name.t":"Jak możemy się do Ciebie zwracać?","name.sub":"Używamy Twojego imienia, by witać Cię przy każdym uruchomieniu aplikacji.","name.ph":"Twoje imię","use":"Używasz Azul Groove jako…","ty.listener":"Słuchacz","ty.dj":"DJ / Producent","ty.streamer":"Streamer","ty.owner":"Właściciel serwera","ty.other":"Inne","basic":"Podstawowa konfiguracja","theme":"Motyw","th.auto":"Auto","th.light":"Jasny","th.dark":"Ciemny","start":"Po uruchomieniu aplikacji pokaż","st.panel":"Panel","st.site":"Stronę","hk":"Skrót otwierający czat","hk.sub":"Działa także przy zminimalizowanej aplikacji","upd":"Automatyczne aktualizacje","upd.sub":"Powiadomi Cię o nowej wersji","chk":"Sprawdź teraz","done.t":"Wszystko gotowe, {name}!","done.sub":"Miłego korzystania z Azul Groove Beta.","v.on":"włączone","v.off":"wyłączone","n.skip":"Pomiń","n.back":"Wstecz","n.begin":"Zaczynamy","n.cont":"Dalej","n.open":"Otwórz Azul Groove"}""",
        ["tr"] = """{"hello":"Merhaba! Azul Groove'a hoş geldin","hello.sub":"Her şeyi sana göre ayarlayalım — bir dakikadan kısa sürer.","name.t":"Sana nasıl hitap edelim?","name.sub":"Uygulamayı her açtığında seni karşılamak için adını kullanırız.","name.ph":"Adın","use":"Azul Groove'u şu şekilde kullanıyorsun…","ty.listener":"Dinleyici","ty.dj":"DJ / Yapımcı","ty.streamer":"Yayıncı","ty.owner":"Sunucu sahibi","ty.other":"Diğer","basic":"Temel ayarlar","theme":"Tema","th.auto":"Otomatik","th.light":"Açık","th.dark":"Koyu","start":"Uygulama açılınca göster","st.panel":"Panel","st.site":"Web sitesi","hk":"Sohbeti açma kısayolu","hk.sub":"Uygulama simge durumundayken de çalışır","upd":"Otomatik güncelleme","upd.sub":"Yeni sürüm çıkınca haber verir","chk":"Şimdi denetle","done.t":"Hepsi hazır, {name}!","done.sub":"Azul Groove Beta'nın keyfini çıkar.","v.on":"açık","v.off":"kapalı","n.skip":"Atla","n.back":"Geri","n.begin":"Başla","n.cont":"Devam","n.open":"Azul Groove'u aç"}""",
        ["ru"] = """{"hello":"Привет! Добро пожаловать в Azul Groove","hello.sub":"Настроим всё под вас — это займёт меньше минуты.","name.t":"Как к вам обращаться?","name.sub":"Мы используем ваше имя, чтобы приветствовать вас при каждом запуске.","name.ph":"Ваше имя","use":"Вы используете Azul Groove как…","ty.listener":"Слушатель","ty.dj":"Диджей / Продюсер","ty.streamer":"Стример","ty.owner":"Владелец сервера","ty.other":"Другое","basic":"Базовая настройка","theme":"Тема","th.auto":"Авто","th.light":"Светлая","th.dark":"Тёмная","start":"При запуске показывать","st.panel":"Панель","st.site":"Сайт","hk":"Сочетание клавиш для открытия чата","hk.sub":"Работает, даже когда приложение свёрнуто","upd":"Автообновление","upd.sub":"Сообщит, когда выйдет новая версия","chk":"Проверить сейчас","done.t":"Всё готово, {name}!","done.sub":"Приятного пользования Azul Groove Beta.","v.on":"вкл.","v.off":"выкл.","n.skip":"Пропустить","n.back":"Назад","n.begin":"Начать","n.cont":"Продолжить","n.open":"Открыть Azul Groove"}""",
        ["uk"] = """{"hello":"Привіт! Ласкаво просимо до Azul Groove","hello.sub":"Налаштуймо все під вас — це займе менше хвилини.","name.t":"Як до вас звертатися?","name.sub":"Ми використовуємо ваше ім'я, щоб вітати вас під час кожного запуску.","name.ph":"Ваше ім'я","use":"Ви користуєтеся Azul Groove як…","ty.listener":"Слухач","ty.dj":"Діджей / Продюсер","ty.streamer":"Стример","ty.owner":"Власник сервера","ty.other":"Інше","basic":"Базове налаштування","theme":"Тема","th.auto":"Авто","th.light":"Світла","th.dark":"Темна","start":"Під час запуску показувати","st.panel":"Панель","st.site":"Сайт","hk":"Комбінація клавіш для відкриття чату","hk.sub":"Працює, навіть коли застосунок згорнуто","upd":"Автооновлення","upd.sub":"Повідомить, коли вийде нова версія","chk":"Перевірити зараз","done.t":"Усе готово, {name}!","done.sub":"Приємного користування Azul Groove Beta.","v.on":"увімк.","v.off":"вимк.","n.skip":"Пропустити","n.back":"Назад","n.begin":"Почати","n.cont":"Продовжити","n.open":"Відкрити Azul Groove"}""",
        ["ar"] = """{"hello":"مرحبًا! أهلًا بك في Azul Groove","hello.sub":"لنضبط كل شيء على ذوقك — يستغرق أقل من دقيقة.","name.t":"بماذا نناديك؟","name.sub":"نستخدم اسمك لنرحّب بك في كل مرة تفتح فيها التطبيق.","name.ph":"اسمك","use":"تستخدم Azul Groove بصفتك…","ty.listener":"مستمع","ty.dj":"دي جي / منتج","ty.streamer":"ستريمر","ty.owner":"مالك خادم","ty.other":"أخرى","basic":"الإعداد الأساسي","theme":"السمة","th.auto":"تلقائي","th.light":"فاتح","th.dark":"داكن","start":"عند فتح التطبيق، اعرض","st.panel":"اللوحة","st.site":"الموقع","hk":"اختصار فتح الدردشة","hk.sub":"يعمل حتى عند تصغير التطبيق","upd":"التحديث التلقائي","upd.sub":"ينبّهك عند صدور إصدار جديد","chk":"تحقق الآن","done.t":"كل شيء جاهز يا {name}!","done.sub":"استمتع بـ Azul Groove Beta.","v.on":"مفعّل","v.off":"معطّل","n.skip":"تخطي","n.back":"رجوع","n.begin":"ابدأ","n.cont":"متابعة","n.open":"فتح Azul Groove"}""",
        ["ja"] = """{"hello":"こんにちは！Azul Groove へようこそ","hello.sub":"あなた好みに整えましょう。1分もかかりません。","name.t":"お名前を教えてください","name.sub":"アプリを開くたびに、お名前であいさつします。","name.ph":"お名前","use":"Azul Groove の使い方は…","ty.listener":"リスナー","ty.dj":"DJ / プロデューサー","ty.streamer":"配信者","ty.owner":"サーバー管理者","ty.other":"その他","basic":"基本設定","theme":"テーマ","th.auto":"自動","th.light":"ライト","th.dark":"ダーク","start":"アプリ起動時に表示","st.panel":"パネル","st.site":"サイト","hk":"チャットを開くショートカット","hk.sub":"アプリを最小化していても使えます","upd":"自動アップデート","upd.sub":"新しいバージョンが出たらお知らせします","chk":"今すぐ確認","done.t":"準備完了です、{name}さん！","done.sub":"Azul Groove Beta をお楽しみください。","v.on":"オン","v.off":"オフ","n.skip":"スキップ","n.back":"戻る","n.begin":"はじめる","n.cont":"続ける","n.open":"Azul Groove を開く"}""",
        ["ko"] = """{"hello":"안녕하세요! Azul Groove에 오신 것을 환영합니다","hello.sub":"나에게 맞게 설정해 볼까요? 1분도 걸리지 않아요.","name.t":"어떻게 불러 드릴까요?","name.sub":"앱을 열 때마다 이름으로 맞이해 드려요.","name.ph":"이름","use":"Azul Groove를 이렇게 사용해요…","ty.listener":"리스너","ty.dj":"DJ / 프로듀서","ty.streamer":"스트리머","ty.owner":"서버 운영자","ty.other":"기타","basic":"기본 설정","theme":"테마","th.auto":"자동","th.light":"라이트","th.dark":"다크","start":"앱을 열 때 표시할 화면","st.panel":"패널","st.site":"웹사이트","hk":"채팅 열기 단축키","hk.sub":"앱을 최소화해도 작동해요","upd":"자동 업데이트","upd.sub":"새 버전이 나오면 알려 드려요","chk":"지금 확인","done.t":"준비 완료, {name}님!","done.sub":"Azul Groove Beta를 즐겨 보세요.","v.on":"켜짐","v.off":"꺼짐","n.skip":"건너뛰기","n.back":"이전","n.begin":"시작하기","n.cont":"계속","n.open":"Azul Groove 열기"}""",
        ["zh-CN"] = """{"hello":"你好！欢迎使用 Azul Groove","hello.sub":"让我们把一切设置成你喜欢的样子，不到一分钟。","name.t":"我们该怎么称呼你？","name.sub":"每次打开应用时，我们会用你的名字来欢迎你。","name.ph":"你的名字","use":"你使用 Azul Groove 的身份是…","ty.listener":"听众","ty.dj":"DJ / 制作人","ty.streamer":"主播","ty.owner":"服务器所有者","ty.other":"其他","basic":"基础设置","theme":"主题","th.auto":"自动","th.light":"浅色","th.dark":"深色","start":"打开应用时显示","st.panel":"面板","st.site":"网站","hk":"打开聊天的快捷键","hk.sub":"应用最小化时也能使用","upd":"自动更新","upd.sub":"有新版本时通知你","chk":"立即检查","done.t":"一切就绪，{name}！","done.sub":"祝你使用 Azul Groove Beta 愉快。","v.on":"开","v.off":"关","n.skip":"跳过","n.back":"返回","n.begin":"开始","n.cont":"继续","n.open":"打开 Azul Groove"}""",
        ["zh-TW"] = """{"hello":"你好！歡迎使用 Azul Groove","hello.sub":"讓我們把一切設定成你喜歡的樣子，不到一分鐘。","name.t":"我們該怎麼稱呼你？","name.sub":"每次開啟應用程式時，我們會用你的名字來歡迎你。","name.ph":"你的名字","use":"你使用 Azul Groove 的身分是…","ty.listener":"聽眾","ty.dj":"DJ / 製作人","ty.streamer":"直播主","ty.owner":"伺服器擁有者","ty.other":"其他","basic":"基本設定","theme":"主題","th.auto":"自動","th.light":"淺色","th.dark":"深色","start":"開啟應用程式時顯示","st.panel":"面板","st.site":"網站","hk":"開啟聊天的快速鍵","hk.sub":"應用程式最小化時也能使用","upd":"自動更新","upd.sub":"有新版本時通知你","chk":"立即檢查","done.t":"一切就緒，{name}！","done.sub":"祝你使用 Azul Groove Beta 愉快。","v.on":"開","v.off":"關","n.skip":"略過","n.back":"返回","n.begin":"開始","n.cont":"繼續","n.open":"開啟 Azul Groove"}""",
        ["hi"] = """{"hello":"नमस्ते! Azul Groove में आपका स्वागत है","hello.sub":"आइए सब कुछ आपके हिसाब से सेट करें — एक मिनट से भी कम लगेगा।","name.t":"हम आपको क्या कहकर बुलाएँ?","name.sub":"ऐप खोलने पर हर बार आपका स्वागत करने के लिए हम आपके नाम का उपयोग करते हैं।","name.ph":"आपका नाम","use":"आप Azul Groove का उपयोग इस रूप में करते हैं…","ty.listener":"श्रोता","ty.dj":"DJ / प्रोड्यूसर","ty.streamer":"स्ट्रीमर","ty.owner":"सर्वर मालिक","ty.other":"अन्य","basic":"बुनियादी सेटअप","theme":"थीम","th.auto":"ऑटो","th.light":"लाइट","th.dark":"डार्क","start":"ऐप खुलने पर दिखाएँ","st.panel":"पैनल","st.site":"वेबसाइट","hk":"चैट खोलने का शॉर्टकट","hk.sub":"ऐप मिनिमाइज़ होने पर भी काम करता है","upd":"ऑटोमैटिक अपडेट","upd.sub":"नया संस्करण आने पर सूचित करता है","chk":"अभी जाँचें","done.t":"सब तैयार है, {name}!","done.sub":"Azul Groove Beta का आनंद लें।","v.on":"चालू","v.off":"बंद","n.skip":"छोड़ें","n.back":"पीछे","n.begin":"शुरू करें","n.cont":"आगे बढ़ें","n.open":"Azul Groove खोलें"}""",
        ["id"] = """{"hello":"Halo! Selamat datang di Azul Groove","hello.sub":"Mari atur semuanya sesuai seleramu — tidak sampai satu menit.","name.t":"Kami harus memanggilmu apa?","name.sub":"Kami memakai namamu untuk menyambutmu setiap kali membuka aplikasi.","name.ph":"Namamu","use":"Kamu memakai Azul Groove sebagai…","ty.listener":"Pendengar","ty.dj":"DJ / Produser","ty.streamer":"Streamer","ty.owner":"Pemilik server","ty.other":"Lainnya","basic":"Pengaturan dasar","theme":"Tema","th.auto":"Otomatis","th.light":"Terang","th.dark":"Gelap","start":"Saat aplikasi dibuka, tampilkan","st.panel":"Panel","st.site":"Situs web","hk":"Pintasan untuk membuka obrolan","hk.sub":"Tetap berfungsi saat aplikasi diminimalkan","upd":"Pembaruan otomatis","upd.sub":"Memberi tahu saat ada versi baru","chk":"Periksa sekarang","done.t":"Semua siap, {name}!","done.sub":"Selamat menikmati Azul Groove Beta.","v.on":"aktif","v.off":"nonaktif","n.skip":"Lewati","n.back":"Kembali","n.begin":"Mulai","n.cont":"Lanjut","n.open":"Buka Azul Groove"}""",
        ["vi"] = """{"hello":"Xin chào! Chào mừng đến với Azul Groove","hello.sub":"Hãy thiết lập mọi thứ theo ý bạn — chưa đến một phút.","name.t":"Chúng tôi nên gọi bạn là gì?","name.sub":"Chúng tôi dùng tên bạn để chào mỗi khi bạn mở ứng dụng.","name.ph":"Tên của bạn","use":"Bạn dùng Azul Groove với vai trò…","ty.listener":"Người nghe","ty.dj":"DJ / Nhà sản xuất","ty.streamer":"Streamer","ty.owner":"Chủ máy chủ","ty.other":"Khác","basic":"Thiết lập cơ bản","theme":"Giao diện","th.auto":"Tự động","th.light":"Sáng","th.dark":"Tối","start":"Khi mở ứng dụng, hiển thị","st.panel":"Bảng điều khiển","st.site":"Trang web","hk":"Phím tắt mở trò chuyện","hk.sub":"Hoạt động cả khi ứng dụng được thu nhỏ","upd":"Cập nhật tự động","upd.sub":"Báo cho bạn khi có phiên bản mới","chk":"Kiểm tra ngay","done.t":"Mọi thứ đã sẵn sàng, {name}!","done.sub":"Chúc bạn vui với Azul Groove Beta.","v.on":"bật","v.off":"tắt","n.skip":"Bỏ qua","n.back":"Quay lại","n.begin":"Bắt đầu","n.cont":"Tiếp tục","n.open":"Mở Azul Groove"}""",
    };
}
