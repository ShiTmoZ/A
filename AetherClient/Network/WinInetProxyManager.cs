using System;
using Microsoft.Win32;
using AetherClient.Core;

namespace AetherClient.Network
{
    public sealed class WinInetProxyManager
    {
        private const string InternetSettingsKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
        private const string AetherProxyAddress = "socks=127.0.0.1:1819";
        private const string DefaultProxyOverride = "localhost;127.*;10.*;172.16.*;172.17.*;172.18.*;172.19.*;172.20.*;172.21.*;172.22.*;172.23.*;172.24.*;172.25.*;172.26.*;172.27.*;172.28.*;172.29.*;172.30.*;172.31.*;192.168.*;<local>";

        private readonly Action<string> _logger;

        public WinInetProxyManager(Action<string> logger)
        {
            _logger = logger;
        }

        public void EnableProxy()
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(InternetSettingsKeyPath, writable: true);
            if (key is null)
                throw new InvalidOperationException("Unable to open HKCU Internet Settings registry key.");

            key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
            key.SetValue("ProxyServer", AetherProxyAddress, RegistryValueKind.String);
            key.SetValue("ProxyOverride", DefaultProxyOverride, RegistryValueKind.String);

            RefreshWinInet();
            NativeMethods.DnsFlushResolverCache();
            _logger("[NET-PROXY] WinINET System Proxy enabled (socks=127.0.0.1:1819).");
        }

        public void DisableProxy()
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(InternetSettingsKeyPath, writable: true);
                if (key is null)
                    return;

                string? currentServer = key.GetValue("ProxyServer") as string;
                int? currentEnable = key.GetValue("ProxyEnable") as int?;

                if (currentEnable == 1 || (currentServer?.Contains("127.0.0.1:1819", StringComparison.OrdinalIgnoreCase) ?? false))
                {
                    key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                    if (currentServer?.Contains("127.0.0.1:1819", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        key.SetValue("ProxyServer", string.Empty, RegistryValueKind.String);
                    }

                    RefreshWinInet();
                    NativeMethods.DnsFlushResolverCache();
                    _logger("[NET-PROXY] WinINET System Proxy disabled and restored.");
                }
            }
            catch (Exception ex)
            {
                _logger($"[NET-WARN] Failed to reset WinINET proxy: {ex.Message}");
            }
        }

        public static void SanitizeOrphanedProxySettings()
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(InternetSettingsKeyPath, writable: true);
                if (key is null) return;

                string? server = key.GetValue("ProxyServer") as string;
                if (server is not null && server.Contains("127.0.0.1:1819", StringComparison.OrdinalIgnoreCase))
                {
                    key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                    key.SetValue("ProxyServer", string.Empty, RegistryValueKind.String);
                    RefreshWinInet();
                }
            }
            catch { }
        }

        private static void RefreshWinInet()
        {
            NativeMethods.InternetSetOption(IntPtr.Zero, NativeMethods.INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
            NativeMethods.InternetSetOption(IntPtr.Zero, NativeMethods.INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
        }
    }
}
