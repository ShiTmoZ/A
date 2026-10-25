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
            SafeLoadHeroCharacterImage();

            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
            if (_viewModel.Logs is INotifyCollectionChanged notifyLogs)
            {
                notifyLogs.CollectionChanged += Logs_CollectionChanged;
            }

            Loaded += MainWindow_Loaded;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            StartAmbientAnimeAnimations();
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

        /// <summary>
        /// Safely loads assets/avatar.jpg into the Hero anime stage with full resolution and memory caching.
        /// </summary>
        private void SafeLoadHeroCharacterImage()
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

                        HeroCharacterImage.Source = bitmap;
                        return;
                    }
                }
            }
            catch
            {
                // Image remains null; vector fallback remains visible
            }
        }

        /// <summary>
        /// Rotates the anime magic rune circles in opposite directions to produce the magical energy aura.
        /// </summary>
        private void StartAmbientAnimeAnimations()
        {
            try
            {
                var outerRuneSpin = new DoubleAnimation
                {
                    From = 0,
                    To = 360,
                    Duration = TimeSpan.FromSeconds(40),
                    RepeatBehavior = RepeatBehavior.Forever
                };
                HeroRuneRotateTransform.BeginAnimation(RotateTransform.AngleProperty, outerRuneSpin);

                var innerRuneSpin = new DoubleAnimation
                {
                    From = 0,
                    To = -360,
                    Duration = TimeSpan.FromSeconds(25),
                    RepeatBehavior = RepeatBehavior.Forever
                };
                HeroInnerRuneRotateTransform.BeginAnimation(RotateTransform.AngleProperty, innerRuneSpin);
            }
            catch
            {
                // Silently ignore if animations fail on low-power devices
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
        /// Dynamically alters the visual pulse and aura across Idle, Hunting, and Connected states.
        /// </summary>
        private void ApplyConnectionStateAnimations(TunnelConnectionState state)
        {
            AuraScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            AuraScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            RadarRotateTransform.BeginAnimation(RotateTransform.AngleProperty, null);
            StopEqualizerAnimations();

            if (state == TunnelConnectionState.Idle)
            {
                // IDLE: Soft cyan mana breathing
                var tealColor = (Color)FindResource("TealManaColor");
                AuraOuterRing.Stroke = new SolidColorBrush(tealColor);
                AuraDropShadow.Color = (Color)FindResource("CyanGlowColor");

                var breatheAnimation = new DoubleAnimation
                {
                    From = 0.95,
                    To = 1.08,
                    Duration = TimeSpan.FromSeconds(2.2),
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                };

                AuraScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, breatheAnimation);
                AuraScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, breatheAnimation);
            }
            else if (state == TunnelConnectionState.Hunting)
            {
                // HUNTING: Amber radar pulse + high-speed radar sweep
                var amberColor = (Color)FindResource("AmberWarmColor");
                AuraOuterRing.Stroke = new SolidColorBrush(amberColor);
                AuraDropShadow.Color = amberColor;

                var pulseAnimation = new DoubleAnimation
                {
                    From = 0.92,
                    To = 1.12,
                    Duration = TimeSpan.FromSeconds(0.8),
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
                    Duration = TimeSpan.FromSeconds(1.2),
                    RepeatBehavior = RepeatBehavior.Forever
                };
                RadarRotateTransform.BeginAnimation(RotateTransform.AngleProperty, radarSweep);
            }
            else if (state == TunnelConnectionState.Connected)
            {
                // CONNECTED: Radiant emerald mana shield + dancing micro-equalizer audio bars
                var emeraldColor = (Color)FindResource("ConnectedEmeraldColor");
                AuraOuterRing.Stroke = new SolidColorBrush(emeraldColor);
                AuraDropShadow.Color = emeraldColor;

                var connectedPulse = new DoubleAnimation
                {
                    From = 0.98,
                    To = 1.05,
                    Duration = TimeSpan.FromSeconds(1.6),
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
            AnimateEqualizerBar(EqBar1, 3, 14, 380);
            AnimateEqualizerBar(EqBar2, 5, 16, 280);
            AnimateEqualizerBar(EqBar3, 2, 12, 420);
            AnimateEqualizerBar(EqBar4, 6, 16, 310);
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
