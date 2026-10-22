# HearthLink architecture and engine decision

HearthLink targets Windows 11 Home, Pro, Enterprise and Education on a LAN or private VPN. No rendezvous, relay, public listener configuration, account service or cloud dependency is used.

## Research and decision (2026-10-22)

* Built-in RDP hosting cannot satisfy Windows Home. It also changes console-session behavior, which differs from attended screen sharing.
* RustDesk is an established engine with broad support features. Its client is AGPL-3.0; distributing a modified client entails providing corresponding source and notices, with network-use obligations for modifications. A maintained fork requires Rust/MSVC/vcpkg/LLVM plus UI tooling. No RustDesk binary or code is redistributed here. Sources: https://github.com/rustdesk/rustdesk and https://rustdesk.com/docs/en/dev/build/windows/.
* TigerVNC is GPL-based, including third-party license exceptions; adopting its code entails corresponding-source and notice obligations. Classic VNC authentication alone does not meet the requested connection trust model. A vetted TLS-capable Windows host integration remains a possible future engine replacement. No VNC code is included. Source: https://github.com/TigerVNC/tigervnc/blob/master/LICENCE.TXT.
* Selected for the first milestone: Windows Forms on .NET 10, native GDI capture through `Graphics.CopyFromScreen`, `SendInput`, and .NET `SslStream`. These allow a small, buildable Windows-only implementation. This is an original streaming protocol, **not custom cryptography**. It trades video efficiency and protected-desktop capabilities for simplicity. It is not yet suitable for treating as a hardened production remote-access product. Engine choice must be revisited if measured responsiveness is inadequate.

Official API references:

* TLS: https://learn.microsoft.com/en-us/dotnet/api/system.net.security.sslstream
* Windows key protection: https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.protecteddata
* Capture fallback used: https://learn.microsoft.com/en-us/dotnet/api/system.drawing.graphics.copyfromscreen
* Higher-efficiency capture alternatives evaluated: https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/desktop-dup-api and https://learn.microsoft.com/en-us/windows/uwp/audio-video-camera/screen-capture
* Input and UIPI: https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput
* Services/session isolation: https://learn.microsoft.com/en-us/windows/win32/services/interactive-services
* Windows Installer evaluated: https://learn.microsoft.com/en-us/windows/win32/msi/windows-installer-portal
* Inno Setup used for the executable installer: https://jrsoftware.org/isinfo.php

The .NET runtime is maintained by Microsoft and is distributed under its bundled license and third-party notices when a self-contained build is made. Inno Setup compiles the installer; its compiler is not bundled. No third-party remote desktop engine is linked into the application. Review license texts before distributing a modified build or changing engines.

## Components

`HearthLink.Core`: pairing policy, trust book, bounded length-prefixed JSON messages. `IdentityStore`: unique RSA certificate, current-user DPAPI-protected private PFX and settings, audit metadata. `Transport`: TLS 1.2/1.3 with pinned SHA-256 certificate identity and client certificate authentication. `HostServer`: bounded concurrent handshakes, authenticated authorization, attended consent, one active session, revocation checks. `Desktop`: capture and input. `MainWindow`: enrollment and real status probes. `Viewer`: rendering, monitor selection, coordinate scaling, input and disconnect. `installer`: elevated application install and optional narrowly scoped firewall rule.

## Trust boundary

TLS initially permits a self-signed client certificate to prove key possession. That does **not** authorize screen or input access. Unknown identities can only attempt pairing; all probes and sessions require an exact enrolled fingerprint. The controller compares the host's full fingerprint out of band before sending a pairing code. The host opens an eight-digit, two-minute, one-use pairing window; five failed attempts close it. A matching code still requires explicit approval at the host with a controller fingerprint comparison. Pairing authorizes requesting a session, not unattended access. A separate attended dialog approves viewing or control for every session. View-only is enforced by the host. Revocation terminates the active session and rejects subsequent requests. Host identity changes fail the pin check and require explicit forget/re-pair.

Store protection is per Windows user, independent of domain membership. It does not defend against malware already running as that user or an administrator. Identity expires after five years; re-pairing is necessary after replacement. Remote callers have eight concurrent handshake/request slots and bounded frame sizes, but this milestone is not a comprehensive denial-of-service defense. Use only trusted Private networks or a suitably restricted VPN. No secrets or screen payloads are logged. Audit records contain certificate fingerprints, timestamps and outcomes.

## Milestone boundary

This build implements the first attended connection slice, plus persistent identities/trust, last-seen probes and basic monitor switching. The next stage is gated on a real two-PC screen/control test. Automatic discovery, unattended access, background service/startup, clipboard, file transfer, restart and fully integrated session-history UI are not implemented. History is an actual metadata file opened with Notepad. No nonexistent features have active controls.

The host deliberately runs in the visible interactive user session. No service is installed because a session-zero service cannot simply capture/control the signed-in desktop. A future service needs an authenticated local broker and per-session agent, explicit administrator enablement and separate security review. Sign-in, locked desktop, secure UAC, Ctrl+Alt+Del, elevated windows and pre-login access are unsupported. Windows security protections are not modified. At lock/UAC, capture can fail or stall; close/reconnect after returning to the normal desktop. HDR/protected video may render incorrectly or black. JPEG full-frame capture targets roughly ten frames/second, without performance guarantees. No audio.
