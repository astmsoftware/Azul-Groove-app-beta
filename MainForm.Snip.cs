using System.Drawing.Imaging;
using D2 = System.Drawing.Drawing2D;

namespace AzulGroove;

/// <summary>
/// "Capturar tela" do feedback, igual ao recorte do Windows (Win + Shift + S):
/// o app some por um instante, a tela fica escurecida e a pessoa arrasta para escolher a área.
/// Barra no topo: arrastar = recorte · botão "Tela inteira" · ✕ (ou Esc / botão direito) cancela.
/// </summary>
public partial class MainForm
{
    async Task FbSnipAsync()
    {
        if (fbCapturing) return;
        if (fbFiles.Count >= FbMaxFiles) { await FbPushAsync($"Máximo de {FbMaxFiles} anexos.", "err"); return; }

        fbCapturing = true;
        var prev = Opacity;
        Bitmap? shot = null;
        try
        {
            var vs = SystemInformation.VirtualScreen; // todos os monitores juntos
            Opacity = 0;
            await Task.Delay(350); // dá tempo do Windows redesenhar a tela sem o app

            shot = new Bitmap(vs.Width, vs.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(shot))
                g.CopyFromScreen(vs.Location, Point.Empty, vs.Size);

            Bitmap? result;
            using (var snip = new SnipForm(shot, vs))
            {
                snip.ShowDialog();
                result = snip.Result;
            }

            Opacity = prev;
            ShowWindow();

            if (result == null) { await FbPushAsync("Captura cancelada.", ""); return; }

            using (result)
            {
                Directory.CreateDirectory(FbTempDir);
                var path = Path.Combine(FbTempDir, $"tela-{DateTime.Now:HHmmss}.png");
                result.Save(path, ImageFormat.Png);

                if (FbTotalBytes() + FbSize(path) > FbMaxTotalBytes)
                {
                    try { File.Delete(path); } catch { }
                    await FbPushAsync($"A captura passou do limite de {FbMb(FbMaxTotalBytes)}. Remova algum anexo ou escolha uma área menor.", "err");
                    return;
                }
                fbFiles.Add(path);
            }
            await FbPushAsync("Captura adicionada aos anexos.", "");
        }
        catch
        {
            await FbPushAsync("Não consegui capturar. Use Win + Shift + S e anexe o arquivo.", "err");
        }
        finally
        {
            shot?.Dispose();
            Opacity = prev;
            fbCapturing = false;
        }
    }
}

/// <summary>Tela escurecida por cima de um "congelado" da tela, com seleção por arrasto.</summary>
sealed class SnipForm : Form
{
    readonly Bitmap shot;     // a tela como estava (não é descartado aqui; quem criou descarta)
    readonly Bitmap dimmed;   // a mesma tela, escurecida (montada uma vez só: o desenho fica leve)
    readonly Panel bar = new();
    Point start;
    Rectangle sel;
    bool dragging;

    public Bitmap? Result { get; private set; }

    public SnipForm(Bitmap screenshot, Rectangle bounds)
    {
        shot = screenshot;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = bounds;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        DoubleBuffered = true;
        BackColor = Color.Black;
        Cursor = Cursors.Cross;

        dimmed = new Bitmap(shot.Width, shot.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(dimmed))
        {
            var all = new Rectangle(0, 0, shot.Width, shot.Height);
            g.DrawImage(shot, all, all, GraphicsUnit.Pixel);
            using var br = new SolidBrush(Color.FromArgb(125, 8, 10, 20));
            g.FillRectangle(br, all);
        }
        BuildBar();
    }

    void BuildBar()
    {
        var fg = Color.FromArgb(235, 238, 247);
        bar.Size = new Size(420, 46);
        bar.BackColor = Color.FromArgb(28, 31, 44);
        bar.Cursor = Cursors.Default;

        var hint = new Label
        {
            Text = "▭  Arraste para escolher a área", ForeColor = fg, BackColor = Color.Transparent,
            Left = 14, Top = 0, Width = 230, Height = 46, TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI", 9.5f)
        };
        Button Btn(string text, int x, int w, Color back)
        {
            var b = new Button
            {
                Text = text, Left = x, Top = 7, Width = w, Height = 32, FlatStyle = FlatStyle.Flat,
                BackColor = back, ForeColor = fg, Cursor = Cursors.Hand, TabStop = false,
                Font = new Font("Segoe UI", 9.5f)
            };
            b.FlatAppearance.BorderSize = 0;
            return b;
        }
        var full = Btn("Tela inteira", 250, 106, Color.FromArgb(88, 101, 242));
        var close = Btn("✕", 364, 42, Color.FromArgb(52, 56, 74));
        full.Click += (_, _) => Finish(new Rectangle(0, 0, shot.Width, shot.Height));
        close.Click += (_, _) => CancelSnip();
        bar.Controls.AddRange(new Control[] { hint, full, close });

        using (var gp = new D2.GraphicsPath())
        {
            int r = 14, w = bar.Width, h = bar.Height;
            gp.AddArc(0, 0, r, r, 180, 90);
            gp.AddArc(w - r, 0, r, r, 270, 90);
            gp.AddArc(w - r, h - r, r, r, 0, 90);
            gp.AddArc(0, h - r, r, r, 90, 90);
            gp.CloseFigure();
            bar.Region = new Region(gp);
        }

        // centralizada no topo do monitor onde o mouse está
        var scr = Screen.FromPoint(Cursor.Position).Bounds;
        bar.Location = new Point(scr.X - Bounds.X + (scr.Width - bar.Width) / 2, scr.Y - Bounds.Y + 24);
        Controls.Add(bar);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) CancelSnip();
        base.OnKeyDown(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) { CancelSnip(); return; } // botão direito cancela, como no Windows
        dragging = true;
        start = e.Location;
        var old = sel;
        sel = Rectangle.Empty;
        if (!old.IsEmpty) Invalidate(Grow(old));
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (dragging)
        {
            var n = Rectangle.Intersect(Norm(start, e.Location), ClientRectangle);
            var inv = sel.IsEmpty ? Grow(n) : Rectangle.Union(Grow(sel), Grow(n));
            sel = n;
            Invalidate(inv);
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (dragging && e.Button == MouseButtons.Left)
        {
            dragging = false;
            if (sel.Width >= 6 && sel.Height >= 6) Finish(sel);
            else { var old = sel; sel = Rectangle.Empty; Invalidate(Grow(old)); } // clique sem arrastar: tenta de novo
        }
        base.OnMouseUp(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var clip = Rectangle.Intersect(e.ClipRectangle, new Rectangle(0, 0, dimmed.Width, dimmed.Height));
        if (clip.Width > 0 && clip.Height > 0) g.DrawImage(dimmed, clip, clip, GraphicsUnit.Pixel);
        if (sel.IsEmpty) return;

        g.DrawImage(shot, sel, sel, GraphicsUnit.Pixel); // a área escolhida aparece sem escurecer
        using (var pen = new Pen(Color.FromArgb(109, 124, 255), 2))
            g.DrawRectangle(pen, sel.X, sel.Y, sel.Width - 1, sel.Height - 1);

        var txt = $"{sel.Width} × {sel.Height}";
        using var f = new Font("Segoe UI", 9f, FontStyle.Bold);
        var sz = g.MeasureString(txt, f);
        float ly = sel.Y - sz.Height - 6 < 0 ? sel.Y + 6 : sel.Y - sz.Height - 6;
        using (var bb = new SolidBrush(Color.FromArgb(225, 28, 31, 44)))
            g.FillRectangle(bb, sel.X, ly, sz.Width + 8, sz.Height + 2);
        g.DrawString(txt, f, Brushes.White, sel.X + 4, ly + 1);
    }

    void Finish(Rectangle r)
    {
        r.Intersect(new Rectangle(0, 0, shot.Width, shot.Height));
        if (r.Width < 1 || r.Height < 1) { CancelSnip(); return; }
        Result = shot.Clone(r, PixelFormat.Format32bppArgb);
        DialogResult = DialogResult.OK;
        Close();
    }

    void CancelSnip()
    {
        Result = null;
        DialogResult = DialogResult.Cancel;
        Close();
    }

    static Rectangle Norm(Point a, Point b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    static Rectangle Grow(Rectangle r) { r.Inflate(40, 40); return r; }

    protected override void Dispose(bool disposing)
    {
        if (disposing) dimmed.Dispose();
        base.Dispose(disposing);
    }
}
