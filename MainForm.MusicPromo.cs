namespace AzulGroove;

// Mensagem de divulgação do Azul Groove Music, mostrada ao abrir o app.
// Opções: "Instalar e abrir", "Agora não", "Ver no GitHub" e "Não mostrar mais".
public partial class MainForm
{
    async Task MusicPromoAsync()
    {
        try
        {
            var justUpdatedAtStart = justUpdated; // logo depois de atualizar, o app mostra as novidades: não cobrir com a divulgação
            if (justUpdatedAtStart) return;

            // espera a janela estar visível (se o app abriu na bandeja, aguarda um pouco) e o assistente inicial terminar
            for (int i = 0; i < 40 && !IsDisposed && !(Visible && WindowState != FormWindowState.Minimized); i++)
                await Task.Delay(1500);
            await Task.Delay(2500);

            if (IsDisposed || !Visible || betaWizardTcs != null || IsOffline) return;
            if (!ConsumeMusicPromo()) return;

            using var dlg = new MusicPromoForm(this);
            dlg.ShowDialog(this);
        }
        catch { /* a divulgação nunca atrapalha o app */ }
    }

    sealed class MusicPromoForm : Form
    {
        readonly MainForm host;
        readonly Button bInstall = new(), bLater = new(), bGit = new(), bNever = new();
        readonly Label lbStatus = new();
        readonly ProgressBar pb = new();
        bool busy, done;

        public MusicPromoForm(MainForm host)
        {
            this.host = host;
            Text = host.Tr("p.music.t");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(520, 290);
            BackColor = Color.FromArgb(24, 27, 37);
            ForeColor = Color.FromArgb(242, 244, 255);
            Font = new Font("Segoe UI", 10f);
            Icon = host.Icon;

            // ícone desenhado (quadrado azul/roxo com a nota musical), no lugar do glifo de texto
            var badge = new Panel { Left = 24, Top = 20, Width = 52, Height = 52, BackColor = BackColor };
            badge.Paint += (_, e) => PaintBadge(e.Graphics, badge.ClientRectangle);
            Controls.Add(badge);
            Controls.Add(new Label
            {
                Text = host.Tr("p.music.t"),
                Font = new Font("Segoe UI Semibold", 14f), AutoSize = false,
                Left = 90, Top = 20, Width = 300, Height = 30
            });
            var tag = new Label
            {
                Text = host.Tr("p.music.new").ToUpperInvariant(), AutoSize = false, TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI Semibold", 8.5f), ForeColor = Color.White, BackColor = Color.FromArgb(106, 92, 255),
                Left = 90, Top = 52, Width = 54, Height = 20
            };
            Controls.Add(tag);
            Controls.Add(new Label
            {
                Text = host.Tr("p.music.s"), ForeColor = Color.FromArgb(154, 163, 178), AutoSize = false,
                Left = 24, Top = 84, Width = 472, Height = 44
            });

            pb.SetBounds(24, 134, 472, 8);
            pb.Visible = false;
            lbStatus.SetBounds(24, 146, 472, 22);
            lbStatus.ForeColor = Color.FromArgb(154, 163, 178);
            lbStatus.Visible = false;
            Controls.Add(pb);
            Controls.Add(lbStatus);

            Style(bInstall, host.Tr("p.music.install"), 24, 180, 472, 42, primary: true);
            Style(bLater, host.Tr("p.music.later"), 24, 236, 152, 34);
            Style(bGit, host.Tr("p.music.github"), 184, 236, 152, 34);
            Style(bNever, host.Tr("p.music.never"), 344, 236, 152, 34);
            AcceptButton = bInstall;
            CancelButton = bLater;

            bInstall.Click += async (_, _) => await InstallAsync();
            bLater.Click += (_, _) => Close();
            bGit.Click += (_, _) => { OpenExternal(MusicPage); Close(); };
            bNever.Click += (_, _) => { host.cfg.MusicPromoOff = true; host.cfg.Save(); Close(); };
            FormClosing += (_, e) => { if (busy) e.Cancel = true; }; // não fecha no meio da instalação
        }

        static void PaintBadge(Graphics g, Rectangle r)
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            r.Width -= 1; r.Height -= 1;
            using var path = new System.Drawing.Drawing2D.GraphicsPath();
            int d = 16;
            path.AddArc(r.X, r.Y, d, d, 180, 90); path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            using (var br = new System.Drawing.Drawing2D.LinearGradientBrush(r, Color.FromArgb(88, 101, 242), Color.FromArgb(122, 92, 255), 45f))
                g.FillPath(br, path);
            // nota musical: duas cabeças, hastes e a barra que as liga
            float u = r.Width / 24f;
            using var pen = new Pen(Color.White, 2.2f * u) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round, LineJoin = System.Drawing.Drawing2D.LineJoin.Round };
            g.DrawLines(pen, new[] { new PointF(9.5f * u, 19.5f * u), new PointF(9.5f * u, 7f * u), new PointF(19.5f * u, 5f * u), new PointF(19.5f * u, 17.5f * u) });
            g.FillEllipse(Brushes.White, 6.2f * u, 17f * u, 6.6f * u, 5.6f * u);
            g.FillEllipse(Brushes.White, 16.2f * u, 15f * u, 6.6f * u, 5.6f * u);
        }

        void Style(Button b, string text, int x, int y, int w, int h, bool primary = false)
        {
            b.Text = text;
            b.SetBounds(x, y, w, h);
            b.FlatStyle = FlatStyle.Flat;
            b.Cursor = Cursors.Hand;
            b.UseVisualStyleBackColor = false;
            b.ForeColor = Color.White;
            b.FlatAppearance.BorderColor = primary ? Color.FromArgb(122, 92, 255) : Color.FromArgb(39, 44, 59);
            b.BackColor = primary ? Color.FromArgb(106, 92, 255) : Color.FromArgb(24, 27, 37);
            if (primary) b.Font = new Font("Segoe UI Semibold", 11f);
            Controls.Add(b);
        }

        void SetBusy(bool on)
        {
            busy = on;
            bInstall.Enabled = bLater.Enabled = bGit.Enabled = bNever.Enabled = !on;
        }

        void OnPhase(string phase, int pct, string? msg)
        {
            lbStatus.Visible = true;
            lbStatus.ForeColor = Color.FromArgb(154, 163, 178);
            switch (phase)
            {
                case "check":
                    lbStatus.Text = host.Tr("p.music.check");
                    pb.Visible = true; pb.Style = ProgressBarStyle.Marquee; break;
                case "download":
                    lbStatus.Text = string.Format(host.Tr("p.music.dl"), pct);
                    pb.Visible = true; pb.Style = ProgressBarStyle.Continuous; pb.Value = Math.Clamp(pct, 0, 100); break;
                case "install":
                    lbStatus.Text = host.Tr("p.music.inst");
                    pb.Visible = true; pb.Style = ProgressBarStyle.Marquee; break;
                case "launch":
                    lbStatus.Text = host.Tr("p.music.launch"); break;
                case "done":
                    done = true; break;
                case "err":
                    pb.Visible = false;
                    lbStatus.ForeColor = Color.FromArgb(237, 66, 69);
                    lbStatus.Text = string.IsNullOrEmpty(msg) ? host.Tr("p.music.err") : msg;
                    break;
            }
        }

        async Task InstallAsync()
        {
            SetBusy(true);
            host.musicObserver = OnPhase;
            try { await host.MusicGoAsync(); }
            finally { host.musicObserver = null; }
            SetBusy(false);
            if (done) Close(); // instalou e abriu: fecha a mensagem
        }
    }
}
