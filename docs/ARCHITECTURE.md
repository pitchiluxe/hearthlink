# HearthLink architecture and engine decision

HearthLink targets Windows 11 Home, Pro, Enterprise and Education. Since 0.3.0, paired PCs reach each other on any network through the HearthLink relay (`relay/`, a Cloudflare Worker at `wss://hearthlink-relay.erickomari.workers.dev`); a direct LAN connection on TCP 45831 remains an opt-in fallback. No public listener configuration or account service is used.

## Relay trust boundary

Both PCs connect outbound to the relay and authenticate with a signature from their device certificate. The relay pipes bytes between them; the existing mutual TLS with pinned fingerprints runs end to end inside that tunnel.

| Threat | Protection |
|---|---|
| Relay operator or attacker who controls the relay | End-to-end mutual TLS with pinned fingerprints inside the tunnel; the relay cannot decrypt, alter or impersonate. Worst case: it blocks or delays connections and sees metadata (which device IDs are online, when, how much data). |
| Stranger on the internet | Must possess a paired private key. The relay refuses status and connect for devices not on the host's allowed list, so strangers cannot reach the host's TLS handshake outside an open pairing window. |
| Files | Only during an approved control session: the host's `ready` carries a one-session grant token (`SessionFileGrant`) that a second pinned-TLS connection must present; the grant ends with the session. Received files go into the folder the controller picked in its file manager (an existing absolute folder, written as the host's signed-in Windows user) or, by default, `Downloads\HearthLink`; every name and relative path is checked by `FilePolicy.ValidName`, cannot leave that folder, never overwrites (`name (2)`), and lands through a temp file with length and SHA-256 checks. Reads are limited to what the host's signed-in user can read. Since a control session can already operate the host's desktop, choosing the folder grants nothing that session lacks. |
| Unattended access | Off by default. Changing it in either direction needs Windows Hello at the host (`UserConsentVerifier`), and turning it on is refused while any incoming session is active, before and after the Hello prompt, so a remote controller cannot click the switch and type into the prompt. Turning it off ends an active unattended session. When on, `UnattendedAccess.SkipsApproval` accepts only the switch plus an exact pinned fingerprint in `Controllers`; peer names and text are never consulted, and no model or heuristic is involved. Every unattended session shows the banner and tray icon with End session, raises a notification, is recorded as `unattended-connected`, and ends after 30 minutes without controller input. Revocation still ends it immediately. |
| Guessing a pairing code | 8-digit secret sent only inside TLS, two-minute window, 5 tries per peer, 50 total, plus fingerprint approval on both PCs. The 4-digit lookup sent to the relay is not secret. |

The relay stores only per-host allowed lists, presence and open pairing lookups: nothing about screens, input, the secret part of codes, or keys.

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

TLS initially permits a self-signed client certificate to prove key possession. That does **not** authorize screen or input access. Unknown identities can only attempt pairing; all probes and sessions require an exact enrolled fingerprint. The controller compares the host's full fingerprint out of band before sending a pairing code. The host opens an eight-digit, two-minute, one-use pairing window; five failed attempts close it. A matching code still requires explicit approval at the host with a controller fingerprint comparison. Pairing authorizes requesting a session. A separate attended dialog approves viewing or control for every session, unless the host's unattended switch is on (see the relay trust boundary table); then a paired controller's session starts without it, still with the visible banner. View-only is enforced by the host. Revocation terminates the active session and rejects subsequent requests. Host identity changes fail the pin check and require explicit forget/re-pair.

Store protection is per Windows user, independent of domain membership. It does not defend against malware already running as that user or an administrator. Identity expires after five years; re-pairing is necessary after replacement. Remote callers have eight concurrent handshake/request slots and bounded frame sizes, but this milestone is not a comprehensive denial-of-service defense. Use only trusted Private networks or a suitably restricted VPN. No secrets or screen payloads are logged. Audit records contain certificate fingerprints, timestamps and outcomes.

## Milestone boundary

This build implements the first attended connection slice, plus persistent identities/trust, last-seen probes and basic monitor switching. The next stage is gated on a real two-PC screen/control test. Unattended access exists for a signed-in, unlocked host with HearthLink open (since phase 2). Automatic discovery, background service/startup, clipboard, file transfer, restart and fully integrated session-history UI are not implemented. History is an actual metadata file opened with Notepad. No nonexistent features have active controls.

The host deliberately runs in the visible interactive user session. No service is installed because a session-zero service cannot simply capture/control the signed-in desktop. A future service needs an authenticated local broker and per-session agent, explicit administrator enablement and separate security review. Sign-in, locked desktop, secure UAC, Ctrl+Alt+Del, elevated windows and pre-login access are unsupported. Windows security protections are not modified. At lock/UAC, capture can fail or stall; close/reconnect after returning to the normal desktop. HDR/protected video may render incorrectly or black. JPEG full-frame capture targets roughly ten frames/second, without performance guarantees. No audio.
