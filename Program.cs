namespace AzulGroove;

static class Program
{
    [STAThread]
    static void Main()
    {
        // Só uma janela por vez. Abrir o .exe de novo traz o app que já está aberto (ou na bandeja) para a frente.
        const string showEvent = "AzulGrooveBeta.ShowEvent";
        const string uninstallEvent = "AzulGrooveBeta.UninstallEvent";
        using var mutex = new Mutex(true, "AzulGrooveBeta.SingleInstance", out bool first);
        if (!first)
        {
            try { using var ev = EventWaitHandle.OpenExisting(showEvent); ev.Set(); } catch { }
            return;
        }

        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, showEvent);
        using var uninstall = new EventWaitHandle(false, EventResetMode.AutoReset, uninstallEvent);

        ApplicationConfiguration.Initialize();
        var form = new MainForm();

        var watcher = new Thread(() =>
        {
            WaitHandle[] events = { show, uninstall };
            while (true)
            {
                if (WaitHandle.WaitAny(events) == 1)
                {
                    try { if (form.IsHandleCreated) form.BeginInvoke(form.ExitForUninstall); } catch { }
                    return;
                }

                try { if (form.IsHandleCreated) form.BeginInvoke(form.ShowChat); } catch { }
            }
        }) { IsBackground = true };
        watcher.Start();

        Application.Run(form);
    }
}
