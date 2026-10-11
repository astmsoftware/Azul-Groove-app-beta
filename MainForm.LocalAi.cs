using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AzulGroove;

/// <summary>
/// IA própria do Azul Groove, rodando dentro do app (sem instalar Ollama, LM Studio etc.).
///  • Motor: llama.cpp ("llama-server.exe"), que o app usa como processo filho escondido, só neste PC (127.0.0.1).
///    Procura primeiro em  [pasta do app]\engine\cpu  (ou \vulkan) — dá para ir dentro do instalador —
///    e, se não achar, baixa uma vez do GitHub oficial (ggml-org/llama.cpp) para %LocalAppData%\AzulGroove\engine.
///  • Modelos: arquivos .gguf em %LocalAppData%\AzulGroove\models. O app baixa da lista abaixo (Hugging Face),
///    de um link .gguf que o usuário colar, ou importa um .gguf que já esteja no PC.
///  • Depois de baixados, motor e modelo funcionam 100% offline.
///  • O chat usa o provedor "azul" (porta 18741), o mesmo protocolo OpenAI que as outras IAs.
///  • Configurações: %LocalAppData%\AzulGroove\localai.json
/// </summary>
public partial class MainForm
{
    const int LAiPort = 18741;
    const string LAiBase = "http://127.0.0.1:18741/v1";

    // ---- lista de modelos (Q4_K_M: bom equilíbrio entre qualidade e tamanho) ----
    static readonly LAiCat[] LAiCatalog =
    {
        new("qwen25-15b", "Qwen 2.5 · 1.5B",
            "Muito leve e rápido. Ideal para PCs simples; entende português bem para o tamanho.",
            "bartowski/Qwen2.5-1.5B-Instruct-GGUF", "Qwen2.5-1.5B-Instruct-Q4_K_M.gguf", 1.1, 4, "Apache 2.0"),
        new("llama32-3b", "Llama 3.2 · 3B",
            "Equilíbrio entre qualidade e velocidade. Boa escolha para a maioria dos PCs.",
            "bartowski/Llama-3.2-3B-Instruct-GGUF", "Llama-3.2-3B-Instruct-Q4_K_M.gguf", 2.0, 8, "Llama 3.2 Community"),
        new("qwen25-3b", "Qwen 2.5 · 3B",
            "Parecido com o Llama 3B, com respostas um pouco mais longas. Licença só para uso não comercial.",
            "bartowski/Qwen2.5-3B-Instruct-GGUF", "Qwen2.5-3B-Instruct-Q4_K_M.gguf", 1.9, 8, "Qwen Research"),
        new("qwen25-7b", "Qwen 2.5 · 7B",
            "Respostas bem melhores, mas é pesado: pede 16 GB de RAM e responde mais devagar.",
            "bartowski/Qwen2.5-7B-Instruct-GGUF", "Qwen2.5-7B-Instruct-Q4_K_M.gguf", 4.7, 16, "Apache 2.0"),
    };

    readonly LAiSettings lai = LAiSettings.Load();
    readonly SemaphoreSlim laiGate = new(1, 1);
    readonly Queue<string> laiLog = new();
    Process? laiProc;
    string laiRunning = "";          // id do modelo carregado agora
    bool laiRunningVk;
    string laiPhase = "idle";        // idle, download, extract, load, ready, error
    string laiText = "";
    int laiPct;
    CancellationTokenSource? laiDlCts;
    string laiDlId = "";
    bool laiHooked;
    bool laiWinHooked;
    bool laiAway;           // janela minimizada ou escondida na bandeja
    bool laiPausedByMin;    // o motor foi fechado por causa disso (e volta sozinho ao reabrir)

    static string LAiModelsDir => Path.Combine(DataDir, "models");
    static string LAiEngineRoot => Path.Combine(DataDir, "engine");

    // ================= comandos vindos da página =================
    void LAiHandle(string name, string arg)
    {
        LAiHook();
        switch (name)
        {
            case "aistate": _ = LAiPushStateAsync(); break;
            case "mdl_dl": _ = LAiDownloadModelAsync(arg); break;
            case "mdl_dluse": _ = LAiDownloadAndUseAsync(arg); break;
            case "mdl_cancel": try { laiDlCts?.Cancel(); } catch { } break;
            case "mdl_use": _ = LAiUseAsync(arg); break;
            case "mdl_del": _ = LAiDeleteAsync(arg); break;
            case "mdl_url": _ = LAiAddUrlAsync(arg); break;
            case "mdl_import": BeginInvoke(() => _ = LAiImportAsync()); break;
            case "mdl_vulkan": _ = LAiSetVulkanAsync(arg == "1"); break;
        }
    }

    // Garante que o motor feche junto com o app (e limpa sobras de uma queda anterior)
    void LAiHook()
    {
        if (laiHooked) return;
        laiHooked = true;
        Application.ApplicationExit += (_, _) => LAiStop();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => LAiStop();
        LAiKillOrphans();
        LAiHookWindow();
    }

    // ---- "Fechar a IA ao minimizar" (opção do painel) ----
    // Minimizou ou foi para a bandeja -> fecha o motor e libera a memória. Voltou -> liga de novo sozinho.
    void LAiHookWindow()
    {
        if (laiWinHooked) return;
        laiWinHooked = true;
        Resize += (_, _) => LAiOnWindowChange();
        VisibleChanged += (_, _) => LAiOnWindowChange();
    }

    void LAiOnWindowChange()
    {
        if (IsDisposed || !IsHandleCreated) return;
        var away = WindowState == FormWindowState.Minimized || !Visible;
        if (away == laiAway) return;
        laiAway = away;
        if (away)
        {
            if (!lai.StopOnMinimize) return;
            if (lchatBusy || laiGate.CurrentCount == 0) return; // respondendo ou ainda carregando: não corta no meio
            if (laiProc is { HasExited: false })
            {
                LAiStop();
                laiPausedByMin = true;
                _ = LAiPushStateAsync();
            }
        }
        else if (laiPausedByMin)
        {
            laiPausedByMin = false;
            _ = LAiResumeAsync();
        }
    }

    async Task LAiResumeAsync()
    {
        try
        {
            if (IsDisposed || !OnLocalChatPage) return; // fora do chat offline, liga só quando for usar (LAiEnsureRunningAsync)
            if (lchatData.Provider != "azul" || lai.Active.Length == 0 || !LAiHave(lai.Active)) return;
            await LAiStartAsync(lai.Active);
            await LAiPushStateAsync();
        }
        catch { }
    }

    async Task LAiOnInitAsync()
    {
        LAiHook();
        await LAiPushStateAsync();
        var d = lchatData;
        if (d.Provider == "azul" && lai.Active.Length > 0 && LAiHave(lai.Active))
        {
            await LAiStartAsync(lai.Active);
            await LAiPushStateAsync();
        }
    }

    // Chamado antes de enviar uma mensagem: se a IA do app não estiver de pé, liga
    async Task<bool> LAiEnsureRunningAsync()
    {
        LAiHook();
        if (lai.Active.Length == 0 || !LAiHave(lai.Active))
        {
            LAiPhase("idle", ""); // a tela mostra um cartão com as opções
            return false;
        }
        if (laiProc is { HasExited: false } && laiRunning == lai.Active && laiRunningVk == lai.Vulkan && await LAiHealthAsync()) return true;
        var ok = await LAiStartAsync(lai.Active);
        await LAiPushStateAsync();
        return ok;
    }

    // ================= modelos =================
    LAiItem[] LAiItems()
    {
        var ram = LAiRamGb();
        var rec = ram >= 15 ? "qwen25-7b" : ram >= 7 ? "llama32-3b" : "qwen25-15b";
        var list = new List<LAiItem>();
        foreach (var c in LAiCatalog)
            list.Add(new LAiItem(c.Id, c.Name, c.Desc, c.Gb, c.Ram, c.Lic, File.Exists(LAiModelPath(c.Id)), false, c.Id == rec));
        foreach (var c in lai.Custom)
        {
            var p = LAiModelPath(c.Id);
            double gb = 0;
            try { if (File.Exists(p)) gb = Math.Round(new FileInfo(p).Length / 1073741824.0, 2); } catch { }
            list.Add(new LAiItem(c.Id, c.Name, c.Url.Length > 0 ? "Baixado de um link." : "Arquivo do seu PC.", gb, 0, "", File.Exists(p), true, false));
        }
        return list.ToArray();
    }

    static int LAiRamGb()
    {
        try { return (int)Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1073741824.0); }
        catch { return 0; }
    }

    string LAiModelPath(string id)
    {
        var c = LAiCatalog.FirstOrDefault(x => x.Id == id);
        if (c != null) return Path.Combine(LAiModelsDir, c.File);
        var u = lai.Custom.FirstOrDefault(x => x.Id == id);
        if (u == null) return "";
        return u.Path.Length > 0 ? u.Path : Path.Combine(LAiModelsDir, u.File);
    }

    bool LAiHave(string id)
    {
        var p = LAiModelPath(id);
        return p.Length > 0 && File.Exists(p);
    }

    (string Url, string Dest) LAiSource(string id)
    {
        var c = LAiCatalog.FirstOrDefault(x => x.Id == id);
        if (c != null) return ($"https://huggingface.co/{c.Repo}/resolve/main/{c.File}", Path.Combine(LAiModelsDir, c.File));
        var u = lai.Custom.FirstOrDefault(x => x.Id == id);
        if (u != null && u.Url.Length > 0) return (u.Url, Path.Combine(LAiModelsDir, u.File));
        return ("", "");
    }

    async Task LAiPushStateAsync()
    {
        long total = 0, got = 0;
        await LChatPushAsync("ai", new
        {
            items = LAiItems(),
            active = lai.Active,
            running = laiRunning,
            ramGb = LAiRamGb(),
            vulkan = lai.Vulkan,
            engineReady = LAiFindEngine(lai.Vulkan) != null,
            phase = laiPhase,
            text = laiText,
            pct = laiPct,
            dl = laiDlId,
            got,
            total,
            online = NetworkUp()
        });
    }

    void LAiPhase(string phase, string text, int pct = 0)
    {
        laiPhase = phase; laiText = text; laiPct = pct;
        _ = LChatPushAsync("engine", new { phase, text, pct });
    }

    // Baixa o modelo e já liga a IA (usado pelo botão "Baixar a IA do Azul Groove" do chat)
    async Task LAiDownloadAndUseAsync(string id)
    {
        await LAiDownloadModelAsync(id, thenUse: true);
        if (LAiHave(id) && laiDlCts == null) await LAiUseAsync(id);
    }

    async Task LAiDownloadModelAsync(string id, bool thenUse = false)
    {
        if (laiDlCts != null) { await LChatPushAsync("aiMsg", "Já existe um download em andamento."); return; }
        var (url, dest) = LAiSource(id);
        if (url.Length == 0) return;
        if (!NetworkUp()) { await LChatPushAsync("aiMsg", "Sem internet para baixar agora."); return; }

        var cts = laiDlCts = new CancellationTokenSource();
        laiDlId = id;
        await LAiPushStateAsync();
        try
        {
            await LAiDownloadFileAsync(url, dest, (g, t) => _ = LChatPushAsync("aiDl", new { id, got = g, total = t }), cts.Token);
            if (!thenUse) await LChatPushAsync("aiMsg", "Modelo baixado! Clique em \"Usar este\".");
        }
        catch (OperationCanceledException) { await LChatPushAsync("aiMsg", "Download cancelado. O que já foi baixado fica guardado para continuar depois."); }
        catch (Exception ex) { await LChatPushAsync("aiMsg", "Falha no download: " + ex.Message); }
        finally
        {
            laiDlCts = null; laiDlId = "";
            cts.Dispose();
            await LAiPushStateAsync();
        }
    }

    async Task LAiUseAsync(string id)
    {
        if (!LAiHave(id)) return;
        if (lchatBusy) { await LChatPushAsync("aiMsg", "Espere a resposta terminar para trocar de IA."); return; }
        lai.Active = id; lai.Save();
        var d = lchatData;
        d.Provider = "azul"; d.Base = LAiBase; d.Key = ""; d.Model = id; d.Save();
        await LChatPushStateAsync();
        await LAiPushStateAsync();
        await LAiStartAsync(id);
        await LChatRefreshAsync(false);
        await LAiPushStateAsync();
    }

    async Task LAiDeleteAsync(string id)
    {
        if (laiDlId == id) return;
        if (lchatBusy) { await LChatPushAsync("aiMsg", "Espere a resposta terminar."); return; }
        var cust = lai.Custom.FirstOrDefault(x => x.Id == id);
        var path = LAiModelPath(id);
        if (laiRunning == id) LAiStop();
        try
        {
            // arquivo importado do PC do usuário nunca é apagado; só sai da lista
            if (path.Length > 0 && (cust == null || cust.Path.Length == 0) && File.Exists(path)) File.Delete(path);
            try { if (File.Exists(path + ".part")) File.Delete(path + ".part"); } catch { }
        }
        catch (Exception ex) { await LChatPushAsync("aiMsg", "Não consegui apagar: " + ex.Message); }
        if (cust != null) lai.Custom.Remove(cust);
        if (lai.Active == id)
        {
            lai.Active = "";
            var d = lchatData;
            if (d.Provider == "azul") { d.Provider = ""; d.Base = ""; d.Model = ""; d.Save(); await LChatPushStateAsync(); }
        }
        lai.Save();
        await LAiPushStateAsync();
        await LChatRefreshAsync(false);
    }

    async Task LAiAddUrlAsync(string url)
    {
        url = url.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps)
        { await LChatPushAsync("aiMsg", "Use um link que comece com https://"); return; }
        var file = Path.GetFileName(u.AbsolutePath);
        if (!file.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
        { await LChatPushAsync("aiMsg", "O link precisa ser de um arquivo .gguf"); return; }
        foreach (var ch in Path.GetInvalidFileNameChars()) file = file.Replace(ch, '_');
        // link da página do Hugging Face (/blob/) -> link de download (/resolve/)
        var dl = url.Replace("/blob/", "/resolve/");
        var ex = lai.Custom.FirstOrDefault(x => x.Url == dl);
        if (ex == null)
        {
            ex = new LAiCustom { Id = "c-" + Guid.NewGuid().ToString("N")[..6], Name = file[..^5], File = file, Url = dl };
            lai.Custom.Add(ex); lai.Save();
        }
        await LAiPushStateAsync();
        await LAiDownloadModelAsync(ex.Id);
    }

    async Task LAiImportAsync()
    {
        using var dlg = new OpenFileDialog { Filter = "Modelo GGUF (*.gguf)|*.gguf", Title = "Escolha um modelo .gguf" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var f = dlg.FileName;
        if (lai.Custom.Any(x => x.Path == f)) return;
        lai.Custom.Add(new LAiCustom { Id = "c-" + Guid.NewGuid().ToString("N")[..6], Name = Path.GetFileNameWithoutExtension(f), Path = f });
        lai.Save();
        await LAiPushStateAsync();
    }

    async Task LAiSetVulkanAsync(bool on)
    {
        lai.Vulkan = on; lai.Save();
        if (laiRunning.Length > 0 && !lchatBusy)
        {
            LAiStop();
            await LAiPushStateAsync();
            if (lchatData.Provider == "azul") { await LAiStartAsync(lai.Active); await LChatRefreshAsync(false); }
        }
        await LAiPushStateAsync();
    }

    // ================= motor (llama-server) =================
    string? LAiFindEngine(bool vulkan)
    {
        var kind = vulkan ? "vulkan" : "cpu";
        foreach (var root in new[] { Path.Combine(AppContext.BaseDirectory, "engine", kind), Path.Combine(LAiEngineRoot, kind) })
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                var f = Directory.GetFiles(root, "llama-server.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (f != null) return f;
            }
            catch { }
        }
        return null;
    }

    async Task<string?> LAiEnsureEngineAsync()
    {
        var vk = lai.Vulkan;
        var exe = LAiFindEngine(vk);
        if (exe != null) return exe;
        if (!NetworkUp())
        {
            LAiPhase("error", "O motor da IA ainda não está neste PC e não há internet para baixar. Conecte-se uma vez para baixar.");
            return null;
        }

        LAiPhase("download", "Baixando o motor da IA (só na primeira vez)…");
        string? url = null;
        // Não usa só "releases/latest": o GitHub às vezes publica a versão mais nova ANTES de terminar de subir os
        // arquivos (zips do Windows), e aí o "latest" vem sem motor. Então olha as últimas versões e pega a
        // primeira que já tenha o motor certo para Windows.
        using (var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/ggml-org/llama.cpp/releases?per_page=30"))
        {
            req.Headers.TryAddWithoutValidation("User-Agent", "AzulGroove");
            req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            using var resp = await AiHttp.SendAsync(req);
            if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden || (int)resp.StatusCode == 429)
                throw new InvalidOperationException("o GitHub limitou os acessos por agora, tente de novo em alguns minutos");
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var kindName = vk ? "vulkan" : "cpu";
            foreach (var rel in doc.RootElement.EnumerateArray())
            {
                if (rel.TryGetProperty("draft", out var dr) && dr.GetBoolean()) continue;
                if (!rel.TryGetProperty("assets", out var assets)) continue;
                foreach (var a in assets.EnumerateArray())
                {
                    var n = RpcMapper.Str(a, "name");
                    if (n.StartsWith("llama-", StringComparison.OrdinalIgnoreCase)
                        && n.EndsWith("-x64.zip", StringComparison.OrdinalIgnoreCase)
                        && n.Contains("-bin-win-" + kindName + "-", StringComparison.OrdinalIgnoreCase))
                    { url = RpcMapper.Str(a, "browser_download_url"); break; }
                }
                if (!string.IsNullOrEmpty(url)) break;
            }
        }
        if (string.IsNullOrEmpty(url)) throw new InvalidOperationException("não achei o motor para Windows na versão mais nova");

        var kind = vk ? "vulkan" : "cpu";
        var dir = Path.Combine(LAiEngineRoot, kind);
        var zip = Path.Combine(LAiEngineRoot, kind + ".zip");
        await LAiDownloadFileAsync(url, zip, (g, t) =>
            LAiPhase("download", "Baixando o motor da IA (só na primeira vez)…", t > 0 ? (int)(g * 100 / t) : 0), CancellationToken.None);

        LAiPhase("extract", "Instalando o motor…", 100);
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        ZipFile.ExtractToDirectory(zip, dir, true);
        try { File.Delete(zip); } catch { }
        return LAiFindEngine(vk);
    }

    async Task<bool> LAiStartAsync(string modelId)
    {
        await laiGate.WaitAsync();
        try
        {
            var path = LAiModelPath(modelId);
            if (path.Length == 0 || !File.Exists(path)) { LAiPhase("error", "Esse modelo ainda não foi baixado."); return false; }
            if (laiProc is { HasExited: false } && laiRunning == modelId && laiRunningVk == lai.Vulkan && await LAiHealthAsync())
            { LAiPhase("ready", ""); return true; }

            LAiStop();
            string? exe;
            try { exe = await LAiEnsureEngineAsync(); }
            catch (Exception ex) { LAiPhase("error", "Não consegui baixar o motor da IA: " + ex.Message); return false; }
            if (exe == null) return false;

            LAiKillOrphans();
            LAiPhase("load", "Carregando a IA na memória…");

            var psi = new ProcessStartInfo(exe)
            {
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in new[] { "-m", path, "--host", "127.0.0.1", "--port", LAiPort.ToString(), "-c", "4096", "-np", "1", "--alias", modelId })
                psi.ArgumentList.Add(a);
            if (lai.Vulkan) { psi.ArgumentList.Add("-ngl"); psi.ArgumentList.Add("99"); }

            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            DataReceivedEventHandler h = (_, e) =>
            {
                if (e.Data == null) return;
                lock (laiLog) { laiLog.Enqueue(e.Data); while (laiLog.Count > 30) laiLog.Dequeue(); }
            };
            p.OutputDataReceived += h;
            p.ErrorDataReceived += h;
            p.Exited += (_, _) => { if (ReferenceEquals(laiProc, p)) laiRunning = ""; };
            lock (laiLog) laiLog.Clear();

            try { p.Start(); }
            catch (Exception ex) { LAiPhase("error", "Não consegui iniciar o motor da IA: " + ex.Message); return false; }
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            laiProc = p; laiRunning = modelId; laiRunningVk = lai.Vulkan;

            var t0 = DateTime.UtcNow;
            while ((DateTime.UtcNow - t0).TotalSeconds < 240)
            {
                if (p.HasExited)
                {
                    LAiPhase("error", "A IA fechou ao iniciar. " + LAiLastLog());
                    laiProc = null; laiRunning = "";
                    return false;
                }
                if (await LAiHealthAsync()) { LAiPhase("ready", ""); return true; }
                await Task.Delay(500);
            }
            LAiStop();
            LAiPhase("error", "A IA demorou demais para carregar. Tente um modelo menor.");
            return false;
        }
        finally { laiGate.Release(); }
    }

    static async Task<bool> LAiHealthAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(1500);
            using var r = await AiHttp.GetAsync($"http://127.0.0.1:{LAiPort}/health", cts.Token);
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    string LAiLastLog()
    {
        string[] lines;
        lock (laiLog) lines = laiLog.ToArray();
        var tail = lines.Where(l => l.Trim().Length > 0).TakeLast(2).Select(l => l.Trim());
        var s = string.Join(" ", tail);
        return s.Length > 220 ? s[^220..] : s;
    }

    void LAiStop()
    {
        var p = laiProc;
        laiProc = null; laiRunning = "";
        if (p == null) return;
        try { if (!p.HasExited) { p.Kill(true); p.WaitForExit(2000); } } catch { }
        try { p.Dispose(); } catch { }
    }

    // Se o app caiu antes, o llama-server pode ter ficado aberto ocupando memória: fecha só os nossos
    void LAiKillOrphans()
    {
        try
        {
            var mine = new[] { LAiEngineRoot, Path.Combine(AppContext.BaseDirectory, "engine") };
            foreach (var pr in Process.GetProcessesByName("llama-server"))
            {
                try
                {
                    if (laiProc != null && pr.Id == laiProc.Id) continue;
                    var f = pr.MainModule?.FileName ?? "";
                    if (mine.Any(m => f.StartsWith(m, StringComparison.OrdinalIgnoreCase))) pr.Kill(true);
                }
                catch { }
            }
        }
        catch { }
    }

    // ================= download com retomada =================
    static async Task LAiDownloadFileAsync(string url, string dest, Action<long, long> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var part = dest + ".part";
        long have = File.Exists(part) ? new FileInfo(part).Length : 0;

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", "AzulGroove");
        if (have > 0) req.Headers.Range = new RangeHeaderValue(have, null);
        using var resp = await AiHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

        if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // o .part já estava completo
            File.Move(part, dest, true);
            return;
        }
        resp.EnsureSuccessStatusCode();

        var resume = resp.StatusCode == HttpStatusCode.PartialContent;
        if (!resume) have = 0;
        var total = (resp.Content.Headers.ContentLength ?? 0) + (resume ? have : 0);
        long got = have;

        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var fs = new FileStream(part, resume ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true))
        {
            var buf = new byte[1 << 16];
            var sw = Stopwatch.StartNew();
            long last = 0;
            int n;
            while ((n = await src.ReadAsync(buf.AsMemory(0, buf.Length), ct)) > 0)
            {
                await fs.WriteAsync(buf.AsMemory(0, n), ct);
                got += n;
                if (sw.ElapsedMilliseconds - last > 300) { last = sw.ElapsedMilliseconds; progress(got, total); }
            }
            progress(got, total);
        }

        if (total > 0 && got != total) throw new IOException("o download veio incompleto; tente de novo");
        File.Move(part, dest, true);
    }
}

sealed record LAiCat(string Id, string Name, string Desc, string Repo, string File, double Gb, int Ram, string Lic);
sealed record LAiItem(string Id, string Name, string Desc, double Gb, int Ram, string Lic, bool Have, bool Custom, bool Rec);

sealed class LAiCustom
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string File { get; set; } = "";   // nome do arquivo dentro de \models (modelo baixado por link)
    public string Url { get; set; } = "";
    public string Path { get; set; } = "";   // caminho de um .gguf importado do PC
}

sealed class LAiSettings
{
    public string Active { get; set; } = "";
    public bool Vulkan { get; set; }
    public bool StopOnMinimize { get; set; }   // fecha a IA ao minimizar e liga de novo ao abrir
    public List<LAiCustom> Custom { get; set; } = new();

    static string FilePath => System.IO.Path.Combine(MainForm.DataDir, "localai.json");

    public static LAiSettings Load()
    {
        try
        {
            var s = JsonSerializer.Deserialize<LAiSettings>(System.IO.File.ReadAllText(FilePath)) ?? new LAiSettings();
            s.Active ??= ""; s.Custom ??= new();
            return s;
        }
        catch { return new LAiSettings(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(MainForm.DataDir);
            System.IO.File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch { }
    }
}
