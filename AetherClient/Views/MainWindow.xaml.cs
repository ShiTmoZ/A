using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using AetherClient.ViewModels;

namespace AetherClient.Views
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;
        private readonly Action _onHideToTray;
        public bool AllowClose { get; set; }

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
                LogListBox.ScrollIntoView(LogListBox.Items[^1]);
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
