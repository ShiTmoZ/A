# Aether Client for Windows (x64)

Zero-bloat, production-grade Windows Desktop Client for **Cloudflare MASQUE (RFC 9298) HTTP/2** censorship bypass, engineered specifically for high-censorship environments (such as Iran TCI / MCI / Irancell).

![Platform](https://img.shields.io/badge/Platform-Windows%2011%20%7C%2010%20(x64)-blue)
![Framework](https://img.shields.io/badge/.NET-8.0%20WPF-purple)
![Architecture](https://img.shields.io/badge/Architecture-Clean%20MVVM%20%2B%20Kernel%20Job%20Objects-green)

---

## ⚡ Key Highlights

1. **Kernel Job Objects (Zero-Orphan Process Lifecycle)**:
   - Both `aether.exe` and `tun2socks.exe` are bound to a Windows Kernel Job Object (`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`).
   - Even on abrupt termination (`Task Manager -> End Task`), the Windows kernel automatically terminates child processes and tears down the virtual adapter.

2. **Dual-Mode System Tunneling**:
   - **Mode A (System Proxy)**: Fast WinINET API toggle (`HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings`) routing browsers (Brave, Chrome, Edge) and standard download clients to `socks=127.0.0.1:1819`.
   - **Mode B (True TUN Adapter)**: Uses official `wintun.dll` and `tun2socks.exe` to provision the `Aether-Tun` interface (`10.0.85.2/24`). Automatically resolves the physical Default Gateway and installs `/32` bypass routes for MASQUE endpoints, ensuring zero routing loops while directing `0.0.0.0/0` into `127.0.0.1:1819` for all system processes and games.

3. **Production Fluent Dark UI**:
   - Modern dark aesthetic (`#0F1117` base, `#38BDF8` sky blue accent).
   - Real-time connection badge (`DISCONNECTED`, `HUNTING`, `CONNECTED`).
   - Dynamic vector System Tray icon reflecting live state.
   - Live telemetry readout parsing exit IP, route, and latency.
   - Collapsible live diagnostic terminal with search/copy support.

4. **Bundled Provisioned Identity**:
   - Ships with valid `aether-masque.toml` pre-provisioned identity, bypassing registration TLS timeouts in restricted networks.

---

## 🚀 Quick Start

1. Download the latest `AetherClient-Windows-x64.zip` from [Releases](https://github.com/ShiTmoZ/A/releases).
2. Extract the archive to any folder.
3. Run `AetherClient.exe` (Administrator privileges required for Wintun adapter provisioning).
4. Select your preferred mode (**System Proxy** or **True TUN Adapter**) and click **CONNECT**.

---

## 🛠️ Build from Source

```powershell
# Restore NuGet dependencies
dotnet restore AetherClient.sln

# Publish self-contained single-file executable
dotnet publish AetherClient\AetherClient.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\publish
```
Place `aether.exe`, `wintun.dll`, `tun2socks.exe`, and `aether-masque.toml` in the `.\publish\` folder alongside `AetherClient.exe`.
