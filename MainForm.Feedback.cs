using System.Drawing.Imaging;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace AzulGroove;

/// <summary>
/// Feedback → webhook do Discord. Aparece em DOIS lugares: no painel e na aba "Feedback" das configurações do chat offline.
///  • tipo, mensagem, contato opcional, anexos (arquivos, captura da JANELA do app sem esconder, tela inteira) e diagnóstico opcional;
///  • relatório automático de erro: se o app der uma exceção ou fechar sozinho/inesperadamente, na próxima abertura
///    (ou na hora, se o app continuar vivo) o erro + diagnóstico são enviados sozinhos (opção no menu, DESLIGADA por padrão).
/// Mensagens da página:  'fb:get' | 'fb:attach' | 'fb:shot' | 'fb:shotfull' | 'fb:rm:N' | 'fb:preview'
///                       | 'fb:open:form' | 'fb:open:opts' | 'fb:set:diag:0/1' | 'fb:set:auto:0/1' | 'fb:send:{json}'
/// </summary>
public partial class MainForm
{
    // >>> URL do webhook do Discord (se for trocar, cole a nova aqui) <<<
    const string FeedbackWebhook = "https://discord.com/api/webhooks/1561516932090306560/bDnoYJa-hbtXLTjMqiU5gGtZEXKwGDwGt-2oDrvdlSNFRZ_c70MVPlIL5bzjLvKyguwJ";

    // true só se o webhook for de um canal de FÓRUM (cada feedback vira um post). Em canal comum, deixe false.
    static readonly bool FeedbackIsForum = false;

    const int FbMinChars = 10, FbMaxChars = 2000, FbMaxFiles = 5;
    const long FbMaxTotalBytes = 7L * 1024 * 1024 + 512 * 1024;
    static readonly TimeSpan FbCooldown = TimeSpan.FromSeconds(60);

    static readonly (string Name, string Emoji, int Color)[] FbTypes =
    {
        ("Bug", "🐞", 0xED4245),
        ("Sugestão", "💡", 0x5865F2),
        ("Elogio", "💚", 0x3BA55D),
        ("Outro", "💬", 0x99AAB5),
    };

    static readonly HashSet<string> FbImageExts = new() { ".png", ".jpg", ".jpeg", ".gif", ".webp" };
    static readonly HashSet<string> FbAllowedExts = new()
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".mp4", ".webm", ".mov", ".mkv", ".txt", ".log"
    };

    static readonly HttpClient FbHttp = MakeFbHttp();
    static HttpClient MakeFbHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("AzulGroove-Feedback/" + AppVersion);
        return h;
    }

    static bool FbConfigured =>
        FeedbackWebhook.StartsWith("https://discord.com/api/webhooks/", StringComparison.OrdinalIgnoreCase);

    static string FbTempDir => Path.Combine(Path.GetTempPath(), "AzulGroove-Feedback");

    readonly List<string> fbFiles = new();
    DateTime fbLastSent = DateTime.MinValue;
    bool fbSending, fbCapturing;

    // ================= Relatório automático de erro =================
    static string FbRunFlag => Path.Combine(DataDir, "running.flag");      // existe enquanto o app está aberto
    static string FbCrashFile => Path.Combine(DataDir, "crash-pending.txt"); // erro à espera de envio
    // true se na última vez o app NÃO fechou normalmente (o arquivo "running.flag" sobrou)
    static readonly bool fbUncleanExit = FbDetectUnclean();
    static volatile bool fbClosing;   // true durante o fechamento normal: erros de "desmontagem" NÃO são relatados
    static volatile bool fbAuto;      // espelho de cfg.AutoCrashReport (lido por qualquer thread)
    static bool fbHooksInstalled;
    static DateTime fbLastAuto = DateTime.MinValue;
    bool fbCrashSending;

    // Chamado pelo construtor: marca "app aberto" e registra os captadores de erro
    void FbStartSession()
    {
        fbAuto = cfg.AutoCrashReport;
        // a marca guarda o PID: assim uma 2ª cópia do app (ou o app ainda aberto) não é confundida com "travou"
        try { Directory.CreateDirectory(DataDir); File.WriteAllText(FbRunFlag, Environment.ProcessId + "|" + DateTime.UtcNow.ToString("o")); } catch { }
        if (fbHooksInstalled) return;
        fbHooksInstalled = true;

        // erro na interface: o app continua aberto, então já tenta enviar na hora (só se a pessoa ligou a opção)
        Application.ThreadException += (_, e) =>
        {
            if (!FbShouldRecord(e.Exception)) return;
            FbWriteCrash("Exceção na interface (o app continuou aberto)", e.Exception);
            try { if (!fbClosing && IsHandleCreated && !IsDisposed) BeginInvoke(() => _ = FbFlushCrashAsync()); } catch { }
        };
        // erro fatal: o app vai fechar; o envio acontece na próxima abertura
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (FbShouldRecord(e.ExceptionObject)) FbWriteCrash("Erro fatal (o app fechou)", e.ExceptionObject);
        };
    }

    // Só registra se a opção está ligada, se o app NÃO está fechando e se não é um erro "de sobra" do encerramento
    static bool FbShouldRecord(object? ex) =>
        fbAuto && !fbClosing && !(ex is ObjectDisposedException || ex is OperationCanceledException);

    // Se o arquivo "running.flag" sobrou, o app caiu da última vez — a não ser que o PID dele ainda esteja vivo
    static bool FbDetectUnclean()
    {
        try
        {
            if (!File.Exists(FbRunFlag)) return false;
            var parts = File.ReadAllText(FbRunFlag).Split('|');
            if (parts.Length > 1 && int.TryParse(parts[0], out var pid) && pid != Environment.ProcessId)
            {
                try
                {
                    using var p = System.Diagnostics.Process.GetProcessById(pid);
                    using var me = System.Diagnostics.Process.GetCurrentProcess();
                    if (!p.HasExited && string.Equals(p.ProcessName, me.ProcessName, StringComparison.OrdinalIgnoreCase)) return false;
                }
                catch { /* processo não existe mais: foi queda mesmo */ }
            }
            return true;
        }
        catch { return false; }
    }

    // Chamado quando o app fecha de forma normal
    void FbEndSession()
    {
        try { File.Delete(FbRunFlag); } catch { }
    }

    // Chamado no começo do fechamento de verdade (Sair, atualização, desligar o Windows...)
    void FbBeginShutdown()
    {
        fbClosing = true;
        FbEndSession();
    }

    static void FbWriteCrash(string kind, object? ex)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            var txt = $"{kind}\nHora (UTC): {DateTime.UtcNow:o}\nVersão: {AppVersion}\n\n{ex}\n\n----\n";
            if (File.Exists(FbCrashFile) && new FileInfo(FbCrashFile).Length > 60_000) File.Delete(FbCrashFile);
            File.AppendAllText(FbCrashFile, txt, Encoding.UTF8);
        }
        catch { }
    }

    // Na abertura: se o app caiu da última vez, garante que existe um relatório e tenta enviar
    async Task FbCheckPendingAsync()
    {
        try
        {
            await Task.Delay(8000);
            if (IsDisposed) return;
            if (!fbAuto) { try { File.Delete(FbCrashFile); } catch { } return; } // opção desligada: nada é guardado nem enviado
            if (fbUncleanExit && !File.Exists(FbCrashFile))
                FbWriteCrash("Fechamento inesperado",
                    "Na última vez o app fechou sem avisar (travou, foi encerrado à força ou o PC desligou). Não houve erro registrado.");
            await FbFlushCrashAsync();
        }
        catch { }
    }

    async Task FbFlushCrashAsync()
    {
        if (fbCrashSending || fbClosing || IsDisposed || !File.Exists(FbCrashFile)) return;
        if (!cfg.AutoCrashReport) { try { File.Delete(FbCrashFile); } catch { } return; }
        if (!FbConfigured || cfg.OfflineMode || !NetworkUp()) return; // fica guardado para a próxima vez
        if (DateTime.UtcNow - fbLastAuto < FbCooldown) return;

        fbCrashSending = true;
        try
        {
            var text = FbScrub(File.ReadAllText(FbCrashFile));
            var id = Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
            var shown = text.Length > 1500 ? text[..1500] + "…" : text;

            var embed = new Dictionary<string, object?>
            {
                ["title"] = $"💥 Erro automático · #{id}",
                ["description"] = "```\n" + shown.Replace("```", "'''") + "\n```",
                ["color"] = 0xED4245,
                ["fields"] = new object[]
                {
                    new { name = "Versão", value = AppVersion, inline = true },
                    new { name = "Windows", value = Environment.OSVersion.Version.ToString(), inline = true },
                },
                ["footer"] = new { text = "Relatório automático · Azul Groove Beta " + AppVersion },
                ["timestamp"] = DateTime.UtcNow.ToString("o")
            };
            var payload = new Dictionary<string, object?>
            {
                ["username"] = "Azul Groove Feedback",
                ["allowed_mentions"] = new { parse = Array.Empty<string>() },
                ["embeds"] = new[] { embed }
            };
            if (FeedbackIsForum) payload["thread_name"] = $"[Erro] #{id} automático";

            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"), "payload_json");
            int n = 0;
            var err = new ByteArrayContent(Encoding.UTF8.GetBytes(text));
            err.Headers.ContentType = new MediaTypeHeaderValue("text/plain") { CharSet = "utf-8" };
            form.Add(err, $"files[{n++}]", "erro.txt");
            if (cfg.FbDiag)
            {
                var d = new ByteArrayContent(Encoding.UTF8.GetBytes(FbBuildDiag()));
                d.Headers.ContentType = new MediaTypeHeaderValue("text/plain") { CharSet = "utf-8" };
                form.Add(d, $"files[{n++}]", "diagnostico.txt");
            }

            using var resp = await FbHttp.PostAsync(FeedbackWebhook, form);
            fbLastAuto = DateTime.UtcNow;
            if (resp.IsSuccessStatusCode) { try { File.Delete(FbCrashFile); } catch { } }
        }
        catch { /* sem internet agora: tenta de novo na próxima abertura */ }
        finally { fbCrashSending = false; }
    }

    // Tira o nome do usuário do Windows dos textos
    static string FbScrub(string text)
    {
        try
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (profile.Length > 3) text = text.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
            if (Environment.UserName.Length > 2) text = text.Replace(Environment.UserName, "usuario", StringComparison.OrdinalIgnoreCase);
        }
        catch { }
        return text;
    }

    // ================= Mensagens vindas da página =================
    bool HandleFeedbackMessage(CoreWebView2WebMessageReceivedEventArgs e)
    {
        string? t;
        try { t = e.TryGetWebMessageAsString(); } catch { return false; }
        if (t == null || !t.StartsWith("fb:", StringComparison.Ordinal)) return false;
        // só o painel local ou o chat offline mandam comandos (o site https nunca)
        var onPanel = panelActive && IsPanelSource(e.Source);
        var onLocal = !e.Source.StartsWith("http", StringComparison.OrdinalIgnoreCase) && OnLocalChatPage;
        if (!(onPanel || onLocal) || t.Length > 8000) return true;

        var cmd = t[3..];
        if (cmd == "get") _ = FbPushAsync();
        else if (cmd == "attach") BeginInvoke(FbAttach);
        else if (cmd == "shot") _ = FbCaptureAsync(false);
        else if (cmd == "shotfull") _ = FbCaptureAsync(true);
        else if (cmd == "preview") _ = FbPreviewAsync();
        else if (cmd == "open:form") _ = FbInjectAsync("modal", "form");
        else if (cmd == "open:opts") _ = FbInjectAsync("modal", "opts");
        else if (cmd.StartsWith("rm:", StringComparison.Ordinal) && int.TryParse(cmd[3..], out var i)) FbRemoveAt(i);
        else if (cmd.StartsWith("set:", StringComparison.Ordinal))
        {
            var v = cmd.EndsWith(":1", StringComparison.Ordinal);
            if (cmd.StartsWith("set:diag:", StringComparison.Ordinal)) { cfg.FbDiag = v; cfg.Save(); }
            else if (cmd.StartsWith("set:auto:", StringComparison.Ordinal))
            {
                cfg.AutoCrashReport = v; cfg.Save();
                fbAuto = v;
                try { File.Delete(FbCrashFile); } catch { } // ligar/desligar começa do zero: nada antigo é enviado
                _ = FbPushAsync();
            }
        }
        else if (cmd.StartsWith("send:", StringComparison.Ordinal)) _ = FbSendAsync(cmd[5..]);
        return true;
    }

    // ================= Anexos =================
    static long FbSize(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    long FbTotalBytes() => fbFiles.Sum(FbSize);

    static bool FbIsTemp(string path) =>
        path.StartsWith(FbTempDir, StringComparison.OrdinalIgnoreCase);

    string FbMb(long bytes)
    {
        CultureInfo culture;
        try { culture = CultureInfo.GetCultureInfo(LangCode); }
        catch (CultureNotFoundException) { culture = CultureInfo.CurrentCulture; }
        return (bytes / 1048576.0).ToString("0.#", culture) + " MB";
    }

    void FbAttach()
    {
        using var dlg = new OpenFileDialog
        {
            Title = Tr("s3.fbAttachments"),
            Multiselect = true,
            Filter = "Imagens, vídeos e logs|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp;*.mp4;*.webm;*.mov;*.mkv;*.txt;*.log"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        string? problem = null;
        foreach (var f in dlg.FileNames)
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            if (!FbAllowedExts.Contains(ext)) { problem ??= $"“{Path.GetFileName(f)}”: tipo de arquivo não aceito."; continue; }
            if (fbFiles.Contains(f, StringComparer.OrdinalIgnoreCase)) continue;
            if (fbFiles.Count >= FbMaxFiles) { problem ??= TrF("s3.fbMaxFiles", FbMaxFiles); break; }
            var size = FbSize(f);
            if (size <= 0) { problem ??= $"Não consegui ler “{Path.GetFileName(f)}”."; continue; }
            if (FbTotalBytes() + size > FbMaxTotalBytes)
            {
                problem ??= $"“{Path.GetFileName(f)}” passa do limite de {FbMb(FbMaxTotalBytes)} no total. Para vídeos maiores, envie um link.";
                continue;
            }
            fbFiles.Add(f);
        }
        _ = FbPushAsync(problem ?? "", problem != null ? "err" : "");
    }

    void FbRemoveAt(int i)
    {
        if (i < 0 || i >= fbFiles.Count) return;
        var f = fbFiles[i];
        fbFiles.RemoveAt(i);
        if (FbIsTemp(f)) { try { File.Delete(f); } catch { } }
        _ = FbPushAsync();
    }

    void FbClearFiles()
    {
        foreach (var f in fbFiles.Where(FbIsTemp)) { try { File.Delete(f); } catch { } }
        fbFiles.Clear();
    }

    // full = false: captura SÓ a janela do app, sem escondê-la (ela continua aberta).
    // full = true : esconde o app por um instante e captura a tela inteira.
    async Task FbCaptureAsync(bool full)
    {
        if (full) { await FbSnipAsync(); return; } // "Capturar tela": mesma experiência do recorte do Windows
        if (fbCapturing) return;
        if (fbFiles.Count >= FbMaxFiles) { await FbPushAsync(TrF("s3.fbMaxFiles", FbMaxFiles), "err"); return; }
        if (!full && WindowState == FormWindowState.Minimized) { await FbPushAsync(Tr("s3.fbOpenWindow"), "err"); return; }
        fbCapturing = true;
        var prev = Opacity;
        try
        {
            var screen = Screen.FromControl(this).Bounds;
            Rectangle bounds;
            if (full)
            {
                bounds = screen;
                Opacity = 0;
                await Task.Delay(450); // dá tempo do Windows redesenhar a tela sem o app
            }
            else
            {
                bounds = Rectangle.Intersect(Bounds, screen);
                if (bounds.Width < 10 || bounds.Height < 10) bounds = screen;
                await Task.Delay(120);
            }
            using var bmp = new Bitmap(bounds.Width, bounds.Height);
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
            Opacity = prev;

            Directory.CreateDirectory(FbTempDir);
            var path = Path.Combine(FbTempDir, $"{(full ? "tela" : "janela")}-{DateTime.Now:HHmmss}.png");
            bmp.Save(path, ImageFormat.Png);

            if (FbTotalBytes() + FbSize(path) > FbMaxTotalBytes)
            {
                try { File.Delete(path); } catch { }
                await FbPushAsync(TrF("s3.fbCaptureTooBig", FbMb(FbMaxTotalBytes)), "err");
                return;
            }
            fbFiles.Add(path);
            await FbPushAsync(Tr("s3.fbCaptureAdded"), "");
        }
        catch
        {
            await FbPushAsync(Tr("s3.fbCaptureFailed"), "err");
        }
        finally { Opacity = prev; fbCapturing = false; }
    }

    // ================= Diagnóstico =================
    string FbBuildDiag()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Azul Groove: diagnóstico");
        sb.AppendLine($"Versão do app: {AppVersion}");
        sb.AppendLine($"Instalação: {(IsInstalledBySetup() ? "Setup" : "Portable")}");
        sb.AppendLine($"Windows: {Environment.OSVersion.Version} ({(Environment.Is64BitOperatingSystem ? "64" : "32")} bits)");
        sb.AppendLine($".NET: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        try { sb.AppendLine($"WebView2: {web.CoreWebView2?.Environment.BrowserVersionString}"); } catch { }
        sb.AppendLine($"Idioma: {CultureInfo.CurrentUICulture.Name}");
        sb.AppendLine($"Modo offline: {(cfg.OfflineMode ? "sim" : "não")}");
        sb.AppendLine($"Tema: {cfg.Theme}  ·  Página inicial: {cfg.StartPage}  ·  Atalho: {(cfg.HotkeyOn ? "ligado" : "desligado")}");
        sb.AppendLine($"IA local: modelo escolhido = {(lai.Active.Length > 0 ? lai.Active : "nenhum")}, Vulkan = {(lai.Vulkan ? "sim" : "não")}, ligada agora = {(laiRunning.Length > 0 ? laiRunning : "não")}");
        sb.AppendLine($"Rich Presence: {(rpcCfg.On ? rpcStatus : "desligado")}");

        string[] lines;
        lock (laiLog) lines = laiLog.ToArray();
        if (lines.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Últimas linhas do motor de IA:");
            foreach (var l in lines.Where(x => x.Trim().Length > 0).TakeLast(30))
                sb.AppendLine("  " + (l.Length > 300 ? l[..300] : l.TrimEnd()));
        }
        return FbScrub(sb.ToString());
    }

    async Task FbPreviewAsync()
    {
        var txt = FbBuildDiag();
        try { await web.CoreWebView2.ExecuteScriptAsync($"window.__azFb&&window.__azFb.preview({JsonSerializer.Serialize(txt)})"); }
        catch { }
    }

    // ================= Envio =================
    async Task FbSendAsync(string json)
    {
        if (fbSending) return;

        string type, msg, contact; bool withDiag;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            type = JStr(r, "type");
            msg = JStr(r, "msg").Trim();
            contact = JStr(r, "contact").Trim().Replace('\r', ' ').Replace('\n', ' ');
            withDiag = r.TryGetProperty("diag", out var dg) && dg.ValueKind == JsonValueKind.True;
        }
        catch { return; }

        if (!FbConfigured) { await FbPushAsync(Tr("s3.fbNotConfigured"), "err"); return; }
        if (cfg.OfflineMode) { await FbPushAsync(Tr("s3.fbOffline"), "err"); return; }
        var wait = (int)Math.Ceiling((fbLastSent + FbCooldown - DateTime.UtcNow).TotalSeconds);
        if (wait > 0) { await FbPushAsync(TrF("s3.fbWait", wait), "err"); return; }

        var kind = FbTypes.FirstOrDefault(x => x.Name == type);
        if (kind.Name == null) { await FbPushAsync(Tr("s3.fbChooseType"), "err"); return; }
        if (msg.Length < FbMinChars) { await FbPushAsync(TrF("s3.fbMinChars", FbMinChars), "err"); return; }
        if (msg.Length > FbMaxChars) msg = msg[..FbMaxChars];
        if (contact.Length > 80) contact = contact[..80];

        fbSending = true;
        await FbPushAsync();
        string note; string result;
        var id = Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        try
        {
            var atts = new List<(string Path, string Name)>();
            foreach (var f in fbFiles.ToList())
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (!File.Exists(f) || !FbAllowedExts.Contains(ext)) continue;
                atts.Add((f, $"anexo{atts.Count + 1}{ext}"));
            }
            var firstImage = atts.FirstOrDefault(a => FbImageExts.Contains(Path.GetExtension(a.Name)));

            var fields = new List<object>();
            if (contact.Length > 0) fields.Add(new { name = "Contato", value = contact, inline = true });
            fields.Add(new { name = "Versão", value = AppVersion, inline = true });
            fields.Add(new { name = "Windows", value = Environment.OSVersion.Version.ToString(), inline = true });
            if (atts.Count > 0) fields.Add(new { name = "Anexos", value = atts.Count.ToString(), inline = true });

            var embed = new Dictionary<string, object?>
            {
                ["title"] = $"{kind.Emoji} {kind.Name} · #{id}",
                ["description"] = msg,
                ["color"] = kind.Color,
                ["fields"] = fields,
                ["footer"] = new { text = "Azul Groove Beta " + AppVersion },
                ["timestamp"] = DateTime.UtcNow.ToString("o")
            };
            if (firstImage.Name != null) embed["image"] = new { url = "attachment://" + firstImage.Name };

            var payload = new Dictionary<string, object?>
            {
                ["username"] = "Azul Groove Feedback",
                ["allowed_mentions"] = new { parse = Array.Empty<string>() },
                ["embeds"] = new[] { embed }
            };
            if (FeedbackIsForum)
            {
                var first = msg.Replace('\n', ' ');
                var title = $"[{kind.Name}] #{id} {(first.Length > 60 ? first[..60] : first)}";
                payload["thread_name"] = title.Length > 100 ? title[..100] : title;
            }

            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"), "payload_json");

            int n = 0;
            foreach (var a in atts)
            {
                var fs = new FileStream(a.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var part = new StreamContent(fs);
                part.Headers.ContentType = new MediaTypeHeaderValue(FbMime(Path.GetExtension(a.Name)));
                form.Add(part, $"files[{n++}]", a.Name);
            }
            if (withDiag)
            {
                var part = new ByteArrayContent(Encoding.UTF8.GetBytes(FbBuildDiag()));
                part.Headers.ContentType = new MediaTypeHeaderValue("text/plain") { CharSet = "utf-8" };
                form.Add(part, $"files[{n++}]", "diagnostico.txt");
            }

            using var resp = await FbHttp.PostAsync(FeedbackWebhook, form);
            if (resp.IsSuccessStatusCode)
            {
                fbLastSent = DateTime.UtcNow;
                FbClearFiles();
                note = TrF("s3.fbSent", id);
                result = "ok";
            }
            else
            {
                var code = (int)resp.StatusCode;
                result = "err";
                note = code switch
                {
                    429 => Tr("s3.fbRateLimit"),
                    413 => Tr("s3.fbAttachmentsTooBig"),
                    401 or 403 or 404 => Tr("s3.fbChannelUnavailable"),
                    _ => TrF("s3.fbSendFailed", code)
                };
            }
        }
        catch (TaskCanceledException) { result = "err"; note = Tr("s3.fbTimeout"); }
        catch (HttpRequestException) { result = "err"; note = Tr("s3.fbDiscordOffline"); }
        catch (IOException) { result = "err"; note = Tr("s3.fbReadAttachment"); }
        catch { result = "err"; note = Tr("s3.fbUnexpected"); }
        finally { fbSending = false; }

        await FbPushAsync(note, result);
    }

    static string FbMime(string ext) => ext switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".mp4" => "video/mp4",
        ".webm" => "video/webm",
        ".mov" => "video/quicktime",
        ".mkv" => "video/x-matroska",
        ".txt" or ".log" => "text/plain",
        _ => "application/octet-stream"
    };

    // ================= Página: estado + injeção =================
    object FbStateObject(string note = "", string result = "")
    {
        fbFiles.RemoveAll(f => !File.Exists(f));
        var left = (int)Math.Max(0, Math.Ceiling((fbLastSent + FbCooldown - DateTime.UtcNow).TotalSeconds));
        return new
        {
            files = fbFiles.Select(f => new { name = Path.GetFileName(f), size = FbSize(f) }).ToArray(),
            busy = fbSending,
            wait = left,
            diag = cfg.FbDiag,
            auto = cfg.AutoCrashReport,
            note,
            result
        };
    }

    async Task FbPushAsync(string note = "", string result = "")
    {
        if (web.CoreWebView2 == null || IsDisposed || !(panelActive || OnLocalChatPage)) return;
        try
        {
            var json = JsonSerializer.Serialize(FbStateObject(note, result));
            await web.CoreWebView2.ExecuteScriptAsync($"window.__azFb&&window.__azFb.state({json})");
        }
        catch { /* a página funciona sem isso */ }
    }

    void FbOnNavigationCompleted()
    {
        if (panelActive && betaWizardTcs == null)
        {
            _ = FbInjectButtonAsync();      // só um botão leve; o formulário é montado quando a pessoa clicar
            RpcUiOnNavigationCompleted();   // abre o menu do RPC se foi pedido pela bandeja
        }
    }

    async Task FbInjectButtonAsync()
    {
        try
        {
            if (web.CoreWebView2 == null || IsDisposed) return;
            await web.CoreWebView2.ExecuteScriptAsync(FbButtonScript.Replace("@T@", TextsJson("fbb.")));
        }
        catch { /* o painel funciona sem o botão */ }
    }

    // Botão único "Feedback" com um submenu (usa as classes .dd/.ddb/.ddl do próprio painel)
    const string FbButtonScript = """
(function(){try{
 if(location.protocol==='https:')return;
 var w=document.querySelector('.w');if(!w||document.getElementById('az-fbb'))return;
 var T=@T@;function t(k){return T[k]!=null?T[k]:k}
 function h(t,c,x){var e=document.createElement(t);if(c)e.className=c;if(x!=null)e.textContent=x;return e}
 function post(m){try{window.chrome.webview.postMessage(m)}catch(e){}}
 var root=h('div');root.id='az-fbb';
 root.appendChild(h('h2','',t('fbb.h')));
 var c=h('div','c'),r=h('div','r'),l=h('div');
 l.appendChild(document.createTextNode(t('fbb.t')));
 l.appendChild(h('small','',t('fbb.s')));
 r.appendChild(l);
 var dd=h('div','dd'),b=h('button','ddb',t('fbb.open')),ls=h('div','ddl');b.type='button';
 function item(t,m){var o=h('div','ddo',t);o.onclick=function(ev){ev.stopPropagation();dd.classList.remove('open');post(m)};ls.appendChild(o)}
 item(t('fbb.send'),'fb:open:form');
 item(t('fbb.diag'),'fb:open:opts');
 b.onclick=function(ev){ev.stopPropagation();var o=dd.classList.contains('open');document.querySelectorAll('.dd.open').forEach(function(x){x.classList.remove('open')});dd.classList.toggle('open',!o)};
 dd.appendChild(b);dd.appendChild(ls);r.appendChild(dd);c.appendChild(r);root.appendChild(c);
 w.insertBefore(root,w.querySelector('.ft'));
}catch(e){}})();
""";

    // host: "panel" (fim do painel) ou "local" (aba Feedback das configurações do chat offline)
    // host: "modal" (janela do painel; view = "form" ou "opts") ou "local" (aba do chat offline)
    async Task FbInjectAsync(string host, string view = "all")
    {
        try
        {
            if (web.CoreWebView2 == null || IsDisposed) return;
            if (host == "modal" && !panelActive) return;
            var state = JsonSerializer.Serialize(new
            {
                host,
                view,
                ok = FbConfigured,
                types = FbTypes.Select(t => new
                {
                    name = t.Name,
                    label = Tr(t.Name switch
                    {
                        "Bug" => "s3.fbTypeBug",
                        "Sugestão" => "s3.fbTypeIdea",
                        "Elogio" => "s3.fbTypePraise",
                        _ => "s3.fbTypeOther"
                    }),
                    emoji = t.Emoji
                }).ToArray(),
                min = FbMinChars,
                max = FbMaxChars,
                maxFiles = FbMaxFiles,
                maxMb = FbMb(FbMaxTotalBytes),
                init = FbStateObject()
            });
            await web.CoreWebView2.ExecuteScriptAsync(FbPanelScript.Replace("@STATE@", state).Replace("@T@", TextsJson("fbb.", "s3.")));
        }
        catch { /* a página funciona sem o bloco */ }
    }

    const string FbPanelScript = """
(function(S){try{
 var local=S.host==='local',modal=S.host==='modal',view=S.view||'all';
 var T=@T@;function t(k){return T[k]||k}
 var host=local?document.getElementById('fbHost'):document.querySelector('.w');
 if(!host)return;
 if(!local&&location.protocol==='https:')return;
 var old=document.getElementById('az-fb');if(old)old.remove();
 var oldm=document.getElementById('az-fbm');if(oldm)oldm.remove();
 function h(t,c,x){var e=document.createElement(t);if(c)e.className=c;if(x!=null)e.textContent=x;return e}
 function post(m){try{window.chrome.webview.postMessage(m)}catch(e){}}
 function fmt(n){return n>=1048576?(n/1048576).toFixed(1).replace('.',',')+' MB':Math.max(1,Math.round(n/1024))+' KB'}
 var IR='<svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 12a9 9 0 1 1-3-6.7L21 8"/><path d="M21 3v5h-5"/></svg>';
 var IS='<svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><path d="M22 2L11 13"/><path d="M22 2l-7 20-4-9-9-4 20-7z"/></svg>';
 var HINT={'Bug':t('s3.fbBugHint'),'Sugestão':t('s3.fbIdeaHint'),'Elogio':t('s3.fbPraiseHint'),'Outro':t('s3.fbOtherHint')};
 var PH={'Bug':t('s3.fbBugPrompt'),'Sugestão':t('s3.fbIdeaPrompt'),'Elogio':t('s3.fbPraisePrompt'),'Outro':t('s3.fbOtherPrompt')};

 var root=h('div');root.id='az-fb';
 var ov,mc;
 if(modal){ov=h('div','md');ov.id='az-fbm';mc=h('div','mc');ov.appendChild(mc);ov.onclick=function(e){if(e.target===ov)closeM()}}
 function esc(e){if(e.key==='Escape')closeM()}
 function closeM(){var o=document.getElementById('az-fbm');if(o)o.remove();window.__azFb=null;document.removeEventListener('keydown',esc)}
 if(modal)document.addEventListener('keydown',esc);
 var sty=h('style');sty.textContent=
  '#az-fb{--fa:var(--ac,var(--p,#5865f2));--fa2:var(--ac2,var(--fa));--ft:var(--tx,#f2f4ff);--fm:var(--mu,var(--m,#9aa3b2));--fl:var(--line,rgba(255,255,255,.12));--ff:color-mix(in srgb,var(--ft) 6%,transparent);text-align:start;color:var(--ft);font-size:.92rem}'+
  '#az-fb .c{padding:18px 20px}'+
  '#az-fb .fbhd{margin-bottom:6px}#az-fb .fbhd b{display:block;font-size:1.05rem}'+
  '#az-fb .fbm{color:var(--fm);font-size:.83rem;line-height:1.45}'+
  '#az-fb .fbl{font-size:.72rem;font-weight:700;letter-spacing:.08em;text-transform:uppercase;color:var(--fm);margin:20px 0 8px}'+
  '#az-fb .fbts{display:grid;grid-template-columns:repeat(auto-fit,minmax(118px,1fr));gap:8px}'+
  '#az-fb .fbt{display:flex;flex-direction:column;align-items:flex-start;gap:2px;text-align:left;background:var(--ff);border:1.5px solid transparent;color:var(--ft);border-radius:14px;padding:11px 13px;font:inherit;cursor:pointer;transition:.15s}'+
  '#az-fb .fbt i{font-style:normal;font-size:1.35rem;margin-bottom:2px}#az-fb .fbt b{font-size:.92rem}#az-fb .fbt span{font-size:.76rem;color:var(--fm)}'+
  '#az-fb .fbt:hover{border-color:var(--fl)}'+
  '#az-fb .fbt.on{border-color:var(--fa);background:color-mix(in srgb,var(--fa) 18%,transparent)}'+
  '#az-fb .fbin{display:block;width:100%;box-sizing:border-box;background:var(--ff);color:var(--ft);border:1px solid var(--fl);border-radius:12px;padding:11px 14px;font:inherit;font-size:.92rem;line-height:1.45;outline:none;transition:.15s}'+
  '#az-fb textarea.fbin{min-height:130px;resize:vertical}'+
  '#az-fb .fbin:focus{border-color:var(--fa);box-shadow:0 0 0 3px color-mix(in srgb,var(--fa) 25%,transparent)}'+
  '#az-fb .fbc{text-align:end;margin-top:5px;color:var(--fm);font-size:.78rem}'+
  '#az-fb .fbd{border:1.5px dashed var(--fl);border-radius:14px;padding:14px 16px}'+
  '#az-fb .fbb{display:flex;gap:8px;flex-wrap:wrap;margin-top:10px}'+
  '#az-fb .fbbtn{background:var(--ff);border:1px solid var(--fl);color:var(--ft);border-radius:10px;padding:8px 14px;font:inherit;font-size:.86rem;cursor:pointer;transition:.15s}'+
  '#az-fb .fbbtn:hover:not(:disabled){border-color:var(--fa)}#az-fb .fbbtn:disabled{opacity:.5;cursor:default}'+
  '#az-fb .fbpri{border:0;background:linear-gradient(135deg,var(--fa),var(--fa2));color:#fff;font-weight:600;padding:10px 22px;border-radius:12px;display:inline-flex;gap:8px;align-items:center;box-shadow:0 6px 18px color-mix(in srgb,var(--fa) 40%,transparent)}'+
  '#az-fb .fbpri:hover:not(:disabled){filter:brightness(1.1);border:0}'+
  '#az-fb .fbpri.spin svg{animation:fbspin .9s linear infinite}@keyframes fbspin{to{transform:rotate(360deg)}}'+
  '#az-fb .chips{display:flex;flex-wrap:wrap;gap:8px;margin-top:12px}#az-fb .chips:empty{display:none}'+
  '#az-fb .chip{display:inline-flex;align-items:center;gap:6px;background:var(--ff);border:1px solid var(--fl);border-radius:999px;padding:4px 6px 4px 12px;font-size:.84rem}'+
  '#az-fb .chip span{max-width:230px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}'+
  '#az-fb .chip button{background:none;border:0;color:var(--fm);cursor:pointer;font:inherit;width:22px;height:22px;border-radius:50%}'+
  '#az-fb .chip button:hover{background:rgba(237,66,69,.2);color:#ed4245}'+
  '#az-fb .fbo{display:flex;justify-content:space-between;align-items:center;gap:14px;padding:12px 14px;border-radius:12px;background:var(--ff);margin-top:8px}'+
  '#az-fb .fbo b{display:block;font-weight:600;font-size:.9rem}'+
  '#az-fb .fbk{background:none;border:0;padding:0;color:var(--fa);font:inherit;font-size:.82rem;cursor:pointer;text-decoration:underline}'+
  '#az-fb .fbsw{position:relative;width:42px;height:24px;border-radius:24px;background:color-mix(in srgb,var(--fm) 40%,transparent);cursor:pointer;flex:none;transition:.2s;outline:none}'+
  '#az-fb .fbsw::after{content:"";position:absolute;top:3px;left:3px;width:18px;height:18px;border-radius:50%;background:#fff;transition:.2s}'+
  '#az-fb .fbsw.on{background:var(--fa)}#az-fb .fbsw.on::after{transform:translateX(18px)}'+
  '#az-fb .fbsw:focus-visible{box-shadow:0 0 0 3px color-mix(in srgb,var(--fa) 35%,transparent)}'+
  '#az-fb .fbp{white-space:pre-wrap;font-family:Consolas,monospace;font-size:.78rem;color:var(--fm);background:var(--ff);border:1px solid var(--fl);border-radius:10px;padding:12px;margin-top:10px;max-height:200px;overflow:auto}'+
  '#az-fb .fbf{display:flex;justify-content:space-between;align-items:center;gap:14px;flex-wrap:wrap;margin-top:20px;padding-top:16px;border-top:1px solid var(--fl)}'+
  '#az-fb .fbs{flex:1;min-width:180px;font-size:.9rem;color:var(--fm)}'+
  '#az-fb .fbs.upok{color:#3ba55d}#az-fb .fbs.uperr{color:#ed4245}'+
  '#az-fb .fbmh{display:flex;justify-content:space-between;align-items:center;gap:10px;margin-bottom:6px}#az-fb .fbmh b{font-size:1.12rem}';
 root.appendChild(sty);
 if(modal){var mh=h('div','fbmh');mh.appendChild(h('b','',view==='opts'?t('fbb.diag'):t('fbb.send')));
  var xb=h('button','fbbtn','\u2715');xb.type='button';xb.title='Fechar';xb.onclick=closeM;mh.appendChild(xb);root.appendChild(mh)}
 else if(!local)root.appendChild(h('h2','',t('fbb.send')));
 var c=h('div',local||modal?'':'c');root.appendChild(c);
 // 3 grupos: formulário · opções · botão de enviar (o "submenu" mostra só o que interessa)
 var gA=h('div'),gO=h('div'),gB=h('div');c.appendChild(gA);c.appendChild(gO);c.appendChild(gB);
 if(view==='opts')gA.style.display=gB.style.display='none';else if(view==='form')gO.style.display='none';
 var hd=h('div','fbhd');hd.appendChild(h('b','',t('s3.fbPrompt')));
 hd.appendChild(h('div','fbm',t('s3.fbHelp')));gA.appendChild(hd);

 // 1 · tipo
 gA.appendChild(h('div','fbl','1 · '+t('s3.fbType')));
 var ts=h('div','fbts'),cur=S.types[0].name,tb=[];
 var msg=h('textarea','fbin');
 function mark(){tb.forEach(function(x){x[0].classList.toggle('on',x[1]===cur)});msg.placeholder=PH[cur]||PH['Outro']}
 S.types.forEach(function(t){var b=h('button','fbt');b.type='button';b.appendChild(h('i','',t.emoji));b.appendChild(h('b','',t.label));b.appendChild(h('span','',HINT[t.name]||''));
  b.onclick=function(){cur=t.name;mark()};ts.appendChild(b);tb.push([b,t.name])});
 gA.appendChild(ts);

 // 2 · mensagem
 gA.appendChild(h('div','fbl','2 · '+t('s3.fbMessage')));
 msg.maxLength=S.max;msg.rows=5;
 var cnt=h('div','fbc');
 function upd(){cnt.textContent=msg.value.length+' / '+S.max+(msg.value.trim().length<S.min?'  ·  '+t('s3.fbMinChars').replace('{0}',S.min):'')}
 msg.oninput=upd;upd();mark();
 gA.appendChild(msg);gA.appendChild(cnt);

 // contato
 gA.appendChild(h('div','fbl',t('s3.fbContact')));
 var ct=h('input','fbin');ct.maxLength=80;ct.spellcheck=false;ct.placeholder=t('s3.fbContactHint');
 gA.appendChild(ct);

 // 3 · anexos
 gA.appendChild(h('div','fbl','3 · '+t('s3.fbAttachments')));
 var dz=h('div','fbd');
 dz.appendChild(h('div','fbm',t('s3.fbAttachmentHint').replace('{0}',S.maxFiles).replace('{1}',S.maxMb)));
 var bx=h('div','fbb');
 var b1=h('button','fbbtn','📎 '+t('s3.fbAttachFile')),b2=h('button','fbbtn','🪟 '+t('s3.fbWindow')),b3=h('button','fbbtn','✂️ '+t('s3.fbScreen'));
 b1.type=b2.type=b3.type='button';
 b2.title=t('s3.fbCaptureWindowHint');
 b3.title=t('s3.fbCaptureScreenHint');
 b1.onclick=function(){post('fb:attach')};b2.onclick=function(){post('fb:shot')};b3.onclick=function(){post('fb:shotfull')};
 bx.appendChild(b1);bx.appendChild(b2);bx.appendChild(b3);dz.appendChild(bx);
 var chips=h('div','chips');dz.appendChild(chips);gA.appendChild(dz);
 var files=[];
 function renderChips(){chips.textContent='';files.forEach(function(f,i){var p=h('span','chip');p.appendChild(h('span','',f.name+' · '+fmt(f.size)));var x=h('button','','✕');x.type='button';x.title='Remover';x.onclick=function(){post('fb:rm:'+i)};p.appendChild(x);chips.appendChild(p)})}

 // opções
 gO.appendChild(h('div','fbl',t('s3.fbOptions')));
 var diag=!!S.init.diag,auto=!!S.init.auto;
 function tgl(on,cb){var d=h('div','fbsw'+(on?' on':''));d.setAttribute('role','switch');d.tabIndex=0;
  function flip(){d.classList.toggle('on');cb(d.classList.contains('on'))}
  d.onclick=flip;d.onkeydown=function(e){if(e.key===' '||e.key==='Enter'){e.preventDefault();flip()}};
  d.set=function(v){d.classList.toggle('on',!!v)};return d}
 var pre=h('pre','fbp');pre.style.display='none';
 var o1=h('div','fbo'),l1=h('div');l1.appendChild(h('b','',t('fbb.diag')));
 var s1=h('div','fbm',t('s3.fbDiagDescription')+' ');
 var pv=h('button','fbk',t('s3.fbPreview'));pv.type='button';
 pv.onclick=function(){if(pre.style.display==='block'){pre.style.display='none';pv.textContent=t('s3.fbPreview')}else post('fb:preview')};
 s1.appendChild(pv);l1.appendChild(s1);o1.appendChild(l1);
 var t1=tgl(diag,function(v){diag=v;post('fb:set:diag:'+(v?1:0))});o1.appendChild(t1);gO.appendChild(o1);gO.appendChild(pre);
 function preview(text){pre.textContent=text;pre.style.display='block';pv.textContent=t('s3.fbHide')}
 var o2=h('div','fbo'),l2=h('div');l2.appendChild(h('b','',t('s3.fbAutoTitle')));
 l2.appendChild(h('div','fbm',t('s3.fbAutoDescription')));
 o2.appendChild(l2);var t2=tgl(auto,function(v){auto=v;post('fb:set:auto:'+(v?1:0))});o2.appendChild(t2);gO.appendChild(o2);

 // enviar
 var ft=h('div','fbf'),st=h('div','fbs');
 var sb=h('button','fbbtn fbpri');sb.type='button';ft.appendChild(st);ft.appendChild(sb);gB.appendChild(ft);
 function setStatus(t,cls){st.className='fbs'+(cls?' '+cls:'');st.textContent=t}
 var busy=false,wait=0,tm=0;
 function setBtn(ic,t,spin,dis){sb.innerHTML=ic+'<span>'+t+'</span>';sb.classList.toggle('spin',!!spin);sb.disabled=!!dis}
 function refresh(){clearTimeout(tm);
  if(!S.ok)setBtn(IS,t('fbb.send'),false,true);
  else if(busy)setBtn(IR,t('s3.fbSending'),true,true);
  else if(wait>0){setBtn(IS,'Aguarde '+wait+' s',false,true);tm=setTimeout(function(){wait--;refresh()},1000)}
  else setBtn(IS,t('fbb.send'),false,false)}
 sb.onclick=function(){
  var m=msg.value.trim();
  if(m.length<S.min){setStatus('Escreva pelo menos '+S.min+' caracteres.','uperr');msg.focus();return}
  busy=true;refresh();setStatus(t('s3.fbSending'),'');
  post('fb:send:'+JSON.stringify({type:cur,msg:m,contact:ct.value.trim(),diag:diag}))};

 function state(d){
  files=d.files||[];renderChips();busy=!!d.busy;wait=d.wait||0;
  if(typeof d.diag==='boolean'){diag=d.diag;t1.set(diag)}
  if(typeof d.auto==='boolean'){auto=d.auto;t2.set(auto)}
  if(d.note)setStatus(d.note,d.result==='ok'?'upok':d.result==='err'?'uperr':'');
  if(d.result==='ok'){msg.value='';upd()}
  refresh()}

 if(!S.ok)setStatus('O envio de feedback ainda não foi configurado nesta versão.','uperr');
 state(S.init||{});
 window.__azFb={state:state,preview:preview};
 if(local)host.appendChild(root);else if(modal){mc.appendChild(root);document.body.appendChild(ov)}else host.insertBefore(root,host.querySelector('.ft'));
}catch(e){}})(@STATE@);
""";
}
