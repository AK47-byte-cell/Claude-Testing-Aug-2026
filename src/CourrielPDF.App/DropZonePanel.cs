using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CourrielPDF.App
{
    /// <summary>The big dashed drop target at the top of the window.</summary>
    internal sealed class DropZonePanel : Panel
    {
        private bool _active;

        public DropZonePanel()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            AllowDrop = true;
            BackColor = Color.FromArgb(247, 249, 252);
            Cursor = Cursors.Hand;
        }

        public string Title { get; set; } = "Glissez-déposez ici vos courriels";
        public string Subtitle { get; set; } = "Fichiers .msg ou .eml, dossiers complets, ou messages glissés directement depuis Outlook";

        public bool Active
        {
            get => _active;
            set
            {
                if (_active == value) return;
                _active = value;
                BackColor = value ? Color.FromArgb(225, 238, 255) : Color.FromArgb(247, 249, 252);
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(8, 8, Width - 17, Height - 17);
            if (rect.Width <= 0 || rect.Height <= 0) return;

            using (var path = RoundedRect(rect, 14))
            using (var pen = new Pen(_active ? Color.FromArgb(0, 99, 177) : Color.FromArgb(150, 160, 175), 2f) { DashStyle = DashStyle.Dash })
            {
                g.DrawPath(pen, path);
            }

            using (var titleFont = new Font("Segoe UI", 15f, FontStyle.Bold))
            using (var subFont = new Font("Segoe UI", 9.5f))
            using (var brush = new SolidBrush(_active ? Color.FromArgb(0, 80, 150) : Color.FromArgb(50, 60, 75)))
            using (var subBrush = new SolidBrush(Color.FromArgb(95, 105, 120)))
            {
                var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisWord };
                var titleRect = new RectangleF(rect.X + 16, rect.Y + rect.Height / 2f - 40, rect.Width - 32, 40);
                var subRect = new RectangleF(rect.X + 16, rect.Y + rect.Height / 2f + 2, rect.Width - 32, 44);
                g.DrawString(Title, titleFont, brush, titleRect, fmt);
                g.DrawString(Subtitle, subFont, subBrush, subRect, fmt);
            }
        }

        private static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            var d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
