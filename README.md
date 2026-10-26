# HearthLink

Original Windows 11 remote desktop application for your own PCs. **Version 0.3.2 is an attended test build, not the complete requested release. Two-physical-PC acceptance is pending.** No fake devices or simulated sessions appear in the application.

**Unattended access (unreleased, planned for 0.4.0):** the host setting **Let my paired PCs connect when I'm away** lets every PC already paired with this one start a session without the per-session Allow dialog. It is off by default, and turning it on or off needs Windows Hello (PIN, face or fingerprint) at the host; if Windows Hello is not set up, HearthLink explains how to add a PIN and leaves the switch unchanged. Unpaired PCs still cannot connect. Every unattended session still shows the host banner and tray icon with **End session**, raises a Windows notification, writes `unattended-connected` to session history, and disconnects after 30 minutes without input from the controller (keyboard, mouse, display or quality changes; a view-only viewer that only watches is disconnected after 30 minutes). The decision is fixed rules only: the switch plus the controller's pinned certificate fingerprint. Nothing the other PC sends, such as its name, is consulted. Start at sign-in and close-to-tray are not in this build yet, so HearthLink must be open and the host signed in and unlocked. If Windows Hello is later removed, turn off **Allow incoming sessions** or close HearthLink to stop all incoming sessions.

Version 0.3.2 keeps relay connections working while a proxy-based "VPN" app (such as Aurora VPN free mode) is on: HearthLink reaches the relay directly first and uses the Windows proxy only if that fails.

Version 0.3.1 adds the "Created by Erick Omari" credit to the app sidebar.

Version 0.3.0 connects paired PCs on any network through the HearthLink relay (`relay/`, a Cloudflare Worker). Both PCs connect outbound, so no firewall rule or router setup is needed; mutual TLS with pinned fingerprints still runs end to end, and the relay cannot read sessions ([trust boundary](docs/ARCHITECTURE.md#relay-trust-boundary)). Pairing uses a 12-digit code and trusts both directions; existing one-way 0.2.x pairings upgrade automatically the next time the two PCs connect. Direct LAN connections are an opt-in fallback. **Update both PCs to 0.3.0**: a 0.2.x PC shows an update message instead of connecting.

Version 0.2.2 hardens pairing: remote computer names are stripped of control and text-direction characters, session approval prompts state the access mode first, wrong pairing codes lock out only the guessing address, and saved hosts are matched only by the address you typed.

Version 0.2.1 adds automatic update checks, notifications, verified installer downloads, and user-confirmed installation. Open **App updates** in the local computer panel. The release source is `pitchiluxe/hearthlink` on GitHub; checks require an internet connection and a published release. Existing 0.2.0 installations need this first update-enabled version installed manually. [Update and publishing guide](docs/UPDATES.md).

The responsive Home, Download, and Contact website lives in `website/`. Its intended Vercel address is `hearthlink.vercel.app`; this address is not confirmed until deployment succeeds. Installers stay on GitHub Releases, not in the website repository.

Version 0.1.1 adds the HearthLink linked-screen icon, a navy/blue Windows interface, a separate local-access panel and a matching session toolbar. [Design notes and preview commands](docs/DESIGN.md) describe the actual UI renders. The icon source and multi-resolution ICO are in `assets/`; generators are in `tools/`.

## Illustrated user manual

The [step-by-step PDF manual](artifacts/manual/HearthLink-0.1.1-User-Manual.pdf) and [offline browser manual](artifacts/manual/HearthLink-0.1.1-User-Manual.html) include annotated screenshots, installation and pairing instructions, normal use, troubleshooting, revocation and cleanup.

## Install on two PCs

1. Copy `artifacts/HearthLink-0.3.0-win-x64-setup.exe` to both PCs. This build is unsigned; Windows can show an unknown-publisher/SmartScreen warning. Verify its SHA-256 against `artifacts/SHA256SUMS.txt` before deciding to run it. No security-tool exclusions are required or recommended.
2. Run the installer. It installs for the machine and needs administrator approval. Leave the optional **direct connections** firewall task unchecked unless you want the LAN fallback; it permits only this executable's TCP 45831 on Private networks from the local subnet.
3. Launch HearthLink as your normal Windows user. On the remote PC, enable **Allow incoming sessions**; its status should read **Online through relay**. On the controller, enable **Use as a controller**. For both roles, enable both. Give each a recognizable name and save it. **Also accept direct connections on my network** is off by default; turn it on (with the firewall task) only for the LAN fallback.
4. Keep the host signed in and unlocked.
5. On the host, choose **Open pairing for 2 minutes**. It shows a 12-digit code such as `1234-5678-9012`. On the controller, type that code into the pair box and select **Pair / connect**. Compare all 64 hexadecimal fingerprint digits with the remote PC's identity display. Reject a mismatch. (A hostname or IPv4 address instead of a code pairs over the LAN, which needs direct connections on the host.)
6. At the host, compare the requesting PC's fingerprint with the identity shown on that PC, then approve **Trust this PC both ways?**. Codes expire after two minutes, are single-use, and lock out after five incorrect attempts per PC (50 in total).
7. Select the paired PC on the controller. Status refreshes every 10 seconds: **Online** (through the relay), **Online (network)** (LAN only), **Offline**, **Trust revoked** or **Identity mismatch**. Choose keyboard/mouse control or leave it unchecked for view-only, then **Connect**.
8. Approve the view/control request at the host. Confirm the viewer shows the host's actual desktop. Open Notepad locally on the host, type from the controller and test clicking/moving its window. Do not use a password field for this test.
9. Close the viewer or choose **Disconnect**. At the host, **End remote session** also terminates access. The visible host status identifies an active connection.

Pair only when someone can compare both devices' identities. One pairing trusts both directions: either PC can request a session on the other, and every session still needs approval on the PC being accessed unless that PC has turned on unattended access.

**Unattended access:** on the host, select **Let my paired PCs connect when I'm away** and complete the Windows Hello prompt. The host status then reads **unattended access on**, and paired PCs connect without the Allow dialog. Select it again and verify with Windows Hello to turn it off. Changes, refusals and every unattended session are written to **Session history**.

## Normal use and revocation

Use the monitor list to switch displays. The image is fitted to the viewer; mouse coordinates account for letterboxing and display resolution. Full screen toggles from its toolbar. Keyboard focus belongs to the viewer when its screen is clicked. F12 releases focus to the Disconnect button. Windows may intercept local combinations such as Windows keys and Alt+Tab; Ctrl+Alt+Del, secure UAC, locked desktop and sign-in are unsupported. Elevated applications may reject input under Windows UIPI. No attempt is made to bypass those restrictions.

**File transfer (control sessions):** in the viewer's toolbar (move the pointer to the top edge to show it), **Send files** copies files you pick into the other PC's `Downloads\HearthLink` (or drag files and folders from Explorer onto the remote screen; folders keep their subfolders, and a folder that already exists there arrives as `name (2)`); **File transfer** opens a two-pane file manager in the style of Total Commander: this PC on the left, the other PC on the right, each with a drive dropdown, drive buttons with free space, the open folder's files (Name, Ext, Size, Date) and a selection summary. Pick any drive and folder on either side, select files or folders and press **F5** (or an arrow button) to copy them into the folder open in the other pane; **F7** creates a folder, **Ctrl+R** refreshes, **Tab** switches panes, **Backspace** goes up. Progress and **Cancel** appear in a strip at the bottom of the viewer. Files of any size, as long as the receiving drive has room (otherwise the copy is refused up front with the space needed and available); folders of any size list completely; a file with an existing name is saved as `name (2).ext`; a cancelled or broken transfer leaves no partial file. Files move only while the approved control session lasts, on a separate connection so video stays smooth.

**Remove PC** (in either list) removes that PC in both roles, ends the current host session and updates the relay's allowed list, so the removed PC no longer sees this one as Online. Remove it on the other PC too. Disabling incoming sessions or closing the host app takes the host offline. After a host identity mismatch, compare its new identity in person before deliberately removing and pairing again.

Network interruptions close the viewer connection. Close the viewer, restore connectivity, refresh status, then connect again and approve at the host. There is no automatic reconnection. Unattended access needs HearthLink open on a signed-in, unlocked host; do not rely on it for reboot recovery.

## Troubleshooting

* **Offline:** confirm the host app is running with incoming sessions enabled and its status reads **Online through relay**, and that both PCs have internet access. **Relay unreachable • LAN only** on the host means it cannot reach the relay; paired PCs can then connect only over the LAN with direct connections enabled.
* **Offline only while a VPN/proxy app is on:** some "free VPN" apps (for example Aurora VPN's free mode) only set a Windows proxy. HearthLink now reaches the relay directly first and uses that proxy only if direct fails; relay sessions are already end-to-end encrypted, so the proxy adds no privacy. Full-tunnel VPNs (WireGuard/OpenVPN) need nothing special. A VPN app in an error or "blocked" state (for example a revoked Mullvad device) blocks all traffic, including HearthLink.
* **LAN fallback (Online (network)):** needs **Also accept direct connections on my network** on the host and TCP 45831 reachable. In PowerShell on the controller: `Test-NetConnection HOSTNAME -Port 45831`. Port reachability alone does not verify authentication.
* **Firewall (LAN fallback only):** the installer rule applies only to Private networks/local subnet. Domain-joined PCs work at the application protocol level, but a domain-profile firewall or organization policy can block it. Have the administrator create a rule for this exact app, port and approved source subnet on the appropriate profile. Do not open Public profiles or configure router forwarding. VPN subnets may need a similarly explicit administrator-scoped rule.
* **Host unavailable/address in use:** another host process can own port 45831. Close duplicate copies. One Windows user's host per PC can listen at a time.
* **Trust revoked:** re-pair only after deliberate approval at the host.
* **Identity mismatch:** never dismiss by turning off TLS validation. Compare identities and explicitly re-enroll.
* **Code rejected:** open a fresh pairing window and approve there. The app must remain visible to approve.
* **Screen black/stale or input blocked:** return to the ordinary unlocked desktop. Secure desktops, elevated programs, protected video and some HDR content are outside the supported capture/input path. Reconnect if necessary.
* **No background access after reboot:** expected in this milestone. Open HearthLink after signing in. Host configuration persists, but automatic startup is not installed.

## Build and tests

Build tools used: .NET SDK **10.0.402**, .NET Windows Desktop runtime **10.0.13**, Inno Setup **6** (see verification report for exact local compiler version). Target: `win-x64`, .NET 10 Windows Forms (`net10.0-windows10.0.19041.0`, so the app can call the built-in Windows Hello API; the publish output therefore includes Microsoft's `Microsoft.Windows.SDK.NET.dll` and `WinRT.Runtime.dll` projection assemblies). No third-party NuGet libraries. The SDK/runtime pack downloads require access to Microsoft's NuGet feeds on a fresh machine.

Run `powershell -ExecutionPolicy Bypass -File .\build.ps1 -SelfContained` from a checkout. The process-level execution-policy flag runs this reviewed local build script; it does not change machine policy. An ordinary build may need access to SDK registry metadata; restricted environments must permit that read. `build.ps1` runs both test suites, clears only its generated publish folder, publishes the app and compiles the installer. It does not install the app or open firewall rules on the build PC. The self-contained runtime version is pinned to 10.0.13 in the app project.

Additional Windows integration checks: `dotnet run --project tests/HearthLink.WindowsTests -c Release`. Close all HearthLink hosts first because the fixture uses TCP 45831 on loopback. These checks create and remove their own temporary DPAPI identities. They do not perform a two-PC test or inject remote keyboard input.

Use `-SelfContained` for a release installer that includes the runtime. Without it, .NET Desktop Runtime 10 must already be installed on each PC. For SDK operation within a restricted workspace, set `DOTNET_CLI_HOME` to a writable workspace folder (the build script does this).

For production distribution, sign the application and compiled installer with your organization's trusted Authenticode code-signing certificate and a trusted timestamp. Keep the signing private key out of source and build logs. Signed builds have different SHA-256 hashes; regenerate the manifest after signing. No certificate or publisher identity has been fabricated.

## Uninstall

Use Windows **Settings → Apps → Installed apps → HearthLink → Uninstall**, or the Start-menu uninstaller. The uninstaller removes the program and its named firewall rule. No Windows service, scheduled task or startup entry exists in 0.1.1. Per-user enrollment/history is retained to avoid silently deleting another user's records; to remove it, revoke access first, close HearthLink and delete `%LOCALAPPDATA%\HearthLink` separately for each Windows user that ran it. Deleting identity data requires pairing again after reinstall.

Read [architecture and trust model](docs/ARCHITECTURE.md), [two-PC acceptance checklist](docs/ACCEPTANCE.md), [design notes](docs/DESIGN.md), and [verification report](docs/VERIFICATION.md). The unattended Windows service (lock screen and after-restart access), start at sign-in, discovery, clipboard, file transfer and restart/reconnect remain later work, gated on the first real connection test.
