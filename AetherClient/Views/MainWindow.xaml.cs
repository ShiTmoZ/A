using System.IO;
using System.Windows.Media.Imaging;
using System;
using System.ComponentModel;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using AetherClient.Core;

namespace AetherClient.Views
{
    public partial class MainWindow : Window
    {
        private readonly AetherProcessService _processService;
        private readonly StringBuilder _plainLogBuffer = new(16384);
        private Storyboard? _radarSpinStoryboard;
        private Storyboard? _pulseRingStoryboard;
        private int _eventCount;
        private readonly Action? _onMinimizedToTray;

        public bool AllowClose { get; set; }
        public AetherProcessService ProcessService => _processService;

        // Syntax-Highlighted Brushes for Live Glass Terminal Telemetry
        private static readonly SolidColorBrush BrushTimestamp = CreateFrozenBrush("#637394");
        private static readonly SolidColorBrush BrushMessage = CreateFrozenBrush("#E2ECFF");
        private static readonly SolidColorBrush BrushCoreTag = CreateFrozenBrush("#00E5FF");
        private static readonly SolidColorBrush BrushHuntTag = CreateFrozenBrush("#FFB300");
        private static readonly SolidColorBrush BrushTunTag = CreateFrozenBrush("#00E676");
        private static readonly SolidColorBrush BrushExitTag = CreateFrozenBrush("#C084FC");
        private static readonly SolidColorBrush BrushErrorTag = CreateFrozenBrush("#FF5252");

        // Dynamic State Colors
        private static readonly Color ColorCyan = (Color)ColorConverter.ConvertFromString("#00E5FF");
        private static readonly Color ColorAmber = (Color)ColorConverter.ConvertFromString("#FF9100");
        private static readonly Color ColorEmerald = (Color)ColorConverter.ConvertFromString("#00E676");
        private static readonly Color ColorCrimson = (Color)ColorConverter.ConvertFromString("#FF1744");

        public MainWindow() : this(null)
        {
        }

        public MainWindow(Action? onMinimizedToTray = null)
        {
            InitializeComponent();

            _onMinimizedToTray = onMinimizedToTray;
            _processService = new AetherProcessService();
            _processService.LogEmitted += OnServiceLogEmitted;
            _processService.TelemetryUpdated += OnServiceTelemetryUpdated;

            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
        }

        private static SolidColorBrush CreateFrozenBrush(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }

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
            catch { }
        }

        public async Task ToggleConnectionAsync()
        {
            if (_processService.CurrentState == ConnectionState.Standby)
            {
                string? customPeer = CustomPeerTextBox.Text.Trim();
                await _processService.StartAsync(
                    forceFreshHunt: false,
                    customPeer: string.IsNullOrWhiteSpace(customPeer) ? null : customPeer);
            }
            else
            {
                await _processService.StopAsync();
            }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            SafeSetWindowIcon();
            InitializeContinuousAnimations();
            ApplyReactorVisualState(ConnectionState.Standby);

            AppendTerminalLog(new LogEntry(
                DateTime.Now,
                LogCategory.CORE,
                "AetherClient Liquid Glass & Floating Cyber Edition initialized (v2.4.0 backend discipline enforced)."));
            AppendTerminalLog(new LogEntry(
                DateTime.Now,
                LogCategory.CORE,
                "Local proxy endpoints armed -> SOCKS5 127.0.0.1:1819 | HTTP 127.0.0.1:1820"));
        }

        private async void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            if (!AllowClose && _onMinimizedToTray is not null)
            {
                e.Cancel = true;
                Hide();
                _onMinimizedToTray();
                return;
            }

            _processService.LogEmitted -= OnServiceLogEmitted;
            _processService.TelemetryUpdated -= OnServiceTelemetryUpdated;
            await _processService.DisposeAsync();
        }

        // =========================================================================
        // CONTINUOUS STORYBOARDS (RADAR SWEEP & BREATHING REACTOR PULSE)
        // =========================================================================

        private void InitializeContinuousAnimations()
        {
            // 1. Radar Sweep Rotation (0 -> 360 deg loop)
            _radarSpinStoryboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
            var spinAnim = new DoubleAnimation
            {
                From = 0,
                To = 360,
                Duration = TimeSpan.FromSeconds(1.85)
            };
            Storyboard.SetTarget(spinAnim, ReactorRadarRing);
            Storyboard.SetTargetProperty(spinAnim, new PropertyPath("(UIElement.RenderTransform).(RotateTransform.Angle)"));
            _radarSpinStoryboard.Children.Add(spinAnim);

            // 2. Concentric Energy Pulse & LED Halo Breathing Loop
            _pulseRingStoryboard = new Storyboard
            {
                RepeatBehavior = RepeatBehavior.Forever,
                AutoReverse = true
            };

            var scaleXAnim = new DoubleAnimation(0.92, 1.06, TimeSpan.FromSeconds(1.6))
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            var scaleYAnim = new DoubleAnimation(0.92, 1.06, TimeSpan.FromSeconds(1.6))
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            var opacityAnim = new DoubleAnimation(0.22, 0.65, TimeSpan.FromSeconds(1.6))
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };

            Storyboard.SetTarget(scaleXAnim, ReactorPulseRing);
            Storyboard.SetTargetProperty(scaleXAnim, new PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleX)"));
            Storyboard.SetTarget(scaleYAnim, ReactorPulseRing);
            Storyboard.SetTargetProperty(scaleYAnim, new PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleY)"));
            Storyboard.SetTarget(opacityAnim, ReactorPulseRing);
            Storyboard.SetTargetProperty(opacityAnim, new PropertyPath(UIElement.OpacityProperty));

            var ledScaleX = new DoubleAnimation(0.85, 1.45, TimeSpan.FromSeconds(1.1))
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            var ledScaleY = new DoubleAnimation(0.85, 1.45, TimeSpan.FromSeconds(1.1))
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            Storyboard.SetTarget(ledScaleX, StatusLedHalo);
            Storyboard.SetTargetProperty(ledScaleX, new PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleX)"));
            Storyboard.SetTarget(ledScaleY, StatusLedHalo);
            Storyboard.SetTargetProperty(ledScaleY, new PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleY)"));

            _pulseRingStoryboard.Children.Add(scaleXAnim);
            _pulseRingStoryboard.Children.Add(scaleYAnim);
            _pulseRingStoryboard.Children.Add(opacityAnim);
            _pulseRingStoryboard.Children.Add(ledScaleX);
            _pulseRingStoryboard.Children.Add(ledScaleY);
            _pulseRingStoryboard.Begin(this, true);
        }

        // =========================================================================
        // LIQUID REACTOR CORE VISUAL STATE MACHINE
        // =========================================================================

        private void ApplyReactorVisualState(ConnectionState state)
        {
            ReactorCoreButton.ApplyTemplate();

            var innerGlowStop = ReactorCoreButton.Template.FindName("CoreGlowInnerStop", ReactorCoreButton) as GradientStop;
            var dropGlow = ReactorCoreButton.Template.FindName("ReactorDropGlow", ReactorCoreButton) as DropShadowEffect;
            var pillDot = ReactorCoreButton.Template.FindName("ReactorPillDot", ReactorCoreButton) as Ellipse;
            var stateBadge = ReactorCoreButton.Template.FindName("ReactorStateBadgeText", ReactorCoreButton) as TextBlock;
            var actionLabel = ReactorCoreButton.Template.FindName("ReactorActionLabel", ReactorCoreButton) as TextBlock;
            var subLabel = ReactorCoreButton.Template.FindName("ReactorSubLabel", ReactorCoreButton) as TextBlock;
            var vectorIcon = ReactorCoreButton.Template.FindName("ReactorVectorIcon", ReactorCoreButton) as Path;

            Color targetColor;
            SolidColorBrush targetBrush;
            string badgeText;
            string buttonTitle;
            string buttonSubtitle;
            bool showRadarSweep = false;

            switch (state)
            {
                case ConnectionState.Standby:
                    targetColor = ColorCyan;
                    targetBrush = (SolidColorBrush)FindResource("CyanAccentBrush");
                    badgeText = "REACTOR CORE // STANDBY";
                    buttonTitle = "ENGAGE MASQUE LINK";
                    buttonSubtitle = "Click to initialize HTTP/2 Anycast Turbo Tunnel";
                    DnsGuardBadgeText.Text = "DNS GUARD: STANDBY";
                    DnsGuardBadgeText.Foreground = (SolidColorBrush)FindResource("TextSecondaryBrush");
                    break;

                case ConnectionState.Hunting:
                    targetColor = ColorAmber;
                    targetBrush = (SolidColorBrush)FindResource("AmberAccentBrush");
                    badgeText = "MESH SCANNER // ACTIVE";
                    buttonTitle = "HUNTING ANYCAST MESH...";
                    buttonSubtitle = "Probing 2079 Cloudflare Anycast edges (Click to abort)";
                    DnsGuardBadgeText.Text = "DNS GUARD: LOCKING 1.1.1.1";
                    DnsGuardBadgeText.Foreground = targetBrush;
                    showRadarSweep = true;
                    break;

                case ConnectionState.Connected:
                    targetColor = ColorEmerald;
                    targetBrush = (SolidColorBrush)FindResource("EmeraldAccentBrush");
                    badgeText = "SHIELDED // MASQUE H2 TUNNEL";
                    buttonTitle = "SEVER MASQUE TUNNEL";
                    buttonSubtitle = "Encrypted HTTP/2 tunnel active • Click to sever link";
                    DnsGuardBadgeText.Text = "DNS LOCKED: 1.1.1.1";
                    DnsGuardBadgeText.Foreground = targetBrush;
                    break;

                case ConnectionState.Teardown:
                default:
                    targetColor = ColorCrimson;
                    targetBrush = (SolidColorBrush)FindResource("CrimsonAccentBrush");
                    badgeText = "REACTOR CORE // TEARDOWN";
                    buttonTitle = "SEVERING LINK...";
                    buttonSubtitle = "Restoring adapter DNS & terminating child processes";
                    DnsGuardBadgeText.Text = "DNS GUARD: REVERTING DHCP";
                    DnsGuardBadgeText.Foreground = targetBrush;
                    break;
            }

            // Update Reactor Core Template Parts
            if (innerGlowStop != null)
            {
                innerGlowStop.Color = Color.FromArgb(0x58, targetColor.R, targetColor.G, targetColor.B);
            }
            if (dropGlow != null)
            {
                dropGlow.Color = targetColor;
            }
            if (pillDot != null) pillDot.Fill = targetBrush;
            if (stateBadge != null)
            {
                stateBadge.Text = badgeText;
                stateBadge.Foreground = targetBrush;
            }
            if (actionLabel != null) actionLabel.Text = buttonTitle;
            if (subLabel != null) subLabel.Text = buttonSubtitle;
            if (vectorIcon != null) vectorIcon.Fill = targetBrush;

            // Update Outer Rings & Upper Deck Status LED
            ReactorPulseRing.Stroke = targetBrush;
            StatusLedCore.Fill = targetBrush;
            StatusLedHalo.Fill = targetBrush;
            HudStatusText.Foreground = targetBrush;

            // Toggle Rotating Radar Sweep Ring
            if (showRadarSweep)
            {
                ReactorRadarRing.BeginAnimation(UIElement.OpacityProperty,
                    new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(240)));
                _radarSpinStoryboard?.Begin(this, true);
            }
            else
            {
                ReactorRadarRing.BeginAnimation(UIElement.OpacityProperty,
                    new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(240)));
                _radarSpinStoryboard?.Stop(this);
            }
        }

        // =========================================================================
        // TELEMETRY & LOGGING EVENT HANDLERS
        // =========================================================================

        private void OnServiceTelemetryUpdated(object? sender, TelemetrySnapshot snapshot)
        {
            Dispatcher.InvokeAsync(() =>
            {
                ApplyReactorVisualState(snapshot.State);

                // 1. STATUS Capsule
                HudStatusText.Text = snapshot.StatusText;
                HudStatusSubText.Text = snapshot.State switch
                {
                    ConnectionState.Standby => "Awaiting MASQUE H2 link...",
                    ConnectionState.Hunting => "Scanning Cloudflare Anycast mesh...",
                    ConnectionState.Connected => $"Routing Mode: {(_processService.CurrentRoutingMode == RoutingMode.TrueTun ? "True TUN (Wintun L3)" : "System Proxy (:1820)")}",
                    ConnectionState.Teardown => "Releasing system sockets & routes...",
                    _ => string.Empty
                };

                // 2. EDGE PEER Capsule
                HudEdgePeerText.Text = string.IsNullOrWhiteSpace(snapshot.EdgePeer)
                    ? "---.---.---.---:---"
                    : snapshot.EdgePeer;

                // 3. EXIT POP Capsule
                HudExitPopText.Text = string.IsNullOrWhiteSpace(snapshot.ExitPop)
                    ? "---"
                    : snapshot.ExitPop.ToUpperInvariant();
                HudExitCountryText.Text = string.IsNullOrWhiteSpace(snapshot.ExitCountry)
                    ? "IATA"
                    : snapshot.ExitCountry.ToUpperInvariant();

                // 4. REAL-TIME RTT Capsule with Dynamic Color Grading (<180ms Emerald, 180-280ms Cyan, >280ms Amber)
                if (snapshot.LatencyMs.HasValue && snapshot.LatencyMs.Value > 0)
                {
                    int rtt = snapshot.LatencyMs.Value;
                    HudRttText.Text = $"{rtt} ms";

                    if (rtt < 180)
                    {
                        var emerald = (SolidColorBrush)FindResource("EmeraldAccentBrush");
                        HudRttText.Foreground = emerald;
                        HudRttGradeBadge.Text = "OPTIMAL";
                        HudRttGradeBadge.Foreground = emerald;
                    }
                    else if (rtt <= 280)
                    {
                        var cyan = (SolidColorBrush)FindResource("CyanAccentBrush");
                        HudRttText.Foreground = cyan;
                        HudRttGradeBadge.Text = "NOMINAL";
                        HudRttGradeBadge.Foreground = cyan;
                    }
                    else
                    {
                        var amber = (SolidColorBrush)FindResource("AmberAccentBrush");
                        HudRttText.Foreground = amber;
                        HudRttGradeBadge.Text = "ELEVATED";
                        HudRttGradeBadge.Foreground = amber;
                    }
                }
                else
                {
                    HudRttText.Text = "-- ms";
                    HudRttText.Foreground = (SolidColorBrush)FindResource("CyanAccentBrush");
                    HudRttGradeBadge.Text = "IDLE";
                    HudRttGradeBadge.Foreground = (SolidColorBrush)FindResource("TextMutedBrush");
                }
            });
        }

        private void OnServiceLogEmitted(object? sender, LogEntry entry)
        {
            Dispatcher.InvokeAsync(() => AppendTerminalLog(entry));
        }

        private void AppendTerminalLog(LogEntry entry)
        {
            _eventCount++;
            EventCounterBadgeText.Text = $"{_eventCount} EVENTS";

            string timestampStr = $"[{entry.Timestamp:HH:mm:ss.fff}] ";
            string tagStr = entry.Category switch
            {
                LogCategory.CORE => "[CORE] ",
                LogCategory.HUNT => "[HUNT] ",
                LogCategory.TUN  => "[TUN]  ",
                LogCategory.EXIT => "[EXIT] ",
                LogCategory.WARN => "[WARN] ",
                LogCategory.ERR  => "[ERR]  ",
                _                => "[CORE] "
            };

            SolidColorBrush tagBrush = entry.Category switch
            {
                LogCategory.CORE => BrushCoreTag,
                LogCategory.HUNT => BrushHuntTag,
                LogCategory.TUN  => BrushTunTag,
                LogCategory.EXIT => BrushExitTag,
                LogCategory.WARN => BrushErrorTag,
                LogCategory.ERR  => BrushErrorTag,
                _                => BrushCoreTag
            };

            _plainLogBuffer.Append(timestampStr).Append(tagStr).AppendLine(entry.Message);

            var paragraph = new Paragraph();
            paragraph.Inlines.Add(new Run(timestampStr) { Foreground = BrushTimestamp });
            paragraph.Inlines.Add(new Run(tagStr) { Foreground = tagBrush, FontWeight = FontWeights.Bold });
            paragraph.Inlines.Add(new Run(entry.Message)
            {
                Foreground = (entry.Category == LogCategory.WARN || entry.Category == LogCategory.ERR)
                    ? BrushErrorTag
                    : BrushMessage
            });

            TelemetryFlowDoc.Blocks.Add(paragraph);

            // Cap FlowDocument blocks to 400 lines for zero UI memory bloat
            while (TelemetryFlowDoc.Blocks.Count > 400)
            {
                TelemetryFlowDoc.Blocks.Remove(TelemetryFlowDoc.Blocks.FirstBlock);
            }

            TelemetryTerminalBox.ScrollToEnd();
        }

        // =========================================================================
        // USER INTERACTION HANDLERS
        // =========================================================================

        private async void ReactorCoreButton_Click(object sender, RoutedEventArgs e)
        {
            if (_processService.CurrentState == ConnectionState.Standby)
            {
                string? customPeer = CustomPeerTextBox.Text.Trim();
                await _processService.StartAsync(
                    forceFreshHunt: false,
                    customPeer: string.IsNullOrWhiteSpace(customPeer) ? null : customPeer);
            }
            else
            {
                await _processService.StopAsync();
            }
        }

        private async void HuntNextEdgeButton_Click(object sender, RoutedEventArgs e)
        {
            // Triggers a fresh turbo mesh hunt (--no-quick-reconnect)
            CustomPeerTextBox.Text = string.Empty;
            await _processService.HuntNextEdgeAsync();
        }

        private async void PingTestButton_Click(object sender, RoutedEventArgs e)
        {
            PingTestButton.IsEnabled = false;
            try
            {
                await _processService.MeasureSocks5RttAsync();
            }
            finally
            {
                PingTestButton.IsEnabled = true;
            }
        }

        private async void SystemProxyModeBtn_Click(object sender, RoutedEventArgs e)
        {
            AnimateSegmentedSwitch(isTrueTun: false);
            await _processService.SetRoutingModeAsync(RoutingMode.SystemProxy);
        }

        private async void TrueTunModeBtn_Click(object sender, RoutedEventArgs e)
        {
            AnimateSegmentedSwitch(isTrueTun: true);
            await _processService.SetRoutingModeAsync(RoutingMode.TrueTun);
        }

        private void AnimateSegmentedSwitch(bool isTrueTun)
        {
            double targetX = isTrueTun ? SystemProxyModeBtn.ActualWidth : 0;

            var slideAnim = new DoubleAnimation
            {
                To = targetX,
                Duration = TimeSpan.FromMilliseconds(220),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            SegmentedPillTranslate.BeginAnimation(TranslateTransform.XProperty, slideAnim);

            var proxyLabel = SystemProxyModeBtn.Template.FindName("ProxyModeLabel", SystemProxyModeBtn) as TextBlock;
            var tunLabel = TrueTunModeBtn.Template.FindName("TunModeLabel", TrueTunModeBtn) as TextBlock;

            if (proxyLabel != null)
            {
                proxyLabel.Foreground = (SolidColorBrush)FindResource(isTrueTun ? "TextSecondaryBrush" : "TextPrimaryBrush");
            }
            if (tunLabel != null)
            {
                tunLabel.Foreground = (SolidColorBrush)FindResource(isTrueTun ? "TextPrimaryBrush" : "TextSecondaryBrush");
            }
        }

        private void CustomPeerTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            string peer = CustomPeerTextBox.Text.Trim();
            CustomPeerPlaceholder.Visibility = string.IsNullOrEmpty(peer)
                ? Visibility.Visible
                : Visibility.Collapsed;

            ActiveCliPreviewText.Text = string.IsNullOrEmpty(peer)
                ? "--masque --h2 -4 --turbo --quick-reconnect --http-proxy 127.0.0.1:1820"
                : $"--masque --h2 -4 --peer {peer} --quick-reconnect --http-proxy 127.0.0.1:1820";
        }

        private void ClearCustomPeer_Click(object sender, RoutedEventArgs e)
        {
            CustomPeerTextBox.Text = string.Empty;
        }

        private async void CopyLogsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(_plainLogBuffer.ToString());
                CopyLogsButtonText.Text = "Copied! ✓";
                CopyLogsButtonText.Foreground = (SolidColorBrush)FindResource("EmeraldAccentBrush");

                await Task.Delay(1800);

                CopyLogsButtonText.Text = "Copy Logs";
                CopyLogsButtonText.Foreground = (SolidColorBrush)FindResource("TextPrimaryBrush");
            }
            catch (Exception ex)
            {
                AppendTerminalLog(new LogEntry(DateTime.Now, LogCategory.WARN, $"Clipboard access busy: {ex.Message}"));
            }
        }

        private void ClearLogsButton_Click(object sender, RoutedEventArgs e)
        {
            TelemetryFlowDoc.Blocks.Clear();
            _plainLogBuffer.Clear();
            _eventCount = 0;
            EventCounterBadgeText.Text = "0 EVENTS";
        }

        // =========================================================================
        // CUSTOM WINDOW CHROME HANDLERS
        // =========================================================================

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                MaximizeButton_Click(sender, e);
                return;
            }

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

        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
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
    }
}
