# Verification report — 2026-10-22

## Update and website release 0.2.1

The completed retry build passed **41 core checks and 17 Windows checks** and compiled the self-contained installer with Inno Setup. Log: `artifacts/build-0.2.1-retry.log`. Eight update-core checks cover exact release metadata/checksum pairing, no reinstall/downgrade, byte integrity, overwrite refusal, corrupt-download cleanup, HTTPS downgrade rejection, cancellation cleanup, and external publisher rejection. Two new Windows checks verify malformed release handling by the update coordinator and reset of stale downloaded-installer state when a different release becomes available.

One earlier full build failed a loopback TCP connection timeout. The unchanged transport passed an isolated rerun and then the complete retry build. No transport fix or root cause is claimed for that isolated failure. The retry log is the successful release evidence.

A separate review found and corrected unhandled verification failures, explicit elevation that could lose Inno's original-user context, and stale installer binding. The installer requests its own elevation; the application does not explicitly request the runas verb. See the [Inno original-user launch documentation](https://jrsoftware.org/is6help/topic_runsection.htm).

Browser checks loaded the real Home, Download, and Contact pages. At desktop width 1440, homepage assets loaded and no horizontal overflow was present. At mobile width 390, all three pages had no horizontal overflow; Contact required a nonempty message. Screenshots are in `artifacts/design/website-*.png`. A mobile heading word-spacing issue was corrected after screenshot inspection. Desktop update-window and dashboard renders are in `artifacts/design/0.2.1/`. The illustrations and application renders use isolated fixtures, not customer sessions.

The JavaScript syntax check passed. GitHub-hosted release discovery and an actual UAC installation were not exercised against a published release. The local machine reported DNS resolution failures for api.vercel.com and api.github.com, and GitHub CLI reported an invalid pitchiluxe credential. Neither a GitHub push nor a Vercel deployment is confirmed by this report. The intended production domain is hearthlink.vercel.app, subject to availability and successful deployment. Earlier version reports below are retained as history.

## Release state

HearthLink 0.1.1 is a build of the **attended vertical slice awaiting two-PC acceptance**. It is not the complete application requested. Source and a self-contained x64 installer are provided for testing that slice. No physical second computer has been accessed by this agent. Installation, screen streaming, keyboard/mouse injection, performance and clean disconnect between two physical PCs remain unverified. Do not infer these from successful compilation or TLS tests.

## Executed automated checks

* Core suite: **13 passed**. Closed pairing by default; random eight-digit format; wrong code rejection; valid code; consumed code rejection; expiry; five-failure closure; unknown/trusted/revoked trust entries; packet round trip; zero-sized and oversized frame rejection.
* Windows suite: **14 passed**. Unique certificates; DPAPI identity persistence; protected trust persistence; non-raw-PFX saved key; actual mutually authenticated encrypted TLS over loopback; trusted policy response; client identity observed by server; untrusted and revoked identity rejection by fixture authorization; changed host pin rejection during handshake. Four additional checks exercise the **actual `HostServer`**: trusted probe succeeds; untrusted control request is denied before capture/consent; pairing without an open pairing window is denied; a revoked controller is denied.
* The Windows suite uses actual `IdentityStore`, `Connection`, `Wire` and `HostServer`. Its actual-host checks run an isolated Windows Forms instance with a real UI dispatcher and a loopback-only listener. It does **not** approve a graphical consent dialog, capture a desktop, inject input or connect two physical PCs. These remain manual acceptance checks.
* A deliberately incorrect pairing implementation first failed the closed-by-default check. The implementation was then corrected and all core checks passed.
* The initial Windows TLS check failed because Schannel rejected ephemeral private keys. The observed exception was `Authentication failed because the platform does not support ephemeral keys`. Importing to a current-user Windows key container resolved it. The saved PFX remains DPAPI-protected; the Windows key container is managed by the certificate lifetime.

## Local build checks

* .NET SDK 10.0.402; Windows Desktop runtime 10.0.13.
* Release desktop compilation succeeded with **zero warnings, zero errors**.
* Self-contained win-x64 publish succeeded, bundling .NET 10.0.13 runtime components.
* Inno Setup **6.7.0** compiled the installer. The locally installed compiler reports **Non-commercial use only**; this home-use artifact is for the user's personal LAN testing. Commercial distribution needs an appropriately licensed compiler and review of the current Inno Setup terms: https://jrsoftware.org/isinfo.php.
* Final reproducible command: `powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -SelfContained`. It runs both suites, publishes, copies runtime license notices, compiles setup and generates the installer SHA-256 manifest and complete source ZIP. Original build output: `artifacts/build.log`; visual-update build output: `artifacts/build-0.1.1.log`.
* The sandbox initially blocked SDK registry metadata reads; the same build succeeded when that read was permitted. No Windows protection setting was disabled.
* No installation or uninstall was performed on the development PC; no application firewall rule or service was installed here. The test UI constructed and exited successfully. UI rendering validation is described below; no interactive consent, capture or input smoke check has been performed.

## Visual update in 0.1.1

The actual Windows Forms dashboard was rendered from its own controls at 1340×880 and 1120×790 client sizes using an isolated, unenrolled identity. Both PNGs were visually inspected. Clipped button labels and narrow-column wrapping were corrected, then the previews were regenerated and inspected. These are UI renders, not OS screen captures or remote-session evidence. The temporary preview contains no invented devices, active sessions or production identities. The matching multi-resolution icon is embedded in the executable and installer; its nine images span 16–256 pixels. Viewer toolbar and pairing-entry dialog styling use the same theme, but are not exercised by the dashboard renders.

## Physical-PC acceptance pending

Use [ACCEPTANCE.md](ACCEPTANCE.md). First establish an attended A→B session, prove that an unpredictable visible change on B appears on A, type a unique nonsensitive sentence in B's Notepad from A, then disconnect from each end. Record actual results before declaring the milestone achieved. Both enrolled identities must be compared at pairing. Also check the installer firewall/uninstall behavior on the two PCs.

## Remaining implementation

Automatic host discovery; unattended access and administrator enablement; service/session broker and startup after reboot; text clipboard; file transfer and validation tests; confirmed remote restart and automatic reconnection; richer session-history interface; broader authentication throttling; robust handling of secure-desktop transitions; independent security review and extensive compatibility/performance validation.

Basic monitor switching, image fit/full-screen and persistent enrollment exist in source, but their real mixed-DPI/multi-monitor behavior is pending manual acceptance. Current history records metadata only; it is not a complete forensic audit system. First milestone uses GDI JPEG frames around a 100ms pacing interval, not an optimized video codec. There is no assurance yet of usable performance on the user's actual machines.

## Signing and limitations

Builds are **unsigned**. There is no invented publisher or signing certificate. Windows can warn about an unknown publisher or download reputation. Verify the SHA-256 before deciding to run. Production signing requires a trusted Authenticode certificate and timestamp; regenerate hashes after signing. Never add security-tool exclusions.

This milestone requires a visible host process in an unlocked, signed-in normal user session. It cannot administer sign-in, lock screens, secure UAC or Ctrl+Alt+Del, and elevated windows may reject input. It installs no background service or startup task. Domain membership is not used for authorization: app certificate pairing is required on both domain-joined and workgroup PCs. Firewall profile/VPN differences require local administrative configuration restricted to the approved subnet.
