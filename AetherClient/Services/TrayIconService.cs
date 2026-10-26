using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using AetherClient.Core;

namespace AetherClient.Services
{
    public sealed class TrayIconService : IDisposable
    {
        private readonly NotifyIcon _notifyIcon;
        private readonly ToolStripMenuItem _statusItem;
        private readonly ToolStripMenuItem _quickToggleItem;
        private readonly Action _showWindowAction;
        private readonly Action _toggleConnectionAction;
        private readonly Action _exitApplicationAction;
        private Icon? _currentIcon;

        public TrayIconService(
            Action showWindowAction,
            Action toggleConnectionAction,
            Action exitApplicationAction)
        {
            _showWindowAction = showWindowAction;
            _toggleConnectionAction = toggleConnectionAction;
            _exitApplicationAction = exitApplicationAction;

            var contextMenu = new ContextMenuStrip
            {
                BackColor = Color.FromArgb(14, 21, 38),
                ForeColor = Color.FromArgb(241, 245, 249),
                ShowImageMargin = false,
                Renderer = new DarkMenuRenderer()
            };

            _statusItem = new ToolStripMenuItem("Status: Standby")
            {
                Enabled = false,
                ForeColor = Color.FromArgb(148, 163, 184)
            };

            var openItem = new ToolStripMenuItem("Open Aether Console");
            openItem.Click += (_, _) => _showWindowAction();

            _quickToggleItem = new ToolStripMenuItem("Quick Connect");
            _quickToggleItem.Click += (_, _) => _toggleConnectionAction();

            var exitItem = new ToolStripMenuItem("Exit Aether");
            exitItem.Click += (_, _) => _exitApplicationAction();

            contextMenu.Items.Add(_statusItem);
            contextMenu.Items.Add(new ToolStripSeparator());
            contextMenu.Items.Add(openItem);
            contextMenu.Items.Add(_quickToggleItem);
            contextMenu.Items.Add(new ToolStripSeparator());
            contextMenu.Items.Add(exitItem);

            _currentIcon = CreateDynamicIcon(ConnectionState.Standby);

            _notifyIcon = new NotifyIcon
            {
                Text = "Aether Client — Standby",
                Icon = _currentIcon,
                ContextMenuStrip = contextMenu,
                Visible = true
            };

            _notifyIcon.DoubleClick += (_, _) => _showWindowAction();
        }

        public void UpdateState(ConnectionState state, RoutingMode mode)
        {
            string modeLabel = mode == RoutingMode.TrueTun ? "TUN" : "Proxy";

            (_statusItem.Text, _quickToggleItem.Text, _notifyIcon.Text) = state switch
            {
                ConnectionState.Connected => ($"● Connected ({modeLabel})", "Quick Disconnect", $"Aether Client — Connected ({modeLabel})"),
                ConnectionState.Hunting   => ($"◌ Hunting Exit Node...", "Cancel Hunting", "Aether Client — Hunting..."),
                _                         => ("○ Standby", "Quick Connect", "Aether Client — Standby")
            };

            Icon? previous = _currentIcon;
            _currentIcon = CreateDynamicIcon(state);
            _notifyIcon.Icon = _currentIcon;
            previous?.Dispose();
        }

        public void ShowNotification(string title, string message)
        {
            _notifyIcon.ShowBalloonTip(2000, title, message, ToolTipIcon.None);
        }

        private static Icon CreateDynamicIcon(ConnectionState state)
        {
            using var bmp = new Bitmap(32, 32);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            Color ringColor = state switch
            {
                ConnectionState.Connected => Color.FromArgb(0, 230, 118),  // #00E676 Emerald
                ConnectionState.Hunting   => Color.FromArgb(255, 145, 0),  // #FF9100 Amber
                _                         => Color.FromArgb(0, 229, 255)   // #00E5FF Cyan
            };

            using (var bgBrush = new SolidBrush(Color.FromArgb(11, 15, 25)))
            {
                g.FillEllipse(bgBrush, 2, 2, 28, 28);
            }

            using (var pen = new Pen(ringColor, 3.5f))
            {
                g.DrawEllipse(pen, 4, 4, 24, 24);
            }

            using (var coreBrush = new SolidBrush(ringColor))
            {
                g.FillEllipse(coreBrush, 11, 11, 10, 10);
            }

            IntPtr hIcon = bmp.GetHicon();
            try
            {
                using Icon temp = Icon.FromHandle(hIcon);
                return (Icon)temp.Clone();
            }
            finally
            {
                NativeMethods.DestroyIcon(hIcon);
            }
        }

        public void Dispose()
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _currentIcon?.Dispose();
        }

        private sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
        {
            public DarkMenuRenderer() : base(new DarkColorTable()) { }
        }

        private sealed class DarkColorTable : ProfessionalColorTable
        {
            public override Color MenuItemSelected => Color.FromArgb(20, 32, 55);
            public override Color MenuItemBorder => Color.FromArgb(0, 229, 255);
            public override Color MenuBorder => Color.FromArgb(30, 45, 75);
            public override Color ToolStripDropDownBackground => Color.FromArgb(14, 21, 38);
            public override Color SeparatorDark => Color.FromArgb(30, 45, 75);
            public override Color SeparatorLight => Color.FromArgb(30, 45, 75);
        }
    }
}
