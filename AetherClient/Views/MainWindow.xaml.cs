using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using AetherClient.ViewModels;

namespace AetherClient.Views
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;
        private readonly Action _onHideToTray;
        public bool AllowClose { get; set; }

        // Windows 11 DWM Backdrop Attributes
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        public MainWindow() : this(new MainViewModel(), () => { })
        {
        }

        public MainWindow(MainViewModel viewModel, Action onHideToTray)
        {
            InitializeComponent();
            _viewModel = viewModel;
            _onHideToTray = onHideToTray;
            DataContext = _viewModel;

            SafeSetWindowIcon();

            ((INotifyCollectionChanged)_viewModel.DiagnosticLogs).CollectionChanged += OnLogsCollectionChanged;
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
                    int darkMode = 1;
                    DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

                    int cornerPreference = 2; // DWMWCP_ROUND
                    DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPreference, sizeof(int));

                    int backdropType = 3; // DWMSBT_TRANSIENTWINDOW (Acrylic Blur)
                    DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));
                }
            }
            catch
            {
                // Graceful fallback
            }
        }

        private void MainWindow_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed)
            {
                try
                {
                    DragMove();
                }
                catch { }
            }
        }

        private void SafeSetWindowIcon()
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string[] candidatePaths =
                {
                    System.IO.Path.Combine(baseDir, "app.ico"),
                    System.IO.Path.Combine(baseDir, "assets", "app.ico")
                };

                foreach (string path in candidatePaths)
                {
                    if (System.IO.File.Exists(path))
                    {
                        using var stream = System.IO.File.OpenRead(path);
                        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(
                            stream,
                            System.Windows.Media.Imaging.BitmapCreateOptions.None,
                            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                        return;
                    }
                }
            }
            catch { }
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
                    catch { }
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
            _onHideToTray();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!AllowClose)
            {
                e.Cancel = true;
                Hide();
                _onHideToTray();
                return;
            }

            ((INotifyCollectionChanged)_viewModel.DiagnosticLogs).CollectionChanged -= OnLogsCollectionChanged;
            base.OnClosing(e);
        }
    }
}
