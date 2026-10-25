# ⚡ Cloudflare WARP (MASQUE Edition)

> Lightweight, high-performance native Windows client for **Cloudflare WARP** over **MASQUE HTTP/2 (RFC 9298)**, engineered for zero-DNS-leak censorship bypass on restricted networks (Iran TCI, MCI, Irancell).

<p align="center">
  <img src="assets/preview.jpg" alt="Cloudflare WARP MASQUE Client Preview" width="800" />
</p>

---

### ✨ Features

- **MASQUE HTTP/2 Protocol**: Routes Cloudflare WARP through `CONNECT-IP` (RFC 9298) over standard HTTP/2, completely bypassing UDP filtering and SNI blocks.
- **Dual Routing Architecture**:
  - 🛡️ **True TUN (Wintun)**: Full layer-3 kernel adapter tunneling all system TCP/UDP traffic and games, with automatic DNS anti-poisoning locked to `1.1.1.1`.
  - 🌐 **System Proxy (WinINET)**: Instant zero-overhead HTTP/HTTPS CONNECT proxy on `127.0.0.1:1820` for all Windows browsers (Brave, Chrome, Edge).
- **Smart Reconnection**: Sub-300ms instant reconnect using cached healthy edge gateways, with auto-fallback to turbo scanning across 2,000+ IP candidates.
- **Zero-Orphan Process Management**: Native Win32 Kernel Job Objects ensure clean teardown of child engines and routing tables on exit.
- **Fluent Cyber UI**: Hardware-accelerated WPF dark interface with pulsing radar animations and real-time telemetry.

---

### 🚀 Quick Start

1. Download the latest release from [Releases](https://github.com/ShiTmoZ/A/releases).
2. Extract the archive into a dedicated folder.
3. Run `AetherClient.exe` as **Administrator**.
4. Select your preferred mode (**True TUN** or **System Proxy**) and click **CONNECT**.

---

### 🛠️ Build from Source

```powershell
dotnet publish AetherClient\AetherClient.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o .\publish
```
Place `aether.exe`, `wintun.dll`, `tun2socks.exe`, and `aether-masque.toml` alongside `AetherClient.exe`.
