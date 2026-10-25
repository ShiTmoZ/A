using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using AetherClient.Core;
using AetherClient.Models;

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
                BackColor = Color.FromArgb(22, 25, 34),
                ForeColor = Color.FromArgb(241, 245, 249),
                ShowImageMargin = false,
                Renderer = new DarkMenuRenderer()
            };

            _statusItem = new ToolStripMenuItem("Status: Disconnected")
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

            _currentIcon = CreateDynamicIcon(ConnectionState.Disconnected);

            _notifyIcon = new NotifyIcon
            {
                Text = "Aether Client — Disconnected",
                Icon = _currentIcon,
                ContextMenuStrip = contextMenu,
                Visible = true
            };

            _notifyIcon.DoubleClick += (_, _) => _showWindowAction();
        }

        public void UpdateState(ConnectionState state, TunnelMode mode)
        {
            string modeLabel = mode == TunnelMode.TrueTun ? "TUN" : "Proxy";

            (_statusItem.Text, _quickToggleItem.Text, _notifyIcon.Text) = state switch
            {
                ConnectionState.Connected => ($"● Connected ({modeLabel})", "Quick Disconnect", $"Aether Client — Connected ({modeLabel})"),
                ConnectionState.Hunting   => ($"◌ Hunting Exit Node...", "Cancel Hunting", "Aether Client — Hunting..."),
                _                         => ("○ Disconnected", "Quick Connect", "Aether Client — Disconnected")
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
                ConnectionState.Connected => Color.FromArgb(56, 189, 248),  // #38BDF8 Sky Accent
                ConnectionState.Hunting   => Color.FromArgb(245, 158, 11),  // #F59E0B Amber
                _                         => Color.FromArgb(100, 116, 139)  // #64748B Slate
            };

            using (var bgBrush = new SolidBrush(Color.FromArgb(15, 17, 23)))
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
            public override Color MenuItemSelected => Color.FromArgb(30, 41, 59);
            public override Color MenuItemBorder => Color.FromArgb(56, 189, 248);
            public override Color MenuBorder => Color.FromArgb(45, 55, 72);
            public override Color ToolStripDropDownBackground => Color.FromArgb(22, 25, 34);
            public override Color SeparatorDark => Color.FromArgb(45, 55, 72);
            public override Color SeparatorLight => Color.FromArgb(45, 55, 72);
        }
    }
}
