using System;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using AetherClient.ViewModels;

namespace AetherClient.Views;

public partial class MainWindow : Window
{
    private readonly Action? _onHideToTray;
    private Storyboard? _idleBreathingStoryboard;
    private Storyboard? _huntingRadarStoryboard;
    private Storyboard? _connectedEqualizerStoryboard;

    public bool AllowClose { get; set; }

    public MainWindow() : this(new MainViewModel(), null)
    {
    }

    public MainWindow(MainViewModel viewModel, Action? onHideToTray = null)
    {
        InitializeComponent();
        DataContext = viewModel;
        _onHideToTray = onHideToTray;

        LoadCompanionAvatar();
        InitializeAvatarStoryboards();

        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        ApplyAvatarVisualState(viewModel.ConnectionState);

        Closed += (_, _) =>
        {
            if (DataContext is IDisposable disposable)
            {
                disposable.Dispose();
            }
        };
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

    private void InitializeAvatarStoryboards()
    {
        // 1. IDLE: Ambient Cyan Breathing Animation
        _idleBreathingStoryboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        var breatheOpacity = new DoubleAnimation
        {
            From = 0.45,
            To = 1.0,
            Duration = TimeSpan.FromSeconds(1.8),
            AutoReverse = true,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Storyboard.SetTarget(breatheOpacity, IdleCyanRing);
        Storyboard.SetTargetProperty(breatheOpacity, new PropertyPath(OpacityProperty));
        _idleBreathingStoryboard.Children.Add(breatheOpacity);

        // 2. HUNTING: 360-Degree Rotating Amber Radar Sweep
        _huntingRadarStoryboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        var radarSpin = new DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = TimeSpan.FromSeconds(2.2)
        };
        Storyboard.SetTarget(radarSpin, RadarRingRotateTransform);
        Storyboard.SetTargetProperty(radarSpin, new PropertyPath(System.Windows.Media.RotateTransform.AngleProperty));
        _huntingRadarStoryboard.Children.Add(radarSpin);

        // 3. CONNECTED: Micro-Equalizer Traffic Bars Animation
        _connectedEqualizerStoryboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        AddEqualizerBarAnimation(_connectedEqualizerStoryboard, EqBar1, 4, 13, 320);
        AddEqualizerBarAnimation(_connectedEqualizerStoryboard, EqBar2, 12, 4, 260);
        AddEqualizerBarAnimation(_connectedEqualizerStoryboard, EqBar3, 5, 14, 380);
        AddEqualizerBarAnimation(_connectedEqualizerStoryboard, EqBar4, 13, 5, 290);
    }

    private static void AddEqualizerBarAnimation(
        Storyboard sb,
        FrameworkElement targetBar,
        double fromHeight,
        double toHeight,
        int durationMs)
    {
        var anim = new DoubleAnimation
        {
            From = fromHeight,
            To = toHeight,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            AutoReverse = true,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
        };
        Storyboard.SetTarget(anim, targetBar);
        Storyboard.SetTargetProperty(anim, new PropertyPath(HeightProperty));
        sb.Children.Add(anim);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ConnectionState) && sender is MainViewModel vm)
        {
            ApplyAvatarVisualState(vm.ConnectionState);
        }
    }

    private void ApplyAvatarVisualState(CyberConnectionState state)
    {
        _idleBreathingStoryboard?.Stop();
        _huntingRadarStoryboard?.Stop();
        _connectedEqualizerStoryboard?.Stop();

        IdleCyanRing.Visibility = Visibility.Collapsed;
        HuntingAmberRadarRing.Visibility = Visibility.Collapsed;
        ConnectedEmeraldRing.Visibility = Visibility.Collapsed;
        ConnectedEqualizerBadge.Visibility = Visibility.Collapsed;

        switch (state)
        {
            case CyberConnectionState.Idle:
                IdleCyanRing.Visibility = Visibility.Visible;
                _idleBreathingStoryboard?.Begin();
                break;

            case CyberConnectionState.Hunting:
                HuntingAmberRadarRing.Visibility = Visibility.Visible;
                _huntingRadarStoryboard?.Begin();
                break;

            case CyberConnectionState.Connected:
                ConnectedEmeraldRing.Visibility = Visibility.Visible;
                ConnectedEqualizerBadge.Visibility = Visibility.Visible;
                _connectedEqualizerStoryboard?.Begin();
                break;
        }
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
