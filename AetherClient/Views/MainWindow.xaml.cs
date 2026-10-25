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

        public MainWindow(MainViewModel viewModel, Action onHideToTray)
        {
            InitializeComponent();
            _viewModel = viewModel;
            _onHideToTray = onHideToTray;
            DataContext = _viewModel;

            ((INotifyCollectionChanged)_viewModel.DiagnosticLogs).CollectionChanged += OnLogsCollectionChanged;
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
