using System;
using System.IO;
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

            try
            {
                // Mutex for single instance check with safe fallback
                bool createdNew = true;
                try
                {
                    _singleInstanceMutex = new Mutex(true, "Local\\AetherDesktopClient_SingleInstance_Mutex", out createdNew);
                }
                catch
                {
                    // Fall back if Mutex fails
                    createdNew = true;
                }

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

                // Register global crash / logoff hooks with crash file logging
                AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                {
                    LogFatalException(args.ExceptionObject as Exception);
                    EmergencyTeardown();
                };

                DispatcherUnhandledException += (_, args) =>
                {
                    LogFatalException(args.Exception);
                    EmergencyTeardown();
                    MessageBox.Show(
                        $"Aether encountered an unhandled exception:\n\n{args.Exception?.Message}\n\nDetails saved to crash.log",
                        "Aether Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    args.Handled = true;
                    Shutdown(1);
                };

                SystemEvents.SessionEnding += (_, _) => EmergencyTeardown();

                _viewModel = new MainViewModel();
                _mainWindow = new MainWindow(_viewModel, OnMinimizedToTray);

                try
                {
                    _trayService = new TrayIconService(
                        showWindowAction: RestoreMainWindow,
                        toggleConnectionAction: () => _ = _viewModel.ToggleConnectionAsync(),
                        exitApplicationAction: PerformCleanExit);

                    _viewModel.StateChanged += (state, mode) => _trayService.UpdateState(state, mode);
                }
                catch (Exception trayEx)
                {
                    LogFatalException(trayEx);
                }

                _mainWindow.Show();
            }
            catch (Exception ex)
            {
                LogFatalException(ex);
                MessageBox.Show(
                    $"Fatal error on startup:\n\n{ex.Message}\n\n{ex.StackTrace}",
                    "Aether Fatal Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown(1);
            }
        }

        private static void LogFatalException(Exception? ex)
        {
            if (ex is null) return;
            try
            {
                string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log");
                string logContent = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss UTC}] FATAL EXCEPTION:\n{ex}\n\n";
                File.AppendAllText(logPath, logContent);
            }
            catch { }
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
