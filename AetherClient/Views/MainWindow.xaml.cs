using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using AetherClient.ViewModels;

namespace AetherClient.Views
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;
        private readonly Action? _onMinimizedToTray;

        public bool AllowClose { get; set; }

        public MainWindow() : this(new MainViewModel(), null)
        {
        }

        public MainWindow(MainViewModel viewModel, Action? onMinimizedToTray = null)
        {
            InitializeComponent();

            _viewModel = viewModel;
            DataContext = _viewModel;
            _onMinimizedToTray = onMinimizedToTray;

            SafeSetWindowIcon();

            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
            if (_viewModel.Logs is INotifyCollectionChanged notifyLogs)
            {
                notifyLogs.CollectionChanged += Logs_CollectionChanged;
            }

            Loaded += MainWindow_Loaded;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            ApplyConnectionStateAnimations(_viewModel.CurrentState);
            ScrollLogsToBottom();
        }

        /// <summary>
        /// Safely loads app.ico in code-behind to eliminate XAML BAML resolution crashes.
        /// </summary>
        private void SafeSetWindowIcon()
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string[] candidatePaths =
                {
                    Path.Combine(baseDir, "app.ico"),
                    Path.Combine(baseDir, "assets", "app.ico"),
                    Path.Combine(baseDir, "Assets", "app.ico"),
                    Path.Combine(Environment.CurrentDirectory, "app.ico"),
                    Path.Combine(Environment.CurrentDirectory, "assets", "app.ico")
                };

                foreach (string path in candidatePaths)
                {
                    if (File.Exists(path))
                    {
                        using var stream = File.OpenRead(path);
                        Icon = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                        return;
                    }
                }
            }
            catch
            {
                // Fallback to standard window icon
            }
        }

        private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.CurrentState))
            {
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    ApplyConnectionStateAnimations(_viewModel.CurrentState);
                });
            }
        }

        /// <summary>
        /// Dynamically alters the visual pulse across Idle, Hunting, and Connected states.
        /// </summary>
        private void ApplyConnectionStateAnimations(TunnelConnectionState state)
        {
            AuraScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            AuraScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            RadarRotateTransform.BeginAnimation(RotateTransform.AngleProperty, null);

            if (state == TunnelConnectionState.Idle)
            {
                var cyanColor = (Color)FindResource("CyberCyanColor");
                AuraOuterRing.Stroke = new SolidColorBrush(cyanColor);
                AuraDropShadow.Color = cyanColor;
                RadarSweepEllipse.Visibility = Visibility.Collapsed;

                var breatheAnimation = new DoubleAnimation
                {
                    From = 0.95,
                    To = 1.08,
                    Duration = TimeSpan.FromSeconds(2.0),
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                };

                AuraScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, breatheAnimation);
                AuraScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, breatheAnimation);
            }
            else if (state == TunnelConnectionState.Hunting)
            {
                var amberColor = (Color)FindResource("AmberRadarColor");
                AuraOuterRing.Stroke = new SolidColorBrush(amberColor);
                AuraDropShadow.Color = amberColor;
                RadarSweepEllipse.Visibility = Visibility.Visible;

                var pulseAnimation = new DoubleAnimation
                {
                    From = 0.90,
                    To = 1.15,
                    Duration = TimeSpan.FromSeconds(0.7),
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                };
                AuraScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, pulseAnimation);
                AuraScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, pulseAnimation);

                var radarSweep = new DoubleAnimation
                {
                    From = 0,
                    To = 360,
                    Duration = TimeSpan.FromSeconds(1.0),
                    RepeatBehavior = RepeatBehavior.Forever
                };
                RadarRotateTransform.BeginAnimation(RotateTransform.AngleProperty, radarSweep);
            }
            else if (state == TunnelConnectionState.Connected)
            {
                var emeraldColor = (Color)FindResource("ConnectedEmeraldColor");
                AuraOuterRing.Stroke = new SolidColorBrush(emeraldColor);
                AuraDropShadow.Color = emeraldColor;
                RadarSweepEllipse.Visibility = Visibility.Collapsed;

                var connectedPulse = new DoubleAnimation
                {
                    From = 0.98,
                    To = 1.05,
                    Duration = TimeSpan.FromSeconds(1.8),
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                };
                AuraScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, connectedPulse);
                AuraScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, connectedPulse);
            }
        }

        private void Logs_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Add)
            {
                Application.Current?.Dispatcher.BeginInvoke(new Action(ScrollLogsToBottom));
            }
        }

        private void ScrollLogsToBottom()
        {
            if (TelemetryLogListBox.Items.Count > 0)
            {
                var lastItem = TelemetryLogListBox.Items[TelemetryLogListBox.Items.Count - 1];
                TelemetryLogListBox.ScrollIntoView(lastItem);
            }
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
            _onMinimizedToTray?.Invoke();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            if (!AllowClose && _onMinimizedToTray is not null)
            {
                Hide();
                _onMinimizedToTray();
                return;
            }

            Close();
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if (!AllowClose && _onMinimizedToTray is not null)
            {
                e.Cancel = true;
                Hide();
                _onMinimizedToTray();
                return;
            }

            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
            if (_viewModel.Logs is INotifyCollectionChanged notifyLogs)
            {
                notifyLogs.CollectionChanged -= Logs_CollectionChanged;
            }
            _viewModel.Dispose();
        }
    }
}
