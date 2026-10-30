# HearthLink 0.5.0 — navigation and activity

The sidebar now contains Your computers, Recent sessions, File transfers, Access & security, Connection health, Session history, Settings, and Help & updates. Help stays at the bottom; the other links scroll when needed. An active-session shortcut appears for live viewer/host sessions.

## What works

- Search paired PCs by name/address, toggle favorites and filter the list to favorites. Connection and removal use the selected pinned identity even after filtering/reordering.
- Recent sessions reads actual outbound-connected records and shows only PCs that remain paired. Request Control or View only visibly beside Connect selected. All connection entry points obey the controller role. Removing trust or disabling the controller while waiting for approval prevents a viewer from opening.
- File transfers receives actual preparation, progress, completion, cancellation and failure events from both viewer and file-manager copies. Select a live control session to open its file manager; select a running batch to cancel it. Up to 200 finished records are retained for this app run, alongside current batches. View-only sessions cannot transfer files.
- Access & security reuses the existing incoming-session, LAN, unattended/Windows Hello and revocation controls. Changing pages does not restart hosting or terminate sessions.
- Connection health shows host state, last successful relay check and per-PC status. Failed relay checks show Status unavailable, separately from Offline. This is availability information, not a bandwidth benchmark.
- Session history reads a snapshot of the last 512 KiB and returns up to 1,000 valid local records; damaged/truncated lines are ignored. Search computer/fingerprint/event metadata and export it to JSONL. It uses the profile's own folder, including isolated tests. Export contains timestamps, fingerprints and event types, not keys, codes, file contents or transfer paths.
- Settings saves default keyboard/mouse mode and image quality plus automatic update checks using existing protected storage. Computer name and controller role are here too. Preferences affect new sessions; existing session choices remain in effect. Startup/tray and theme choices are future work.
- Help & updates includes current getting-started instructions, the update window and links to releases and the original illustrated guide. The guide is explicitly labeled as covering the original interface.

## Verification and limits

Core suite: 96 checks passed. Windows suite: 110 checks passed, including a real pinned-TLS test that removes trust while approval is pending. That regression failed before the fix. All eight pages were rendered with an isolated, unenrolled identity; normal and simulated 125%/200% layouts were inspected. No real user history or devices appear in previews.

The live relay integration checks are skipped unless HEARTHLINK_RELAY_URL is set; the relay protocol/source did not change in this update. Physical two-PC sessions, native mixed-monitor DPI transitions, setup execution and UAC installation remain separate acceptance checks. The installer remains unsigned. This version connects to 0.4.1–0.4.4 and preserves existing enrollment/settings.

Self-contained win-x64 publish and Inno Setup compilation succeeded. Local installer: `artifacts/HearthLink-0.5.0-win-x64-setup.exe`; build output: `artifacts/publish-0.5.0.log` and `artifacts/installer-0.5.0.log`. Tests: `artifacts/sidebar-core.log` and `artifacts/sidebar-final-windows.log`. Previews: `artifacts/design/sidebar/`. The local package was not installed or published to GitHub Releases.

Source plan and execution notes: docs/superpowers/plans/2026-10-30-sidebar.md. Source design: docs/superpowers/specs/2026-10-30-sidebar-design.md.
