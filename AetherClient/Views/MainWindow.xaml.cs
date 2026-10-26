using System;
using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using AetherClient.ViewModels;

namespace AetherClient.Views
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel? _viewModel;

        // Windows 11 DWM Backdrop Attributes
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        public MainWindow()
        {
            InitializeComponent();
            _viewModel = DataContext as MainViewModel;

            if (_viewModel != null)
            {
                _viewModel.DiagnosticLogs.CollectionChanged += OnLogsCollectionChanged;
            }

            // Window drag move anywhere on glass background
            MouseDown += MainWindow_MouseDown;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    // 1. Enable Immersive Dark Mode for window frame
                    int darkMode = 1;
                    DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

                    // 2. Request Rounded Window Corners (24px)
                    int cornerPreference = 2; // DWMWCP_ROUND
                    DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPreference, sizeof(int));

                    // 3. Request Hardware-Accelerated Acrylic Backdrop (Windows 11 build 22621+)
                    int backdropType = 3; // DWMSBT_TRANSIENTWINDOW (Acrylic Blur)
                    DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));
                }
            }
            catch
            {
                // Graceful fallback on older Windows 10 versions without DWM backdrop API
            }
        }

        private void MainWindow_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed)
            {
                // Allow dragging window from any empty glass surface
                try
                {
                    DragMove();
                }
                catch
                {
                    // Ignore transient non-client exceptions
                }
            }
        }

        private void OnLogsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Add && LogListBox.Items.Count > 0)
            {
                Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        LogListBox.ScrollIntoView(LogListBox.Items[^1]);
                    }
                    catch
                    {
                        // Ignore virtualization exceptions
                    }
                });
            }
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void HideToTrayButton_Click(object sender, RoutedEventArgs e)
        {
            Hide();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // Do not abruptly terminate background services on window close; minimize to system tray
            e.Cancel = true;
            Hide();
        }
    }
}
