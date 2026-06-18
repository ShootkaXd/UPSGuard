using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;
using WinTimer = System.Windows.Forms.Timer;

public class NotifyForm : Form
{
    private const int MIN_WIDTH = 360;
    private const int MAX_WIDTH = 560;

    private const int MARGIN = 12;
    private const int PAD = 14;

    private const int BORDER_W = 14;

    private const int ICON = 54;
    private const int ICON_LEFT = 22;

    private const int ROUND_RADIUS = 16;

    private readonly WinTimer _anim = new();

    private int _y;
    private int _x;
    private int _targetY;

    private bool _isGood;

    private Color _accent;
    private Color _text;

    private Panel _accentPanel = null!;
    private PictureBox _pic = null!;
    private Label _title = null!;
    private Label _body = null!;
    private Button _closeBtn = null!;

    private float _scale;

    private int S(int px) => (int)Math.Round(px * _scale);

    protected override CreateParams CreateParams
    {
        get
        {
            const int CS_DROPSHADOW = 0x00020000;
            var cp = base.CreateParams;
            cp.ClassStyle |= CS_DROPSHADOW;
            return cp;
        }
    }

    public NotifyForm(bool isGood)
    {
        _isGood = isGood;

        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.None;
        BackColor = Color.White;
        TopMost = true;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;

        _scale = Math.Max(1f, DeviceDpi / 96f);

        _accent = _isGood ? Color.ForestGreen : Color.Maroon;
        _text = _accent;

        BuildUi();

        _anim.Interval = 10;
        _anim.Tick += (_, __) =>
        {
            _y -= Math.Max(1, S(10));
            Location = new Point(_x, _y);

            if (_y <= _targetY)
            {
                _y = _targetY;
                Location = new Point(_x, _y);
                _anim.Stop();
            }
        };

        Shown += (_, __) => ApplyRoundedCorners();
        SizeChanged += (_, __) => ApplyRoundedCorners();
    }

    public NotifyForm() : this(isGood: false) { }

    private void BuildUi()
    {
        Width = S(MIN_WIDTH);
        Height = S(96);

        _accentPanel = new Panel
        {
            BackColor = _accent,
            Left = 0,
            Top = 0,
            Width = S(BORDER_W),
            Height = Height
        };
        Controls.Add(_accentPanel);

        _pic = new PictureBox
        {
            Left = S(ICON_LEFT),
            Top = S(18),
            Width = S(ICON),
            Height = S(ICON),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.White
        };

        var img = LoadImage(_isGood);
        if (img != null) _pic.Image = img;
        Controls.Add(_pic);

        var wa = Screen.PrimaryScreen.WorkingArea;
        int desired = wa.Width / 3;
        Width = Math.Min(S(MAX_WIDTH), Math.Max(S(MIN_WIDTH), desired));

        _closeBtn = new Button
        {
            Text = "×",
            Width = S(28),
            Height = S(28),
            Left = Width - S(36),
            Top = S(8),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White,
            ForeColor = Color.FromArgb(80, 80, 80),
            Font = new Font("Segoe UI", 12f * _scale, FontStyle.Bold),
            TabStop = false
        };

        _closeBtn.FlatAppearance.BorderSize = 0;
        _closeBtn.Click += (_, __) => Close();
        Controls.Add(_closeBtn);

        var titleFont = new Font("Segoe UI Semibold", 11f * _scale, FontStyle.Bold);
        var bodyFont = new Font("Segoe UI", 10.25f * _scale, FontStyle.Regular);

        int contentLeft = _pic.Right + S(14);
        int contentWidth = Width - contentLeft - S(PAD) - S(34);

        _title = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(contentWidth, 0),
            Left = contentLeft,
            Top = S(14),
            Font = titleFont,
            ForeColor = _text,
            UseCompatibleTextRendering = true,
            Text = _isGood ? "Питание восстановлено" : "ИБП: питание от батареи"
        };
        Controls.Add(_title);

        _body = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(contentWidth, 0),
            Left = contentLeft,
            Top = _title.Bottom + S(6),
            Font = bodyFont,
            ForeColor = Color.FromArgb(40, 40, 40),
            UseCompatibleTextRendering = true,
            Text = _isGood
                ? "Переход в гибернацию отменён."
                : "Если питание не восстановится, компьютер будет переведён в гибернацию."
        };
        Controls.Add(_body);

        LayoutByContent();

        SetStyle(ControlStyles.UserPaint |
                 ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer, true);
        UpdateStyles();
    }

    private void LayoutByContent()
    {
        int bottom = Math.Max(_pic.Bottom, _body.Bottom) + S(PAD);
        Height = Math.Max(S(96), bottom);
        _accentPanel.Height = Height;
    }

    private void ApplyRoundedCorners()
    {
        try
        {
            int r = S(ROUND_RADIUS);
            if (r < 6) r = 6;

            using var path = new GraphicsPath();
            var rect = new Rectangle(0, 0, Width, Height);
            int d = r * 2;

            path.AddArc(rect.Left, rect.Top, d, d, 180f, 90f);
            path.AddArc(rect.Right - d, rect.Top, d, d, 270f, 90f);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0f, 90f);
            path.AddArc(rect.Left, rect.Bottom - d, d, d, 90f, 90f);
            path.CloseFigure();

            Region?.Dispose();
            Region = new Region(path);
        }
        catch
        {
        }
    }

    private Screen GetTargetScreen()
    {
        try
        {
            var p = Location;
            return Screen.FromPoint(new Point(Math.Max(0, p.X), Math.Max(0, p.Y)));
        }
        catch
        {
            return Screen.PrimaryScreen;
        }
    }

    public void SetMessage(string message, bool isGood)
    {
        _isGood = isGood;

        _accent = isGood ? Color.ForestGreen : Color.Maroon;
        _text = _accent;

        _accentPanel.BackColor = _accent;

        var img = LoadImage(isGood);
        if (img != null)
        {
            var old = _pic.Image;
            _pic.Image = img;
            old?.Dispose();
        }

        _title.Text = isGood ? "Питание восстановлено" : "ИБП: питание от батареи";
        _title.ForeColor = _text;

        _body.Text = message;

        _closeBtn.Left = Width - S(36);
        _closeBtn.Top = S(8);

        int contentLeft = _pic.Right + S(14);
        int contentWidth = Width - contentLeft - S(PAD) - S(34);

        _title.MaximumSize = new Size(contentWidth, 0);
        _body.MaximumSize = new Size(contentWidth, 0);
        _body.Top = _title.Bottom + S(6);

        LayoutByContent();
        ApplyRoundedCorners();

        var wa = GetTargetScreen().WorkingArea;
        _x = wa.Right - Width - S(MARGIN);
        _targetY = wa.Bottom - Height - S(MARGIN);
        _y = _targetY;

        Location = new Point(_x, _y);
        Invalidate();
    }

    public void ShowToast(string message, bool isGood, int timeoutMs)
    {
        SetMessage(message, isGood);

        var wa = GetTargetScreen().WorkingArea;

        _x = wa.Right - Width - S(MARGIN);
        _targetY = wa.Bottom - Height - S(MARGIN);

        _y = wa.Bottom + S(80);
        Location = new Point(_x, _y);

        _anim.Stop();
        _anim.Start();

        Show();
        Activate();
    }

    private static Image? LoadImage(bool isGood)
    {
        try
        {
            string resourceName = isGood
                ? "UPSGuard.NotifyHost.Assets.good_green.png"
                : "UPSGuard.NotifyHost.Assets.BAD_Red.png";

            using (Stream? s = typeof(NotifyForm).Assembly.GetManifestResourceStream(resourceName))
            {
                if (s != null)
                {
                    using var img = Image.FromStream(s);
                    return (Image)img.Clone();
                }
            }

            string fileName = isGood ? "good_green.png" : "BAD_Red.png";
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", fileName);

            if (File.Exists(path))
            {
                using var fs = File.OpenRead(path);
                using var img2 = Image.FromStream(fs);
                return (Image)img2.Clone();
            }
        }
        catch
        {
        }

        return null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _anim.Stop();
            _anim.Dispose();

            if (_pic != null)
            {
                _pic.Image?.Dispose();
                _pic.Image = null;
            }

            Region?.Dispose();
        }

        base.Dispose(disposing);
    }
}