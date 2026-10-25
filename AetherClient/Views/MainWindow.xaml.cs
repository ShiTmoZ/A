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
            SafeLoadAvatarImage();

            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
            if (_viewModel.Logs is INotifyCollectionChanged notifyLogs)
            {
                notifyLogs.CollectionChanged += Logs_CollectionChanged;
            }

            Loaded += MainWindow_Loaded;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            StartAmbientOrbitalAnimation();
            ApplyConnectionStateAnimations(_viewModel.CurrentState);
            ScrollLogsToBottom();
        }

        /// <summary>
        /// Safely loads app.ico in code-behind without risking a XAML BAML parser crash if missing.
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
                // Keep default system icon if app.ico is unavailable or invalid.
            }
        }

        /// <summary>
        /// Safely loads assets/avatar.jpg into the circular HUD container while preserving
        /// the stylized geometric elven silhouette fallback if the file is absent.
        /// </summary>
        private void SafeLoadAvatarImage()
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string[] candidatePaths =
                {
                    Path.Combine(baseDir, "assets", "avatar.jpg"),
                    Path.Combine(baseDir, "Assets", "avatar.jpg"),
                    Path.Combine(baseDir, "assets", "avatar.png"),
                    Path.Combine(Environment.CurrentDirectory, "assets", "avatar.jpg"),
                    Path.Combine(Environment.CurrentDirectory, "Assets", "avatar.jpg")
                };

                foreach (string path in candidatePaths)
                {
                    if (File.Exists(path))
                    {
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.UriSource = new Uri(path, UriKind.Absolute);
                        bitmap.EndInit();
                        bitmap.Freeze();

                        AvatarImageBrush.ImageSource = bitmap;
                        AvatarImageContainer.Visibility = Visibility.Visible;
                        return;
                    }
                }
            }
            catch
            {
                AvatarImageContainer.Visibility = Visibility.Collapsed;
            }
        }

        private void StartAmbientOrbitalAnimation()
        {
            var orbitalSpin = new DoubleAnimation
            {
                From = 0,
                To = -360,
                Duration = TimeSpan.FromSeconds(28),
                RepeatBehavior = RepeatBehavior.Forever
            };
            OrbitalRotateTransform.BeginAnimation(RotateTransform.AngleProperty, orbitalSpin);
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
        /// Drives hardware-accelerated UIElement.BeginAnimation transitions across IDLE, HUNTING, and CONNECTED states.
        /// </summary>
        private void ApplyConnectionStateAnimations(TunnelConnectionState state)
        {
            // Clear existing dynamic animations before applying new state profile
            AuraScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            AuraScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            RadarRotateTransform.BeginAnimation(RotateTransform.AngleProperty, null);
            StopEqualizerAnimations();

            if (state == TunnelConnectionState.Idle)
            {
                // IDLE: Soft breathing aura using Marcille's Primary Cloak Teal/Cyan (#22B8CF)
                var cyanColor = (Color)FindResource("PrimaryCyanGlowColor");
                AuraOuterRing.Stroke = new SolidColorBrush(cyanColor);
                OrbitalDashedRing.Stroke = new SolidColorBrush(cyanColor);
                AuraDropShadow.Color = cyanColor;

                var breatheAnimation = new DoubleAnimation
                {
                    From = 0.96,
                    To = 1.04,
                    Duration = TimeSpan.FromSeconds(2.4),
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                };

                AuraScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, breatheAnimation);
                AuraScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, breatheAnimation);
            }
            else if (state == TunnelConnectionState.Hunting)
            {
                // HUNTING: Rotating radar sweep using Marcille's Golden Blonde Highlight (#F5A623)
                var goldColor = (Color)FindResource("SecondaryGoldColor");
                AuraOuterRing.Stroke = new SolidColorBrush(goldColor);
                OrbitalDashedRing.Stroke = new SolidColorBrush(goldColor);
                AuraDropShadow.Color = goldColor;

                var pulseAnimation = new DoubleAnimation
                {
                    From = 0.94,
                    To = 1.06,
                    Duration = TimeSpan.FromSeconds(0.9),
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                };
                AuraScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, pulseAnimation);
                AuraScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, pulseAnimation);

                var radarSweepAnimation = new DoubleAnimation
                {
                    From = 0,
                    To = 360,
                    Duration = TimeSpan.FromSeconds(1.35),
                    RepeatBehavior = RepeatBehavior.Forever
                };
                RadarRotateTransform.BeginAnimation(RotateTransform.AngleProperty, radarSweepAnimation);
            }
            else if (state == TunnelConnectionState.Connected)
            {
                // CONNECTED: Vibrant Mana Emerald aura (#4ADE80) + animated micro equalizer telemetry bars
                var emeraldColor = (Color)FindResource("ConnectedEmeraldColor");
                AuraOuterRing.Stroke = new SolidColorBrush(emeraldColor);
                OrbitalDashedRing.Stroke = new SolidColorBrush(emeraldColor);
                AuraDropShadow.Color = emeraldColor;

                var connectedPulse = new DoubleAnimation
                {
                    From = 0.98,
                    To = 1.03,
                    Duration = TimeSpan.FromSeconds(1.8),
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                };
                AuraScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, connectedPulse);
                AuraScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, connectedPulse);

                StartEqualizerAnimations();
            }
        }

        private void StartEqualizerAnimations()
        {
            AnimateEqualizerBar(EqBar1, 4, 13, 380);
            AnimateEqualizerBar(EqBar2, 5, 14, 290);
            AnimateEqualizerBar(EqBar3, 3, 12, 430);
            AnimateEqualizerBar(EqBar4, 6, 14, 320);
            AnimateEqualizerBar(EqBar5, 4, 11, 360);
        }

        private static void AnimateEqualizerBar(FrameworkElement bar, double minHeight, double maxHeight, int durationMs)
        {
            var heightAnim = new DoubleAnimation
            {
                From = minHeight,
                To = maxHeight,
                Duration = TimeSpan.FromMilliseconds(durationMs),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            bar.BeginAnimation(HeightProperty, heightAnim);
        }

        private void StopEqualizerAnimations()
        {
            EqBar1?.BeginAnimation(HeightProperty, null);
            EqBar2?.BeginAnimation(HeightProperty, null);
            EqBar3?.BeginAnimation(HeightProperty, null);
            EqBar4?.BeginAnimation(HeightProperty, null);
            EqBar5?.BeginAnimation(HeightProperty, null);
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
