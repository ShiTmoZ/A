using System;
using System.Threading;
using System.Windows;
using Microsoft.Win32;
using AetherClient.Services;
using AetherClient.ViewModels;
using AetherClient.Views;

namespace AetherClient
{
    public partial class App : Application
    {
        private Mutex? _singleInstanceMutex;
        private MainViewModel? _viewModel;
        private MainWindow? _mainWindow;
        private TrayIconService? _trayService;
        private bool _hasNotifiedTrayMinimize;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            _singleInstanceMutex = new Mutex(true, "Global\\AetherDesktopClient_SingleInstance_Mutex", out bool createdNew);
            if (!createdNew)
            {
                MessageBox.Show(
                    "Aether Client is already running in the system tray.",
                    "Aether Client",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                Shutdown();
                return;
            }

            // Register global crash / logoff hooks to guarantee routing table & proxy restoration
            AppDomain.CurrentDomain.UnhandledException += (_, _) => EmergencyTeardown();
            DispatcherUnhandledException += (_, args) =>
            {
                EmergencyTeardown();
                args.Handled = true;
                Shutdown(1);
            };
            SystemEvents.SessionEnding += (_, _) => EmergencyTeardown();

            _viewModel = new MainViewModel();
            _mainWindow = new MainWindow(_viewModel, OnMinimizedToTray);

            _trayService = new TrayIconService(
                showWindowAction: RestoreMainWindow,
                toggleConnectionAction: () => _ = _viewModel.ToggleConnectionAsync(),
                exitApplicationAction: PerformCleanExit);

            _viewModel.StateChanged += (state, mode) => _trayService.UpdateState(state, mode);

            _mainWindow.Show();
        }

        private void OnMinimizedToTray()
        {
            if (!_hasNotifiedTrayMinimize)
            {
                _hasNotifiedTrayMinimize = true;
                _trayService?.ShowNotification("Aether Running in Background", "Double-click or right-click the tray icon for Quick Disconnect.");
            }
        }

        private void RestoreMainWindow()
        {
            if (_mainWindow is null) return;

            if (!_mainWindow.IsVisible)
                _mainWindow.Show();

            if (_mainWindow.WindowState == WindowState.Minimized)
                _mainWindow.WindowState = WindowState.Normal;

            _mainWindow.Activate();
        }

        private void PerformCleanExit()
        {
            EmergencyTeardown();

            if (_mainWindow is not null)
            {
                _mainWindow.AllowClose = true;
                _mainWindow.Close();
            }

            _trayService?.Dispose();
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
            Shutdown(0);
        }

        private void EmergencyTeardown()
        {
            try
            {
                _viewModel?.Dispose();
            }
            catch { }
        }
    }
}
