namespace AzulGroove;

/// <summary>Economia de recursos: com o app escondido na bandeja o WebView2 é suspenso (CPU e memória livres).</summary>
public partial class MainForm
{
    async Task SuspendWebAsync()
    {
        try
        {
            await Task.Delay(1500); // espera a janela sumir de vez
            var cw = web.CoreWebView2;
            if (cw == null || IsDisposed || Visible || cw.IsSuspended) return;
            if (cw.IsDocumentPlayingAudio) return; // tocando música: nunca suspende
            await cw.TrySuspendAsync();
        }
        catch { /* se não der para suspender, tudo continua funcionando normalmente */ }
    }

    void ResumeWeb()
    {
        try
        {
            var cw = web.CoreWebView2;
            if (cw != null && cw.IsSuspended) cw.Resume();
        }
        catch { }
    }
}
