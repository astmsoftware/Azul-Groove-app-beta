namespace AzulGroove;

static class Program
{
    [STAThread]
    static void Main()
    {
        // Só uma janela por vez. Abrir o .exe de novo traz o app que já está aberto (ou na bandeja) para a frente.
        const string showEvent = "AzulGroove.ShowEvent";
        using var mutex = new Mutex(true, "AzulGroove.SingleInstance", out bool first);
        if (!first)
        {
            try { using var ev = EventWaitHandle.OpenExisting(showEvent); ev.Set(); } catch { }
            return;
        }

        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, showEvent);

        ApplicationConfiguration.Initialize();
        var form = new MainForm();

        var watcher = new Thread(() =>
        {
            while (show.WaitOne())
            {
                try { if (form.IsHandleCreated) form.BeginInvoke(form.ShowChat); } catch { }
            }
        }) { IsBackground = true };
        watcher.Start();

        Application.Run(form);
    }
}
