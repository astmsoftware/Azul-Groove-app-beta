using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace AzulGrooveUninstall;

static class Program
{
    [STAThread]
    static async Task Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        try
        {
            string? installDir = GetArgument(args, "--install-dir");
            bool runCopy = args.Contains("--run-uninstall", StringComparer.OrdinalIgnoreCase);
            bool quiet = args.Contains("--quiet", StringComparer.OrdinalIgnoreCase);
            bool removeData = args.Contains("--remove-data", StringComparer.OrdinalIgnoreCase);

            if (!runCopy)
            {
                LaunchTemporaryCopy(installDir ?? AppContext.BaseDirectory, quiet, removeData);
                return;
            }

            int? parentPid = int.TryParse(GetArgument(args, "--parent-pid"), out int pid) ? pid : null;
            if (!WaitForLauncher(parentPid))
            {
                if (quiet)
                {
                    Environment.ExitCode = 1;
                }
                else
                {
                    MessageBox.Show(Texts.IsPortuguese
                            ? "Não foi possível preparar a desinstalação. Feche o assistente e tente novamente."
                            : "The uninstaller could not be prepared. Close the assistant and try again.",
                        "Azul Groove Beta", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                return;
            }

            if (quiet)
            {
                await UninstallWizard.RunQuietUninstallAsync(installDir ?? AppContext.BaseDirectory, removeData);
                return;
            }

            Application.Run(new UninstallWizard(installDir ?? AppContext.BaseDirectory, removeData));
        }
        catch (Exception ex)
        {
            if (args.Contains("--quiet", StringComparer.OrdinalIgnoreCase))
            {
                Environment.ExitCode = 1;
            }
            else
            {
                MessageBox.Show(Texts.IsPortuguese
                        ? $"Não foi possível abrir o assistente de desinstalação.\n\n{ex.Message}"
                        : $"The uninstall assistant could not be opened.\n\n{ex.Message}",
                    "Azul Groove Beta", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    static string? GetArgument(string[] args, string name)
    {
        for (int i = 0; i + 1 < args.Length; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    static void LaunchTemporaryCopy(string installDir, bool quiet, bool removeData)
    {
        string source = Environment.ProcessPath ?? Application.ExecutablePath;
        string sourceDir = Path.GetFullPath(installDir);
        string tempDir = Path.Combine(Path.GetTempPath(), "AzulGrooveBetaUninstall-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string copy = Path.Combine(tempDir, Path.GetFileName(source));
        File.Copy(source, copy);

        var start = new ProcessStartInfo(copy)
        {
            UseShellExecute = false,
            WorkingDirectory = tempDir
        };
        start.ArgumentList.Add("--run-uninstall");
        if (quiet)
            start.ArgumentList.Add("--quiet");
        if (removeData)
            start.ArgumentList.Add("--remove-data");
        start.ArgumentList.Add("--install-dir");
        start.ArgumentList.Add(sourceDir);
        start.ArgumentList.Add("--parent-pid");
        start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

        if (Process.Start(start) is null)
            throw new InvalidOperationException(Texts.IsPortuguese
                ? "O Windows não iniciou o assistente de desinstalação."
                : "Windows did not start the uninstall assistant.");
    }

    static bool WaitForLauncher(int? processId)
    {
        if (processId is null)
            return true;

        try
        {
            using Process parent = Process.GetProcessById(processId.Value);
            return parent.WaitForExit(15000);
        }
        catch (ArgumentException)
        {
            return true;
        }
    }
}

static class Texts
{
    public static bool IsPortuguese => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "pt";
    public static string AppName => "Azul Groove Beta";
    public static string Title => IsPortuguese ? "Assistente de desinstalação" : "Uninstall assistant";
    public static string Remove => IsPortuguese ? "Desinstalar" : "Uninstall";
    public static string Cancel => IsPortuguese ? "Cancelar" : "Cancel";
    public static string Close => IsPortuguese ? "Fechar" : "Close";
    public static string Finish => IsPortuguese ? "Concluir" : "Finish";
    public static string Retry => IsPortuguese ? "Tentar novamente" : "Try again";
    public static string Pick(string pt, string en) => IsPortuguese ? pt : en;
    public static string RemoveDataCaption => IsPortuguese ? "Remover também meus dados e configurações" : "Also remove my data and settings";
    public static string RemoveDataHint => IsPortuguese
        ? "Apaga login, preferências e arquivos do usuário. Não pode ser desfeito."
        : "Deletes login, preferences and user files. This can't be undone.";
    public static string ConfirmDataTitle => IsPortuguese ? "Apagar também seus dados?" : "Also delete your data?";
}

sealed class UninstallWizard : Form
{
    static readonly Color Background = Color.FromArgb(12, 16, 27);
    internal static readonly Color Card = Color.FromArgb(20, 27, 43);
    static readonly Color Border = Color.FromArgb(38, 49, 72);
    static readonly Color FooterBackground = Color.FromArgb(9, 13, 23);
    internal static readonly Color Accent = Color.FromArgb(79, 125, 243);
    internal static readonly Color AccentEnd = Color.FromArgb(126, 87, 255);
    internal static readonly Color Muted = Color.FromArgb(157, 171, 194);

    const int TickMs = 33; // ~30 fps (antes: 80 ms, ~12 fps, animação "travada")

    readonly string installDir;
    readonly Label title;
    readonly Label message;
    readonly Label detail;
    readonly RoundedPanel card;
    readonly ProgressStrip progress;
    readonly RoundedButton primary;
    readonly RoundedButton secondary;
    readonly StatusMark statusMark;
    readonly HeaderPanel header;
    readonly Panel footer;
    readonly RoundedCheck removeCheck;
    readonly Panel feedbackArea;
    readonly ChipButton[] chips;
    readonly RoundedPanel feedbackBox;
    readonly TextBox feedbackText;
    readonly string? appVersion;
    readonly StarRating rating;
    readonly Label feedbackCounter;
    bool feedbackSent;
    // Animação de entrada da tela de feedback (linhas deslizam para cima, uma após a outra).
    const int FeedbackIntroMs = 420;
    const int FeedbackStaggerMs = 90;
    const int FeedbackSlidePx = 20;
    readonly Stopwatch feedbackIntroClock = new();
    readonly Dictionary<Control, (int Top, int Row)> feedbackLayout = new();
    bool feedbackIntroActive;
    // Tempo mínimo da tela "Desinstalando..." para o usuário conseguir acompanhar o progresso.
    const int MinRunningMs = 4000;
    readonly System.Windows.Forms.Timer animation;
    readonly Stopwatch clock = Stopwatch.StartNew();
    WizardPage page = WizardPage.Welcome;
    bool running;

    public UninstallWizard(string installDir, bool removeUserData = false)
    {
        this.installDir = Path.GetFullPath(installDir);
        appVersion = ReadAppVersion(this.installDir);
        Text = Texts.Title;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        ClientSize = new Size(560, 444);
        DoubleBuffered = true;
        BackColor = Background;
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10f);
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? Application.ExecutablePath);

        // Cabeçalho: gradiente, logo e barras de equalizador desenhados num único controle
        // (sem cantos quadrados escuros em volta do logo, sem flicker).
        var logo = Icon?.ToBitmap();
        header = new HeaderPanel(logo) { Dock = DockStyle.Top, Height = 92, BackColor = Card };
        header.Controls.Add(new Label
        {
            Text = Texts.AppName,
            Left = 88,
            Top = 20,
            Width = 300,
            Height = 32,
            ForeColor = Color.White,
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI Semibold", 19f, FontStyle.Bold)
        });
        header.Controls.Add(new Label
        {
            Text = Texts.Title,
            Left = 90,
            Top = 52,
            Width = 300,
            Height = 22,
            ForeColor = Color.FromArgb(213, 222, 245),
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI", 9.5f)
        });
        Controls.Add(header);

        // Rodapé com os botões (faixa mais escura + linha divisória), como nos instaladores modernos.
        footer = new Panel { Dock = DockStyle.Bottom, Height = 76, BackColor = FooterBackground };
        footer.Paint += (_, e) =>
        {
            using var line = new Pen(Border);
            e.Graphics.DrawLine(line, 0, 0, footer.Width, 0);
        };
        Controls.Add(footer);

        title = new Label
        {
            Left = 28,
            Top = 110,
            Width = 504,
            Height = 32,
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 14f, FontStyle.Bold)
        };
        Controls.Add(title);

        message = new Label
        {
            Left = 28,
            Top = 146,
            Width = 504,
            Height = 52,
            ForeColor = Color.FromArgb(214, 222, 240),
            Font = new Font("Segoe UI", 10f)
        };
        Controls.Add(message);

        card = new RoundedPanel
        {
            Left = 28,
            Top = 206,
            Width = 504,
            Height = 76,
            BackColor = Card,
            BorderColor = Border
        };
        Controls.Add(card);

        // Ícone e texto centralizados na vertical (antes o texto ficava colado no topo do cartão).
        statusMark = new StatusMark
        {
            Left = 16,
            Top = 16,
            Width = 36,
            Height = 36
        };
        card.Controls.Add(statusMark);

        detail = new Label
        {
            Left = 64,
            Top = 8,
            Width = 424,
            Height = 52,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(214, 222, 240),
            Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold)
        };
        card.Controls.Add(detail);

        progress = new ProgressStrip
        {
            Left = 16,
            Top = 62,
            Width = 472,
            Height = 6,
            Visible = false
        };
        card.Controls.Add(progress);

        removeCheck = new RoundedCheck
        {
            Left = 28,
            Top = 294,
            Width = 504,
            Height = 42,
            Caption = Texts.RemoveDataCaption,
            Hint = Texts.RemoveDataHint,
            Checked = removeUserData
        };
        removeCheck.CheckedChanged += (_, _) =>
        {
            if (page == WizardPage.Welcome)
                ApplyWelcomeText();
        };
        Controls.Add(removeCheck);

        // Área de feedback opcional: nota (estrelas), motivos (múltipla escolha) e mensagem livre.
        feedbackArea = new Panel { Left = 28, Top = 198, Width = 504, Height = 158, BackColor = Background, Visible = false };

        rating = new StarRating
        {
            Left = 0,
            Top = 0,
            Width = 504,
            Height = 28,
            Prompt = Texts.Pick("Como foi sua experiência?", "How was your experience?"),
            Labels = Texts.IsPortuguese
                ? new[] { "Péssimo", "Ruim", "Regular", "Bom", "Ótimo" }
                : new[] { "Terrible", "Poor", "Okay", "Good", "Great" }
        };
        rating.ValueChanged += (_, _) =>
        {
            if (page == WizardPage.Feedback)
                primary.Enabled = HasFeedbackContent();
        };
        feedbackArea.Controls.Add(rating);

        string[] reasons = Texts.IsPortuguese
            ? new[] { "Não uso mais", "Erros / bugs", "Faltam recursos", "Outro motivo" }
            : new[] { "No longer needed", "Bugs / errors", "Missing features", "Other" };
        chips = new ChipButton[reasons.Length];
        for (int i = 0; i < chips.Length; i++)
        {
            var chip = new ChipButton { Text = reasons[i], Left = i * 128, Top = 34, Width = 120, Height = 30 };
            chip.Click += (_, _) => SelectChip(chip);
            chips[i] = chip;
            feedbackArea.Controls.Add(chip);
        }

        feedbackBox = new RoundedPanel { Left = 0, Top = 70, Width = 504, Height = 66, BackColor = Card, BorderColor = Border };
        feedbackText = new TextBox
        {
            Left = 14,
            Top = 8,
            Width = 476,
            Height = 50,
            Multiline = true,
            AcceptsReturn = true,
            BorderStyle = BorderStyle.None,
            BackColor = Card,
            ForeColor = Color.FromArgb(230, 236, 248),
            Font = new Font("Segoe UI", 10f),
            MaxLength = 800,
            ScrollBars = ScrollBars.None, // sem a barra de rolagem feia; o texto ainda rola com o cursor e o mouse
            PlaceholderText = Texts.Pick("Conte mais, se quiser (opcional)...", "Tell us more if you like (optional)...")
        };
        feedbackText.Enter += (_, _) => feedbackBox.BorderColor = Accent;
        feedbackText.Leave += (_, _) => feedbackBox.BorderColor = Border;
        feedbackBox.Controls.Add(feedbackText);
        feedbackArea.Controls.Add(feedbackBox);

        feedbackCounter = new Label
        {
            Left = 384,
            Top = 140,
            Width = 120,
            Height = 16,
            Text = "0/800",
            TextAlign = ContentAlignment.MiddleRight,
            ForeColor = Muted,
            Font = new Font("Segoe UI", 8.5f)
        };
        feedbackText.TextChanged += (_, _) =>
        {
            feedbackCounter.Text = $"{feedbackText.TextLength}/{feedbackText.MaxLength}";
            if (page == WizardPage.Feedback)
                primary.Enabled = HasFeedbackContent();
        };
        feedbackArea.Controls.Add(feedbackCounter);
        feedbackArea.Controls.Add(new Label
        {
            Left = 0,
            Top = 140,
            Width = 380,
            Height = 16,
            ForeColor = Muted,
            Font = new Font("Segoe UI", 8.5f),
            Text = Texts.Pick(
                "Enviamos só sua resposta e as versões do app e do Windows.",
                "We only send your answers and the app and Windows versions.")
        });
        Controls.Add(feedbackArea);

        primary = new RoundedButton { Text = Texts.Remove, IsPrimary = true };
        primary.Width = 146;
        primary.Left = 386;
        primary.Top = 18;
        primary.Click += async (_, _) =>
        {
            switch (page)
            {
                case WizardPage.Welcome:
                    // Confirmação dentro do próprio assistente (antes era uma caixa branca do Windows).
                    if (removeCheck.Checked)
                        ShowPage(WizardPage.ConfirmData);
                    else
                        await StartUninstallAsync();
                    break;
                case WizardPage.ConfirmData:
                case WizardPage.Error:
                    await StartUninstallAsync();
                    break;
                case WizardPage.Feedback:
                    await SendFeedbackAsync();
                    break;
                case WizardPage.Success:
                case WizardPage.Thanks:
                    Close();
                    break;
            }
        };
        footer.Controls.Add(primary);
        AcceptButton = primary;

        secondary = new RoundedButton { Text = Texts.Cancel };
        secondary.Width = 132;
        secondary.Left = 242;
        secondary.Top = 18;
        secondary.Click += (_, _) =>
        {
            if (running)
                return;
            switch (page)
            {
                case WizardPage.ConfirmData:
                    ShowPage(WizardPage.Welcome);
                    break;
                case WizardPage.Feedback: // Pular
                    feedbackSent = false;
                    ShowPage(WizardPage.Thanks);
                    break;
                default: // Cancelar, Fechar
                    Close();
                    break;
            }
        };
        footer.Controls.Add(secondary);
        CancelButton = secondary;

        ShowPage(WizardPage.Welcome);
        animation = new System.Windows.Forms.Timer { Interval = TickMs };
        animation.Tick += (_, _) =>
        {
            header.Time = clock.Elapsed.TotalSeconds;
            header.Invalidate(header.BarsBounds);
            if (progress.Visible)
                progress.Invalidate();
            statusMark.AdvanceAnimation();
            if (feedbackIntroActive)
                AdvanceFeedbackIntro();
        };
        animation.Start();
        FormClosed += (_, _) =>
        {
            animation.Dispose();
            logo?.Dispose();
        };
        Shown += (_, _) => ApplyDarkTitleBar();
    }

    void ApplyDarkTitleBar()
    {
        try
        {
            int dark = 1;
            if (DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int)) != 0)
                DwmSetWindowAttribute(Handle, 19, ref dark, sizeof(int));
            int caption = ToColorRef(Background);
            int textColor = ToColorRef(Color.FromArgb(235, 238, 247));
            DwmSetWindowAttribute(Handle, 35, ref caption, sizeof(int));
            DwmSetWindowAttribute(Handle, 36, ref textColor, sizeof(int));
            DwmSetWindowAttribute(Handle, 34, ref caption, sizeof(int));
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        catch (ExternalException) { }
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (animation is not null)
        {
            header.Invalidate();
            progress.Invalidate();
        }
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    static int ToColorRef(Color color) => color.R | (color.G << 8) | (color.B << 16);

    void ApplyWelcomeText()
    {
        bool all = removeCheck.Checked;
        message.Text = Texts.IsPortuguese
            ? (all
                ? "Os arquivos do aplicativo, os atalhos e também seu login, suas configurações e seus dados pessoais serão removidos."
                : "Os arquivos do aplicativo e os atalhos serão removidos. Seu login, suas configurações e seus dados pessoais serão preservados.")
            : (all
                ? "The application, its shortcuts and also your login, settings and personal data will be removed."
                : "The application and its shortcuts will be removed. Settings, login, and data in your user folder will be kept.");
        detail.Text = Texts.IsPortuguese
            ? (all ? "Atenção: seus dados serão apagados e não poderão ser recuperados." : "Seus dados pessoais permanecem salvos neste computador.")
            : (all ? "Warning: your data will be deleted and cannot be recovered." : "Your personal data will remain safely on this computer.");
        detail.ForeColor = all ? Color.FromArgb(255, 196, 120) : Color.FromArgb(214, 222, 240);
    }

    static string? ReadAppVersion(string directory)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(Path.Combine(directory, "AzulGroove.exe"));
            return info.ProductVersion ?? info.FileVersion;
        }
        catch (Exception)
        {
            return null;
        }
    }

    void SelectChip(ChipButton clicked)
    {
        clicked.Selected = !clicked.Selected; // vários motivos podem ser marcados
        if (page == WizardPage.Feedback)
            primary.Enabled = HasFeedbackContent();
    }

    string[] SelectedReasons() => chips.Where(c => c.Selected).Select(c => c.Text).ToArray();

    bool HasFeedbackContent() =>
        rating.Value > 0 || chips.Any(c => c.Selected) || feedbackText.Text.Trim().Length > 0;

    async Task SendFeedbackAsync()
    {
        running = true;
        primary.Enabled = secondary.Enabled = false;
        primary.Text = Texts.Pick("Enviando...", "Sending...");
        try
        {
            await FeedbackSender.SendAsync(SelectedReasons(), rating.Value, feedbackText.Text.Trim(), appVersion, removeCheck.Checked);
            running = false;
            feedbackSent = true;
            ShowPage(WizardPage.Thanks);
        }
        catch (Exception)
        {
            running = false;
            ShowPage(WizardPage.Feedback, Texts.Pick(
                "Não foi possível enviar agora. Verifique sua conexão e tente novamente, ou toque em Pular.",
                "Could not send right now. Check your connection and try again, or tap Skip."));
        }
    }

    static double EaseOutCubic(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return 1 - Math.Pow(1 - t, 3);
    }

    void StartFeedbackIntro()
    {
        if (feedbackIntroActive)
            FinishFeedbackIntro();

        // Agrupa por linha (as 4 opções ficam juntas) e guarda a posição final de cada controle.
        feedbackLayout.Clear();
        var tops = feedbackArea.Controls.Cast<Control>().Select(c => c.Top).Distinct().OrderBy(t => t).ToList();
        foreach (Control c in feedbackArea.Controls)
            feedbackLayout[c] = (c.Top, tops.IndexOf(c.Top));

        int slide = (int)Math.Round(FeedbackSlidePx * DeviceDpi / 96.0);
        foreach (var (c, info) in feedbackLayout)
        {
            c.Top = info.Top + slide;
            c.Visible = false;
        }
        feedbackIntroClock.Restart();
        feedbackIntroActive = true;
    }

    void AdvanceFeedbackIntro()
    {
        int slide = (int)Math.Round(FeedbackSlidePx * DeviceDpi / 96.0);
        double elapsed = feedbackIntroClock.Elapsed.TotalMilliseconds;
        bool done = true;

        foreach (var (c, info) in feedbackLayout)
        {
            double t = (elapsed - info.Row * FeedbackStaggerMs) / FeedbackIntroMs;
            if (t < 1)
                done = false;
            if (t <= 0)
                continue;

            int top = info.Top + (int)Math.Round((1 - EaseOutCubic(t)) * slide);
            if (!c.Visible)
                c.Visible = true;
            if (c.Top != top)
                c.Top = top;
        }

        if (done)
            FinishFeedbackIntro();
    }

    void FinishFeedbackIntro()
    {
        foreach (var (c, info) in feedbackLayout)
        {
            c.Top = info.Top;
            c.Visible = true;
        }
        feedbackIntroActive = false;
    }

    void ShowPage(WizardPage next, string? error = null)
    {
        if (feedbackIntroActive)
            FinishFeedbackIntro();
        page = next;
        removeCheck.Visible = next == WizardPage.Welcome;
        card.Visible = next != WizardPage.Feedback;
        feedbackArea.Visible = next == WizardPage.Feedback;
        progress.Visible = next == WizardPage.Running;
        message.ForeColor = Color.FromArgb(214, 222, 240);
        detail.ForeColor = Color.FromArgb(214, 222, 240);
        CancelButton = secondary; // Esc = Pular/Cancelar
        statusMark.State = next switch
        {
            WizardPage.Running => StatusMark.MarkState.Running,
            WizardPage.Success or WizardPage.Thanks => StatusMark.MarkState.Success,
            WizardPage.Error => StatusMark.MarkState.Error,
            WizardPage.ConfirmData => StatusMark.MarkState.Warning,
            _ => StatusMark.MarkState.Ready
        };
        statusMark.Invalidate();

        primary.Visible = secondary.Visible = true;
        primary.Enabled = secondary.Enabled = true;
        bool all = removeCheck.Checked;

        switch (next)
        {
            case WizardPage.Welcome:
                title.Text = Texts.Pick("Remover o Azul Groove deste computador?", "Remove Azul Groove from this computer?");
                ApplyWelcomeText();
                primary.Text = Texts.Remove;
                secondary.Text = Texts.Cancel;
                break;

            case WizardPage.ConfirmData:
                title.Text = Texts.ConfirmDataTitle;
                message.Text = Texts.Pick(
                    "Seu login, suas configurações e seus dados pessoais serão apagados permanentemente junto com o aplicativo.",
                    "Your login, settings and personal data will be permanently deleted along with the application.");
                detail.Text = Texts.Pick("Esta ação não pode ser desfeita.", "This can't be undone.");
                detail.ForeColor = Color.FromArgb(255, 196, 120);
                primary.Text = Texts.Pick("Apagar tudo", "Delete everything");
                secondary.Text = Texts.Pick("Voltar", "Back");
                break;

            case WizardPage.Running:
                title.Text = Texts.Pick("Desinstalando o Azul Groove...", "Uninstalling Azul Groove...");
                message.Text = Texts.IsPortuguese
                    ? (all ? "O Windows está removendo o aplicativo, os atalhos e seus dados." : "O Windows está removendo o aplicativo e os atalhos.")
                    : (all ? "Windows is removing the application, its shortcuts and your data." : "Windows is removing the application and its shortcuts.");
                detail.Text = Texts.Pick(
                    "Aguarde. Esta janela será atualizada quando o processo terminar.",
                    "Please wait. This window will update when the process finishes.");
                primary.Visible = secondary.Visible = false;
                break;

            case WizardPage.Success:
                title.Text = Texts.Pick("Desinstalação concluída", "Uninstall complete");
                message.Text = Texts.Pick(
                    "O desinstalador confirmou que o Azul Groove foi removido.",
                    "The uninstaller confirmed that Azul Groove was removed.");
                detail.Text = Texts.IsPortuguese
                    ? (all ? "Seus dados pessoais e configurações foram removidos." : "Seus dados pessoais e configurações foram mantidos.")
                    : (all ? "Your personal data and settings were removed." : "Your personal data and settings were kept.");
                primary.Text = Texts.Finish;
                secondary.Visible = false;
                break;

            case WizardPage.Feedback:
                if (error is null)
                    StartFeedbackIntro();
                title.Text = Texts.Pick("Azul Groove foi removido", "Azul Groove was removed");
                message.Text = error ?? Texts.Pick(
                    "Quer nos contar por quê? É opcional e leva só alguns segundos.",
                    "Want to tell us why? It's optional and only takes a few seconds.");
                if (error is not null)
                    message.ForeColor = Color.FromArgb(255, 196, 120);
                primary.Text = Texts.Pick("Enviar", "Send");
                secondary.Text = Texts.Pick("Pular", "Skip");
                primary.Enabled = HasFeedbackContent();
                break;

            case WizardPage.Thanks:
                if (feedbackSent)
                {
                    title.Text = Texts.Pick("Obrigado pelo feedback!", "Thanks for your feedback!");
                    message.Text = Texts.Pick(
                        "Recebemos sua mensagem. Ela ajuda a melhorar o Azul Groove para todo mundo.",
                        "We received your message. It helps make Azul Groove better for everyone.");
                    detail.Text = Texts.Pick("Feedback enviado com sucesso.", "Feedback sent successfully.");
                }
                else
                {
                    title.Text = Texts.Pick("Obrigado por usar o Azul Groove!", "Thanks for using Azul Groove!");
                    message.Text = Texts.Pick(
                        "Foi um prazer ter você com a gente. Se mudar de ideia, a porta está sempre aberta.",
                        "It was a pleasure having you with us. If you change your mind, the door is always open.");
                    detail.Text = Texts.Pick(
                        "Reinstale quando quiser em azul-groove.vercel.app",
                        "Reinstall anytime at azul-groove.vercel.app");
                }
                primary.Text = Texts.Finish;
                secondary.Visible = false;
                break;

            default: // Error
                title.Text = Texts.Pick("Não foi possível concluir", "Could not complete the uninstall");
                message.Text = error ?? Texts.Pick("O desinstalador retornou um erro.", "The uninstaller returned an error.");
                detail.Text = Texts.Pick(
                    "O aplicativo pode não ter sido removido. Você pode tentar novamente ou fechar esta janela.",
                    "The application may not have been removed. You can try again or close this window.");
                primary.Text = Texts.Retry;
                secondary.Text = Texts.Close;
                break;
        }
    }

    async Task StartUninstallAsync()
    {
        ShowPage(WizardPage.Running);
        running = true;
        primary.Enabled = secondary.Enabled = false;
        try
        {
            var started = Stopwatch.StartNew();
            await PerformUninstallAsync(installDir, removeCheck.Checked);
            int remaining = MinRunningMs - (int)started.ElapsedMilliseconds;
            if (remaining > 0)
                await Task.Delay(remaining); // deixa a barra de progresso visível por um tempo mínimo
            ShowPage(FeedbackSender.IsConfigured ? WizardPage.Feedback : WizardPage.Success);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            ShowPage(WizardPage.Error, Texts.IsPortuguese
                ? "A solicitação de permissão foi cancelada. A desinstalação não foi iniciada."
                : "The permission request was canceled. The uninstall did not start.");
        }
        catch (Exception ex)
        {
            ShowPage(WizardPage.Error, ex.Message);
        }
        finally
        {
            running = false;
        }
    }

    public static Task RunQuietUninstallAsync(string installDir, bool removeUserData = false) =>
        PerformUninstallAsync(Path.GetFullPath(installDir), removeUserData);

    static async Task PerformUninstallAsync(string directory, bool removeUserData)
    {
        // Se o app já foi removido (ex.: "Tentar novamente" depois de falhar só ao apagar os dados),
        // pula direto para a limpeza dos dados.
        bool appPresent = Directory.Exists(directory) &&
            Directory.EnumerateFiles(directory, "unins*.exe", SearchOption.TopDirectoryOnly).Any();
        if (appPresent || !removeUserData)
            await UninstallAppAsync(directory);
        if (removeUserData)
            await DeleteUserDataAsync();
    }

    static async Task UninstallAppAsync(string directory)
    {
        await StopInstalledAppAsync(directory);
        string uninstaller = FindUninstaller(directory);
        var start = new ProcessStartInfo(uninstaller)
        {
            Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /FORCECLOSEAPPLICATIONS",
            UseShellExecute = true,
            WorkingDirectory = directory
        };

        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException(Texts.IsPortuguese
                ? "O Windows não iniciou o desinstalador do Inno Setup."
                : "Windows did not start the Inno Setup uninstaller.");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(Texts.IsPortuguese
                ? $"O desinstalador terminou com o código {process.ExitCode}."
                : $"The uninstaller exited with code {process.ExitCode}.");
        if (!await WaitForRemovalAsync(directory))
            throw new IOException(Texts.IsPortuguese
                ? "O desinstalador terminou, mas o Azul Groove ainda aparece instalado. Feche o aplicativo e tente novamente."
                : "The uninstaller finished, but Azul Groove still appears to be installed. Close the app and try again.");
    }

    static string FindUninstaller(string directory)
    {
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException(Texts.IsPortuguese
                ? "A pasta de instalação não foi encontrada."
                : "The installation folder could not be found.");

        string[] candidates = Directory.GetFiles(directory, "unins*.exe", SearchOption.TopDirectoryOnly);
        if (candidates.Length != 1)
            throw new FileNotFoundException(Texts.IsPortuguese
                ? "Não foi possível identificar com segurança o desinstalador do Inno Setup."
                : "The Inno Setup uninstaller could not be identified safely.");
        return candidates[0];
    }

    static async Task StopInstalledAppAsync(string directory)
    {
        string executable = Path.Combine(directory, "AzulGroove.exe");
        try
        {
            using var quit = EventWaitHandle.OpenExisting("AzulGrooveBeta.UninstallEvent");
            quit.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
        }

        for (int attempt = 0; attempt < 20; attempt++)
        {
            if (!IsInstalledAppRunning(executable))
                return;
            await Task.Delay(250);
        }

        foreach (Process process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
        {
            using (process)
            {
                if (!IsSameExecutable(process, executable))
                    continue;
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    static bool IsInstalledAppRunning(string executable)
    {
        foreach (Process process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
        {
            using (process)
                if (IsSameExecutable(process, executable))
                    return true;
        }
        return false;
    }

    static bool IsSameExecutable(Process process, string executable)
    {
        try
        {
            string? processPath = process.MainModule?.FileName;
            return processPath is not null &&
                string.Equals(Path.GetFullPath(processPath), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    static async Task<bool> WaitForRemovalAsync(string directory)
    {
        string registryKey = Path.Combine("Software", "Microsoft", "Windows", "CurrentVersion", "Uninstall",
            "{8CAB4764-DD94-4752-9DB7-38FA6199BE02}_is1");
        for (int attempt = 0; attempt < 30; attempt++)
        {
            bool filesRemain;
            try
            {
                filesRemain = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                    .Any(path =>
                    {
                        string fileName = Path.GetFileName(path);
                        return string.Equals(fileName, "AzulGroove.exe", StringComparison.OrdinalIgnoreCase) ||
                            fileName.StartsWith("unins", StringComparison.OrdinalIgnoreCase) &&
                            fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
                    });
            }
            catch (DirectoryNotFoundException)
            {
                filesRemain = false;
            }

            using var userKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(registryKey);
            using var machineKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(registryKey);
            bool registrationRemains = userKey is not null || machineKey is not null;
            if (!filesRemain && !registrationRemains)
                return true;

            await Task.Delay(500);
        }

        return false;
    }

    // Pastas de dados do usuário (login, configurações, cache). CONFIRME estes nomes/locais com o app.
    static readonly string[] UserDataFolderNames = { "AzulGrooveBeta", "Azul Groove Beta" };

    static IEnumerable<string> UserDataFolders()
    {
        string[] roots =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        };
        foreach (string root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            foreach (string name in UserDataFolderNames)
            {
                string folder = Path.GetFullPath(Path.Combine(fullRoot, name));
                // Trava de segurança: só apaga uma pasta filha direta da raiz, com nome conhecido.
                if (string.Equals(Path.GetDirectoryName(folder), fullRoot, StringComparison.OrdinalIgnoreCase))
                    yield return folder;
            }
        }
    }

    static async Task DeleteUserDataAsync()
    {
        var failed = new List<string>();
        foreach (string folder in UserDataFolders())
        {
            if (!Directory.Exists(folder))
                continue;
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
            {
                failed.Add(folder); // atalho/junction: não seguimos links por segurança
                continue;
            }

            bool deleted = false;
            for (int attempt = 0; attempt < 8 && !deleted; attempt++)
            {
                try
                {
                    Directory.Delete(folder, recursive: true);
                    deleted = true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    await Task.Delay(400);
                }
            }
            if (!deleted && Directory.Exists(folder))
                failed.Add(folder);
        }

        if (failed.Count > 0)
            throw new IOException(Texts.IsPortuguese
                ? $"O aplicativo foi removido, mas não foi possível apagar seus dados em: {string.Join(", ", failed)}. Feche programas que usem essa pasta e tente novamente."
                : $"The application was removed, but your data could not be deleted from: {string.Join(", ", failed)}. Close programs using that folder and try again.");
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (running)
            e.Cancel = true;
        base.OnFormClosing(e);
    }

    enum WizardPage
    {
        Welcome,
        ConfirmData,
        Running,
        Success,
        Feedback,
        Thanks,
        Error
    }
}

sealed class HeaderPanel : Panel
{
    static readonly int[] BarHeights = { 20, 30, 14, 10, 18, 31, 23 };
    readonly Image? logo;

    public double Time { get; set; }

    public HeaderPanel(Image? logo)
    {
        this.logo = logo;
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);
    }

    int S(int value) => (int)Math.Round(value * DeviceDpi / 96.0);

    public Rectangle BarsBounds => new(Width - S(150), 0, S(150), Height);

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        var area = ClientRectangle;
        if (area.Width <= 0 || area.Height <= 0)
            return;

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

        using (var background = new LinearGradientBrush(area,
            Color.FromArgb(25, 38, 78), Color.FromArgb(49, 41, 99), LinearGradientMode.Horizontal))
            g.FillRectangle(background, area);
        using (var hairline = new Pen(Color.FromArgb(38, 255, 255, 255)))
            g.DrawLine(hairline, 0, Height - 1, Width, Height - 1);

        // Logo com selo em gradiente (cantos suavizados, sem quadrado escuro atrás).
        var badge = new Rectangle(S(24), (Height - S(48)) / 2, S(48), S(48));
        using (var badgePath = UiShapes.RoundedShape(badge, S(13)))
        using (var badgeFill = new LinearGradientBrush(badge,
            Color.FromArgb(93, 96, 255), Color.FromArgb(137, 76, 255), LinearGradientMode.ForwardDiagonal))
        using (var badgeEdge = new Pen(Color.FromArgb(48, 255, 255, 255)))
        {
            g.FillPath(badgeFill, badgePath);
            g.DrawPath(badgeEdge, badgePath);
        }
        if (logo is not null)
            g.DrawImage(logo, Rectangle.Inflate(badge, -S(7), -S(7)));

        // Barras de equalizador (animação baseada em tempo, independente do FPS).
        int left = Width - S(128);
        for (int i = 0; i < BarHeights.Length; i++)
        {
            double wave = Math.Sin(Time * 4.4 + i * 0.73);
            int height = Math.Max(S(8), S(BarHeights[i]) + (int)(wave * S(7)));
            var bounds = new Rectangle(left + i * S(16), Height / 2 - height / 2, S(8), height);
            using var path = UiShapes.RoundedShape(bounds, S(4));
            using var bar = new LinearGradientBrush(bounds, UninstallWizard.AccentEnd, UninstallWizard.Accent, LinearGradientMode.Vertical);
            g.FillPath(bar, path);
        }
    }
}

sealed class RoundedPanel : Panel
{
    Color borderColor;

    public Color BorderColor
    {
        get => borderColor;
        set
        {
            borderColor = value;
            Invalidate();
        }
    }

    public RoundedPanel()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);
    }

    // Sem Region (ela serrilhava os cantos): o canto é pintado com a cor do pai e a forma é suavizada.
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        using (var outside = new SolidBrush(Parent?.BackColor ?? BackColor))
            g.FillRectangle(outside, ClientRectangle);
        if (Width < 4 || Height < 4)
            return;

        g.SmoothingMode = SmoothingMode.AntiAlias;
        int radius = (int)Math.Round(14 * DeviceDpi / 96.0);
        using var path = UiShapes.RoundedShape(new Rectangle(0, 0, Width - 1, Height - 1), radius);
        using var fill = new SolidBrush(BackColor);
        using var border = new Pen(BorderColor);
        g.FillPath(fill, path);
        g.DrawPath(border, path);
    }
}

sealed class ProgressStrip : Control
{
    readonly Stopwatch clock = Stopwatch.StartNew();

    public ProgressStrip()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = UninstallWizard.Card;
        TabStop = false;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        if (Width < 4 || Height < 2)
            return;

        g.SmoothingMode = SmoothingMode.AntiAlias;
        var track = new Rectangle(0, 0, Width - 1, Height - 1);
        int radius = track.Height / 2 + 1;
        using (var trackPath = UiShapes.RoundedShape(track, radius))
        using (var trackBrush = new SolidBrush(Color.FromArgb(39, 47, 69)))
            g.FillPath(trackBrush, trackPath);

        // Trecho em gradiente que desliza com aceleração/desaceleração suaves.
        const double period = 1.7;
        double p = clock.Elapsed.TotalSeconds % period / period;
        double eased = p * p * (3 - 2 * p);
        int segment = Math.Max(Width / 3, 40);
        int x = (int)(-segment + eased * (Width + segment));
        int x0 = Math.Max(0, x);
        int x1 = Math.Min(Width - 1, x + segment);
        if (x1 - x0 < 2)
            return;

        var bounds = new Rectangle(x0, 0, x1 - x0, Height - 1);
        using var path = UiShapes.RoundedShape(bounds, radius);
        using var fill = new LinearGradientBrush(bounds, UninstallWizard.Accent, UninstallWizard.AccentEnd, LinearGradientMode.Horizontal);
        g.FillPath(fill, path);
    }
}

sealed class StatusMark : Control
{
    readonly Stopwatch clock = Stopwatch.StartNew();
    MarkState state;
    double stateSince;

    public MarkState State
    {
        get => state;
        set
        {
            if (state != value)
            {
                state = value;
                stateSince = clock.Elapsed.TotalSeconds;
            }
            Invalidate();
        }
    }

    public StatusMark()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = UninstallWizard.Card;
    }

    static float EaseOut(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return (float)(1 - Math.Pow(1 - t, 3));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        float k = DeviceDpi / 96f;
        double now = clock.Elapsed.TotalSeconds;
        double since = now - stateSince;

        var circle = new RectangleF(1, 1, Width - 3, Height - 3);
        Color accent = State == MarkState.Error
            ? Color.FromArgb(238, 90, 108)
            : State == MarkState.Warning
                ? Color.FromArgb(255, 190, 100)
            : State == MarkState.Success
                ? Color.FromArgb(88, 208, 164)
                : UninstallWizard.AccentEnd;
        using var background = new SolidBrush(Color.FromArgb(32, accent));
        using var outline = new Pen(Color.FromArgb(115, accent), 1.2f * k);
        g.FillEllipse(background, circle);
        g.DrawEllipse(outline, circle);

        var inner = RectangleF.Inflate(circle, -9 * k, -9 * k);
        using var symbol = new Pen(accent, 2.4f * k)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };

        if (State == MarkState.Running)
        {
            // Spinner com arco que "respira" (varia o comprimento) enquanto gira.
            float start = (float)(now * 300 % 360);
            float sweep = 100f + 160f * (0.5f + 0.5f * MathF.Sin((float)(now * 2.8)));
            g.DrawArc(symbol, inner, start, sweep);
        }
        else if (State == MarkState.Warning)
        {
            float cx = inner.Left + inner.Width / 2f;
            g.DrawLine(symbol, cx, inner.Top, cx, inner.Top + inner.Height * 0.58f);
            using var dot = new SolidBrush(accent);
            float d = 3f * k;
            g.FillEllipse(dot, cx - d / 2, inner.Bottom - d, d, d);
        }
        else if (State == MarkState.Error)
        {
            float p = EaseOut(since / 0.3);
            g.DrawLine(symbol, inner.Left, inner.Top, inner.Left + inner.Width * p, inner.Top + inner.Height * p);
            g.DrawLine(symbol, inner.Right, inner.Top, inner.Right - inner.Width * p, inner.Top + inner.Height * p);
        }
        else
        {
            // Check "desenhado" progressivamente quando a desinstalação conclui.
            float p = State == MarkState.Success ? EaseOut(since / 0.45) : 1f;
            var points = new[]
            {
                new PointF(inner.Left, inner.Top + inner.Height / 2),
                new PointF(inner.Left + inner.Width / 3, inner.Bottom - 1),
                new PointF(inner.Right, inner.Top)
            };
            DrawPartial(g, symbol, points, p);
        }
    }

    static void DrawPartial(Graphics g, Pen pen, PointF[] points, float progress)
    {
        if (progress <= 0)
            return;

        var lengths = new float[points.Length - 1];
        float total = 0;
        for (int i = 0; i < lengths.Length; i++)
        {
            float dx = points[i + 1].X - points[i].X;
            float dy = points[i + 1].Y - points[i].Y;
            lengths[i] = MathF.Sqrt(dx * dx + dy * dy);
            total += lengths[i];
        }

        float remaining = total * Math.Min(progress, 1f);
        var path = new List<PointF> { points[0] };
        for (int i = 0; i < lengths.Length; i++)
        {
            if (remaining >= lengths[i])
            {
                path.Add(points[i + 1]);
                remaining -= lengths[i];
            }
            else
            {
                float t = lengths[i] == 0 ? 0 : remaining / lengths[i];
                path.Add(new PointF(
                    points[i].X + (points[i + 1].X - points[i].X) * t,
                    points[i].Y + (points[i + 1].Y - points[i].Y) * t));
                break;
            }
        }

        if (path.Count > 1)
            g.DrawLines(pen, path.ToArray());
    }

    public void AdvanceAnimation()
    {
        if (State == MarkState.Running || clock.Elapsed.TotalSeconds - stateSince < 0.6)
            Invalidate();
    }

    public enum MarkState
    {
        Ready,
        Running,
        Success,
        Warning,
        Error
    }
}

sealed class RoundedButton : Button
{
    static readonly Color ButtonBackground = Color.FromArgb(28, 33, 48);
    static readonly Color ButtonBorder = Color.FromArgb(49, 57, 79);
    bool hovering;
    bool pressed;

    public bool IsPrimary { get; init; }

    public RoundedButton()
    {
        Size = new Size(150, 40);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        BackColor = ButtonBackground;
        ForeColor = Color.White;
        Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold);
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        hovering = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        hovering = false;
        pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        pressed = true;
        Invalidate();
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        pressed = false;
        Invalidate();
        base.OnMouseUp(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        // Pinta o canto com a cor do pai e desenha a forma suavizada (antes: SetClip serrilhava as bordas).
        g.Clear(Parent?.BackColor ?? BackColor);
        if (Width < 4 || Height < 4)
            return;

        g.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        int radius = (int)Math.Round(12 * DeviceDpi / 96.0);
        using var path = UiShapes.RoundedShape(bounds, radius);

        if (IsPrimary)
        {
            var start = !Enabled ? Color.FromArgb(80, 87, 125)
                : pressed ? Color.FromArgb(65, 75, 205)
                : hovering ? Color.FromArgb(105, 112, 255) : UninstallWizard.Accent;
            var end = !Enabled ? Color.FromArgb(94, 82, 128)
                : pressed ? Color.FromArgb(105, 68, 205)
                : hovering ? Color.FromArgb(150, 105, 255) : UninstallWizard.AccentEnd;
            using var fill = new LinearGradientBrush(bounds, start, end, LinearGradientMode.Horizontal);
            g.FillPath(fill, path);
            using var sheen = new Pen(Color.FromArgb(Enabled ? 46 : 0, 255, 255, 255));
            g.DrawPath(sheen, path);
        }
        else
        {
            var fillColor = !Enabled ? Color.FromArgb(35, 39, 51)
                : pressed ? Color.FromArgb(40, 47, 67)
                : hovering ? Color.FromArgb(36, 43, 62) : ButtonBackground;
            using var fill = new SolidBrush(fillColor);
            using var border = new Pen(hovering && Enabled ? Color.FromArgb(79, 91, 125) : ButtonBorder);
            g.FillPath(fill, path);
            g.DrawPath(border, path);
        }

        TextRenderer.DrawText(g, Text, Font, bounds, Enabled ? ForeColor : UninstallWizard.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        if (Focused && ShowFocusCues && Enabled)
        {
            var focusBounds = Rectangle.Inflate(bounds, -3, -3);
            using var focusPath = UiShapes.RoundedShape(focusBounds, Math.Max(radius - 3, 2));
            using var focus = new Pen(Color.FromArgb(205, 224, 228, 255), 1.5f);
            g.DrawPath(focus, focusPath);
        }
    }
}

static class UiShapes
{
    public static GraphicsPath RoundedShape(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        int diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        var arc = new Rectangle(bounds.X, bounds.Y, diameter, diameter);
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }
}

sealed class RoundedCheck : Control
{
    readonly Font captionFont = new("Segoe UI Semibold", 10f, FontStyle.Bold);
    readonly Font hintFont = new("Segoe UI", 9f);
    bool hovering;
    bool isChecked;

    public string Caption { get; set; } = "";
    public string Hint { get; set; } = "";
    public event EventHandler? CheckedChanged;

    public bool Checked
    {
        get => isChecked;
        set
        {
            if (isChecked == value)
                return;
            isChecked = value;
            Invalidate();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public RoundedCheck()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
            ControlStyles.Selectable, true);
        TabStop = true;
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.CheckButton;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            captionFont.Dispose();
            hintFont.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override void OnMouseEnter(EventArgs e) { hovering = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovering = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnClick(EventArgs e)
    {
        Focus();
        Checked = !Checked;
        base.OnClick(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space)
            Checked = !Checked;
        base.OnKeyUp(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        float k = DeviceDpi / 96f;
        int size = (int)Math.Round(20 * k);
        var box = new Rectangle(1, (int)Math.Round(2 * k), size, size);
        int radius = (int)Math.Round(6 * k);
        using var path = UiShapes.RoundedShape(box, radius);

        if (Checked)
        {
            // Vermelho: é uma ação destrutiva (apaga dados), então destaca mais que o roxo padrão.
            using var fill = new LinearGradientBrush(box,
                Color.FromArgb(238, 90, 108), Color.FromArgb(205, 70, 125), LinearGradientMode.ForwardDiagonal);
            g.FillPath(fill, path);
            using var mark = new Pen(Color.White, 2.2f * k)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            g.DrawLines(mark, new[]
            {
                new PointF(box.Left + size * 0.26f, box.Top + size * 0.54f),
                new PointF(box.Left + size * 0.44f, box.Top + size * 0.72f),
                new PointF(box.Left + size * 0.76f, box.Top + size * 0.30f)
            });
        }
        else
        {
            using var fill = new SolidBrush(UninstallWizard.Card);
            using var border = new Pen(hovering ? Color.FromArgb(110, 124, 160) : Color.FromArgb(62, 74, 104), 1.4f);
            g.FillPath(fill, path);
            g.DrawPath(border, path);
        }

        if (Focused && ShowFocusCues)
        {
            using var focusPath = UiShapes.RoundedShape(Rectangle.Inflate(box, 3, 3), radius + 2);
            using var focus = new Pen(Color.FromArgb(170, 224, 228, 255), 1.5f);
            g.DrawPath(focus, focusPath);
        }

        int textLeft = box.Right + (int)Math.Round(12 * k);
        int captionHeight = TextRenderer.MeasureText("Ag", captionFont).Height;
        var flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
        TextRenderer.DrawText(g, Caption, captionFont,
            new Rectangle(textLeft, 0, Math.Max(Width - textLeft, 1), captionHeight),
            Color.White, flags | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, Hint, hintFont,
            new Rectangle(textLeft, captionHeight + 1, Math.Max(Width - textLeft, 1), Math.Max(Height - captionHeight - 1, 1)),
            UninstallWizard.Muted, flags | TextFormatFlags.WordBreak);
    }
}

static class FeedbackSender
{
    // ATENÇÃO: uma URL de webhook embutida no .exe pode ser extraída por qualquer pessoa que baixe o
    // instalador, e quem a tiver pode enviar mensagens ao seu canal. Para produção, prefira apontar isto
    // para um endpoint seu (ex.: função na Vercel) que guarda o webhook em segredo e limita abusos.
    const string WebhookUrl = "https://discord.com/api/webhooks/1561516932090306560/bDnoYJa-hbtXLTjMqiU5gGtZEXKwGDwGt-2oDrvdlSNFRZ_c70MVPlIL5bzjLvKyguwJ";

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };

    public static bool IsConfigured => !string.IsNullOrWhiteSpace(WebhookUrl);

    static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    // Envia só: nota, motivos escolhidos, mensagem digitada, versão do app, versão do Windows, idioma e se os dados
    // foram apagados. Nada de nome de usuário, nome do computador, IP ou arquivos.
    public static async Task SendAsync(string[] reasons, int rating, string message, string? appVersion, bool removedData)
    {
        string stars = rating > 0 ? $"{new string('★', rating)}{new string('☆', 5 - rating)} ({rating}/5)" : "—";
        int color = rating switch
        {
            0 => 0x4F7DF3,
            <= 2 => 0xEE5A6C,
            3 => 0xFFBE64,
            _ => 0x58D0A4
        };

        var fields = new List<object>
        {
            new { name = "Nota", value = stars, inline = true },
            new { name = "Motivos", value = reasons.Length > 0 ? Clip(string.Join(", ", reasons), 200) : "—", inline = true },
            new { name = "Dados removidos", value = removedData ? "Sim" : "Não", inline = true },
            new { name = "Versão do app", value = Clip(appVersion ?? "desconhecida", 100), inline = true },
            new { name = "Windows", value = Clip(RuntimeInformation.OSDescription, 200), inline = true },
            new { name = "Idioma", value = CultureInfo.CurrentUICulture.Name, inline = true }
        };

        var payload = new
        {
            username = "Azul Groove • Feedback",
            allowed_mentions = new { parse = Array.Empty<string>() }, // impede @everyone/@here vindos do texto
            embeds = new[]
            {
                new
                {
                    title = "Feedback de desinstalação",
                    description = message.Length == 0 ? "(sem mensagem)" : Clip(message, 1800),
                    color,
                    timestamp = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    fields
                }
            }
        };

        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await Http.PostAsync(WebhookUrl, content);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"O servidor respondeu {(int)response.StatusCode}.");
    }
}

sealed class ChipButton : Control
{
    readonly Font chipFont = new("Segoe UI Semibold", 9f, FontStyle.Bold);
    bool hovering;
    bool selected;

    public bool Selected
    {
        get => selected;
        set
        {
            if (selected == value)
                return;
            selected = value;
            Invalidate();
        }
    }

    public ChipButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
            ControlStyles.Selectable, true);
        TabStop = true;
        Cursor = Cursors.Hand;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            chipFont.Dispose();
        base.Dispose(disposing);
    }

    protected override void OnMouseEnter(EventArgs e) { hovering = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovering = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnClick(EventArgs e)
    {
        Focus();
        base.OnClick(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space)
            OnClick(EventArgs.Empty);
        base.OnKeyUp(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? BackColor);
        if (Width < 6 || Height < 6)
            return;

        g.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = UiShapes.RoundedShape(bounds, bounds.Height / 2);

        if (Selected)
        {
            using var fill = new LinearGradientBrush(bounds, UninstallWizard.Accent, UninstallWizard.AccentEnd, LinearGradientMode.Horizontal);
            g.FillPath(fill, path);
        }
        else
        {
            using var fill = new SolidBrush(UninstallWizard.Card);
            using var border = new Pen(hovering ? Color.FromArgb(110, 124, 160) : Color.FromArgb(49, 57, 79));
            g.FillPath(fill, path);
            g.DrawPath(border, path);
        }

        TextRenderer.DrawText(g, Text, chipFont, bounds,
            Selected ? Color.White : Color.FromArgb(214, 222, 240),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);

        if (Focused && ShowFocusCues)
        {
            using var focusPath = UiShapes.RoundedShape(Rectangle.Inflate(bounds, -3, -3), Math.Max(bounds.Height / 2 - 3, 2));
            using var focus = new Pen(Color.FromArgb(190, 224, 228, 255), 1.5f);
            g.DrawPath(focus, focusPath);
        }
    }
}

sealed class StarRating : Control
{
    const int StarCount = 5;
    readonly Font captionFont = new("Segoe UI Semibold", 9.5f, FontStyle.Bold);
    int rating;
    int hoverRating;

    public string Prompt { get; set; } = "";
    public string[] Labels { get; set; } = Array.Empty<string>();
    public event EventHandler? ValueChanged;

    public int Value
    {
        get => rating;
        set
        {
            value = Math.Clamp(value, 0, StarCount);
            if (rating == value)
                return;
            rating = value;
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public StarRating()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
            ControlStyles.Selectable, true);
        TabStop = true;
        Cursor = Cursors.Hand;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            captionFont.Dispose();
        base.Dispose(disposing);
    }

    int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

    int StarAt(Point point)
    {
        int cell = S(24) + S(6);
        if (point.Y < 0 || point.Y > Height || point.X < 0)
            return 0;
        int index = point.X / cell;
        return index < StarCount ? index + 1 : 0;
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int star = StarAt(e.Location);
        if (star != hoverRating)
        {
            hoverRating = star;
            Invalidate();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        hoverRating = 0;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            Focus();
            int star = StarAt(e.Location);
            if (star > 0)
                Value = star == Value ? 0 : star; // clicar na mesma estrela limpa a nota
        }
        base.OnMouseUp(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Right or Keys.Up)
            Value++;
        else if (e.KeyCode is Keys.Left or Keys.Down)
            Value--;
        base.OnKeyDown(e);
    }

    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    static PointF[] StarPoints(float cx, float cy, float outer)
    {
        var points = new PointF[10];
        double inner = outer * 0.48;
        for (int i = 0; i < points.Length; i++)
        {
            double radius = i % 2 == 0 ? outer : inner;
            double angle = -Math.PI / 2 + i * Math.PI / 5;
            points[i] = new PointF((float)(cx + radius * Math.Cos(angle)), (float)(cy + radius * Math.Sin(angle)));
        }
        return points;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        int size = S(24);
        int cell = size + S(6);
        int shown = hoverRating > 0 ? hoverRating : rating;
        float cy = Height / 2f;

        for (int i = 0; i < StarCount; i++)
        {
            float cx = i * cell + size / 2f;
            var points = StarPoints(cx, cy, size / 2f - 1);
            bool filled = i < shown;
            using var fill = new SolidBrush(filled ? Color.FromArgb(255, 200, 87) : UninstallWizard.Card);
            using var edge = new Pen(filled ? Color.FromArgb(255, 214, 130) : Color.FromArgb(62, 74, 104), 1.4f)
            {
                LineJoin = LineJoin.Round
            };
            g.FillPolygon(fill, points);
            g.DrawPolygon(edge, points);
        }

        int textLeft = StarCount * cell + S(8);
        string caption = shown > 0 && shown <= Labels.Length ? Labels[shown - 1] : Prompt;
        TextRenderer.DrawText(g, caption, captionFont,
            new Rectangle(textLeft, 0, Math.Max(Width - textLeft, 1), Height),
            shown > 0 ? Color.White : UninstallWizard.Muted,
            TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding |
            TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

        if (Focused && ShowFocusCues)
        {
            using var focusPath = UiShapes.RoundedShape(new Rectangle(-2, 0, StarCount * cell, Height - 1), S(8));
            using var focus = new Pen(Color.FromArgb(150, 224, 228, 255), 1.5f);
            g.DrawPath(focus, focusPath);
        }
    }
}
