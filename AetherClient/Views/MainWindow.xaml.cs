using System;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using AetherClient.ViewModels;

namespace AetherClient.Views;

public partial class MainWindow : Window
{
    private readonly Action? _onHideToTray;
    public bool AllowClose { get; set; }

    public MainWindow() : this(new MainViewModel(), null)
    {
    }

    public MainWindow(MainViewModel viewModel, Action? onHideToTray = null)
    {
        InitializeComponent();
        DataContext = viewModel;
        _onHideToTray = onHideToTray;

        SafeSetWindowIcon();
        LoadCompanionAvatar();

        viewModel.PropertyChanged += OnViewModelPropertyChanged;

        Loaded += (_, _) =>
        {
            ApplyAvatarVisualState(viewModel.ConnectionState);
        };

        Closed += (_, _) =>
        {
            if (DataContext is IDisposable disposable)
            {
                disposable.Dispose();
            }
        };
    }

    private void SafeSetWindowIcon()
    {
        try
        {
            string diskIconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
            if (File.Exists(diskIconPath))
            {
                Icon = BitmapFrame.Create(new Uri(diskIconPath, UriKind.Absolute));
                return;
            }

            var packUri = new Uri("pack://application:,,,/app.ico", UriKind.Absolute);
            Icon = BitmapFrame.Create(packUri);
        }
        catch
        {
            // Ignore icon failure to allow window to open regardless
        }
    }

    private void LoadCompanionAvatar()
    {
        try
        {
            string localAvatarPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "avatar.jpg");
            if (File.Exists(localAvatarPath))
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(localAvatarPath, UriKind.Absolute);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();
                CompanionAvatarImage.Source = bitmap;
                return;
            }

            // Fallback to pack URI if compiled as Resource
            var packUri = new Uri("pack://application:,,,/assets/avatar.jpg", UriKind.Absolute);
            var packBitmap = new BitmapImage();
            packBitmap.BeginInit();
            packBitmap.UriSource = packUri;
            packBitmap.CacheOption = BitmapCacheOption.OnLoad;
            packBitmap.EndInit();
            packBitmap.Freeze();
            CompanionAvatarImage.Source = packBitmap;
        }
        catch
        {
            // Silently fall back to the vector Cyber-Elf silhouette underneath CompanionAvatarImage
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ConnectionState) && sender is MainViewModel vm)
        {
            Dispatcher.Invoke(() => ApplyAvatarVisualState(vm.ConnectionState));
        }
    }

    private void StopAllAnimations()
    {
        try
        {
            IdleCyanRing.BeginAnimation(OpacityProperty, null);
            RadarRingRotateTransform.BeginAnimation(RotateTransform.AngleProperty, null);
            EqBar1.BeginAnimation(HeightProperty, null);
            EqBar2.BeginAnimation(HeightProperty, null);
            EqBar3.BeginAnimation(HeightProperty, null);
            EqBar4.BeginAnimation(HeightProperty, null);
        }
        catch { }
    }

    private void ApplyAvatarVisualState(CyberConnectionState state)
    {
        try
        {
            StopAllAnimations();

            IdleCyanRing.Visibility = Visibility.Collapsed;
            HuntingAmberRadarRing.Visibility = Visibility.Collapsed;
            ConnectedEmeraldRing.Visibility = Visibility.Collapsed;
            ConnectedEqualizerBadge.Visibility = Visibility.Collapsed;

            switch (state)
            {
                case CyberConnectionState.Idle:
                    IdleCyanRing.Visibility = Visibility.Visible;
                    var breatheAnim = new DoubleAnimation
                    {
                        From = 0.45,
                        To = 1.0,
                        Duration = TimeSpan.FromSeconds(1.8),
                        AutoReverse = true,
                        RepeatBehavior = RepeatBehavior.Forever,
                        EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                    };
                    IdleCyanRing.BeginAnimation(OpacityProperty, breatheAnim);
                    break;

                case CyberConnectionState.Hunting:
                    HuntingAmberRadarRing.Visibility = Visibility.Visible;
                    var radarSpin = new DoubleAnimation
                    {
                        From = 0,
                        To = 360,
                        Duration = TimeSpan.FromSeconds(2.2),
                        RepeatBehavior = RepeatBehavior.Forever
                    };
                    RadarRingRotateTransform.BeginAnimation(RotateTransform.AngleProperty, radarSpin);
                    break;

                case CyberConnectionState.Connected:
                    ConnectedEmeraldRing.Visibility = Visibility.Visible;
                    ConnectedEqualizerBadge.Visibility = Visibility.Visible;

                    AnimateBar(EqBar1, 4, 13, 320);
                    AnimateBar(EqBar2, 12, 4, 260);
                    AnimateBar(EqBar3, 5, 14, 380);
                    AnimateBar(EqBar4, 13, 5, 290);
                    break;
            }
        }
        catch
        {
            // Animation failure must never crash the UI
        }
    }

    private static void AnimateBar(FrameworkElement bar, double from, double to, int durationMs)
    {
        try
        {
            var anim = new DoubleAnimation
            {
                From = from,
                To = to,
                Duration = TimeSpan.FromMilliseconds(durationMs),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
            };
            bar.BeginAnimation(HeightProperty, anim);
        }
        catch { }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (!AllowClose && _onHideToTray is not null)
        {
            Hide();
            _onHideToTray();
            return;
        }

        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose && _onHideToTray is not null)
        {
            e.Cancel = true;
            Hide();
            _onHideToTray();
            return;
        }

        base.OnClosing(e);
    }
}
