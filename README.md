# ⚡ Cloudflare WARP — MASQUE Edition (v1.0.34)

> Production-grade, zero-leak native Windows client for **Cloudflare WARP** over **MASQUE HTTP/2 (RFC 9298)**, engineered for resilient DPI censorship bypass on heavily filtered networks (Iran TCI, MCI, Irancell).

[![Release](https://img.shields.io/github/v/release/ShiTmoZ/A?color=00E676&label=Release&style=flat-square)](https://github.com/ShiTmoZ/A/releases/latest)
[![Platform](https://img.shields.io/badge/Platform-Windows%20x64%20(10%2F11)-blue?style=flat-square)](#)
[![Protocol](https://img.shields.io/badge/Protocol-MASQUE%20H2%20(RFC%209298)-purple?style=flat-square)](#)
[![UI](https://img.shields.io/badge/Design-SetProduct%20Liquid%20Glass-00E676?style=flat-square)](#)

---

### ✨ Core Capabilities

- **MASQUE over HTTP/2 (RFC 9298)**:
  Encapsulates Cloudflare WARP through `CONNECT-IP` on connection-oriented HTTP/2 (TCP port 443). Eliminates UDP drops, handshake throttle, and SNI censorship on DPI firewalls.
- **SetProduct Liquid Glass Optical Material**:
  - High-precision 440px floating chassis with locked 125° specular meniscus lighting.
  - Deep obsidian scrim floor (`#080B11`) with dual ambient radiance amplifiers (Cyan `#4CC9F0` & Violet `#A878FF`).
  - Master Caustic Glass Plate (280×56) with emerald active glow (`#00E676`) and live connection telemetry.
  - Collapsible semantic diagnostics console with color-coded tag telemetry (`#8DB4F5` CORE, `#B7A4F2` MASQUE, `#7FD1BE` TUN).
- **Dual Routing Architecture**:
  - 🛡️ **True TUN (Kernel Wintun L3)**: Low-latency virtual adapter tunneling all system IPv4 UDP/TCP traffic, games, and Telegram Desktop. Features automatic Anycast CIDR pinning (`162.159.192.0/18` & `188.114.96.0/20`) preventing routing loops.
  - 🌐 **System Proxy (WinINET)**: Instant zero-overhead proxy on `127.0.0.1:1820` for browsers (Brave, Chrome, Edge) with automatic DNS cache flushing.
- **Zero-Poisoning DNS Shield**:
  Dynamically locks adapter DNS to `1.1.1.1` and `1.0.0.1`, completely deflecting ISP DNS spoofing and sinkhole hijacking (such as `10.10.34.34`). Restores automatically to DHCP on disconnect.
- **Active TCP Socket Probe**:
  Replaces fragile stdout string parsing with non-blocking kernel socket checks on `127.0.0.1:1820`, locking connection state in <450ms.
- **Kernel Job Object Hygiene**:
  Binds child daemons (`aether.exe` and `tun2socks.exe`) to Windows Job Objects with `KILL_ON_JOB_CLOSE`, ensuring zero zombie background processes on exit or system reboot.

---

### 🛡️ Why MASQUE Works Against Severe DPI & Censorship

| Threat Vector | Standard WireGuard / 1.1.1.1 Client | Aether MASQUE Client (v1.0.34) |
| :--- | :--- | :--- |
| **UDP Rate-Limiting & Blacklisting** | Blocks initial UDP handshake; drops WireGuard packets. | Encapsulated in standard TCP TLS 443; indistinguishable from regular HTTPS web traffic. |
| **ISP DNS Poisoning & Sinkholing** | ISP returns fake IP (e.g. `10.10.34.34`) on port 53. | Queries are routed inside the encrypted tunnel or resolved remotely via Cloudflare edge. |
| **Routing Loop & Adapter Conflict** | Manual route commands easily break when Hyper-V or WSL is present. | Dynamic adapter discovery finds physical gateway and pins Cloudflare CIDRs explicitly. |
| **Crash Recovery** | System proxy or routing tables remain broken after sudden reboot. | `OrphanStateSanitizer` cleans and restores WinINET registry & routes on every launch. |

---

### 🚀 Quick Start

1. Download the latest release: [AetherClient-Windows-x64.zip](https://github.com/ShiTmoZ/A/releases/latest).
2. Extract the archive into a dedicated folder.
3. Run `AetherClient.exe` as **Administrator**.
4. Choose **True TUN** or **System Proxy** and click the central power plate.

---

### 🛠️ Build from Source

Requirements: .NET 8 SDK on Windows x64.

```powershell
dotnet publish AetherClient\AetherClient.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o .\publish
```
