using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace UPSGuard.NotifyHost
{
    internal sealed class AboutForm : Form
    {
        private readonly float _scale;

        private readonly Color _bg = Color.FromArgb(18, 18, 20);
        private readonly Color _footerBg = Color.FromArgb(22, 22, 24);
        private readonly Color _cardBg = Color.FromArgb(24, 24, 28);
        private readonly Color _textMain = Color.FromArgb(235, 235, 235);
        private readonly Color _textSecondary = Color.FromArgb(200, 200, 200);
        private readonly Color _textMuted = Color.FromArgb(160, 160, 160);

        private int S(int px) => (int)Math.Round(px * _scale);

        public AboutForm(
            string productName = "UPSGuard",
            string versionText = "4.2.1",
            string companyLogoFileName = "logo+text_whitemdpi.png")
        {
            _scale = GetScale();

            Text = "О приложении";
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = false;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = _bg;
            ForeColor = _textMain;
            ClientSize = new Size(S(560), S(500));

            Icon = LoadWindowIcon();

            BuildUi(productName, versionText, companyLogoFileName);
        }

        private void BuildUi(
            string productName,
            string versionText,
            string companyLogoFileName)
        {
            int w = ClientSize.Width;

            var companyLogo = new PictureBox
            {
                Width = S(250),
                Height = S(110),
                Left = (w - S(250)) / 2,
                Top = S(42),
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = _bg
            };

            var logoImage = LoadImage(companyLogoFileName);
            if (logoImage != null)
                companyLogo.Image = logoImage;

            Controls.Add(companyLogo);

            var title = new Label
            {
                Text = productName,
                Left = S(20),
                Top = companyLogo.Bottom + S(14),
                Width = w - S(40),
                Height = S(42),
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = _textMain,
                Font = new Font("Segoe UI Semibold", 22f * _scale, FontStyle.Bold),
                AutoEllipsis = true
            };
            Controls.Add(title);

            var version = new Label
            {
                Text = $"Версия {versionText}",
                Left = S(20),
                Top = title.Bottom + S(2),
                Width = w - S(40),
                Height = S(24),
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = _textSecondary,
                Font = new Font("Segoe UI", 11.5f * _scale, FontStyle.Regular),
                AutoEllipsis = true
            };
            Controls.Add(version);

            var subtitle = new Label
            {
                Text = "Модуль уведомлений о состоянии электропитания и ИБП",
                Left = S(30),
                Top = version.Bottom + S(14),
                Width = w - S(60),
                Height = S(24),
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = _textMuted,
                Font = new Font("Segoe UI", 10f * _scale, FontStyle.Regular)
            };
            Controls.Add(subtitle);

            var infoPanel = new Panel
            {
                Left = S(48),
                Top = subtitle.Bottom + S(18),
                Width = w - S(96),
                Height = S(150),
                BackColor = _cardBg
            };
            Controls.Add(infoPanel);

            int labelX = S(18);
            int valueX = S(170);
            int rowY = S(18);
            int rowStep = S(28);

            AddInfoRow(infoPanel, "Программа:", productName, labelX, valueX, rowY);
            rowY += rowStep;

            AddInfoRow(infoPanel, "Версия:", versionText, labelX, valueX, rowY);
            rowY += rowStep;

            AddInfoRow(infoPanel, "Компонент:", "NotifyHost", labelX, valueX, rowY);
            rowY += rowStep;

            AddInfoRow(infoPanel, "Компания:", "Код безопасности", labelX, valueX, rowY);
            rowY += rowStep;

            AddInfoRow(infoPanel, "Назначение:", "Отображение уведомлений ИБП", labelX, valueX, rowY);

            var footer = new Panel
            {
                Left = 0,
                Top = ClientSize.Height - S(68),
                Width = ClientSize.Width,
                Height = S(68),
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
                BackColor = _footerBg
            };
            Controls.Add(footer);

            var closeBtn = new Button
            {
                Text = "Закрыть",
                Width = S(92),
                Height = S(34),
                Left = footer.Width - S(24) - S(92),
                Top = S(18),
                Anchor = AnchorStyles.Right | AnchorStyles.Top,
                FlatStyle = FlatStyle.Flat,
                BackColor = _footerBg,
                ForeColor = _textMain,
                Font = new Font("Segoe UI", 10f * _scale, FontStyle.Regular),
                DialogResult = DialogResult.OK
            };

            closeBtn.FlatAppearance.BorderColor = Color.FromArgb(210, 210, 210);
            closeBtn.FlatAppearance.BorderSize = 1;
            closeBtn.FlatAppearance.MouseOverBackColor = Color.FromArgb(38, 38, 40);
            closeBtn.FlatAppearance.MouseDownBackColor = Color.FromArgb(48, 48, 50);

            footer.Controls.Add(closeBtn);

            AcceptButton = closeBtn;
            CancelButton = closeBtn;
        }

        private void AddInfoRow(Panel parent, string label, string value, int labelX, int valueX, int top)
        {
            var lbl = new Label
            {
                Text = label,
                Left = labelX,
                Top = top,
                Width = S(140),
                Height = S(22),
                ForeColor = _textSecondary,
                Font = new Font("Segoe UI", 9.8f * _scale, FontStyle.Regular),
                TextAlign = ContentAlignment.MiddleLeft
            };

            var val = new Label
            {
                Text = value,
                Left = valueX,
                Top = top,
                Width = parent.Width - valueX - S(18),
                Height = S(22),
                ForeColor = _textMain,
                Font = new Font("Segoe UI Semibold", 9.8f * _scale, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft
            };

            parent.Controls.Add(lbl);
            parent.Controls.Add(val);
        }

        private static float GetScale()
        {
            try
            {
                using var g = Graphics.FromHwnd(IntPtr.Zero);
                return Math.Max(1f, g.DpiX / 96f);
            }
            catch
            {
                return 1f;
            }
        }

        private static Image? LoadImage(string fileName)
        {
            try
            {
                string filePath = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "Assets",
                    fileName);

                if (File.Exists(filePath))
                {
                    using var fs = File.OpenRead(filePath);
                    using var img = Image.FromStream(fs);
                    return (Image)img.Clone();
                }

                var asm = typeof(AboutForm).Assembly;
                var resourceName = asm.GetManifestResourceNames()
                    .FirstOrDefault(x =>
                        x.EndsWith(".Assets." + fileName, StringComparison.OrdinalIgnoreCase) ||
                        x.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));

                if (!string.IsNullOrWhiteSpace(resourceName))
                {
                    using var s = asm.GetManifestResourceStream(resourceName);
                    if (s != null)
                    {
                        using var img = Image.FromStream(s);
                        return (Image)img.Clone();
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        private static Icon LoadWindowIcon()
        {
            try
            {
                string icoPath = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "Assets",
                    "icon.ico");

                if (File.Exists(icoPath))
                    return new Icon(icoPath);

                return Icon.ExtractAssociatedIcon(Application.ExecutablePath)
                       ?? SystemIcons.Application;
            }
            catch
            {
                return SystemIcons.Application;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (Control c in Controls)
                {
                    if (c is PictureBox pb)
                    {
                        pb.Image?.Dispose();
                        pb.Image = null;
                    }
                }
            }

            base.Dispose(disposing);
        }
    }
}