# Two-PC acceptance record

Do not mark a test passed without actually performing it. Record PC names, Windows editions/builds, app version, timestamp, observed result and any failures. Use nonsensitive test content. All boxes start unchecked.

## First required milestone: 0.1.1

- [ ] On PC A and PC B, record Windows 11 edition/build, scaling, monitor resolutions and IP addresses. Confirm Private LAN or approved VPN routing.
- [ ] Verify installer SHA-256. Install on both PCs, enabling the optional host firewall rule only where receiving sessions. Record any unsigned publisher warning.
- [ ] Launch under the intended normal user; select host/controller or both in the app. Record names and full identity fingerprints.
- [ ] Before pairing, an unknown controller must be unable to view or control PC B. Pairing identity confirmation is not session authorization. Use a separate Windows user or fresh test PC identity for an independent rejection test.
- [ ] Open PC B's pairing window. Reject an incorrect code. Compare full host and controller fingerprints. Pair with the correct code and approve on PC B.
- [ ] Verify code reuse fails and a newly opened code expires after two minutes.
- [ ] PC A lists the actual enrolled PC B as Online with a current last-seen time; closing B updates it to Unavailable after the probe timeout. Reopen B.
- [ ] Request a session and decline at PC B. Confirm no desktop is shared.
- [ ] Request a view-only session and approve at PC B. Identify an unpredictable fresh visual change on B visible on A. Mouse and keyboard from A must have no effect on B.
- [ ] Disconnect. Request control and approve at B. Open Notepad on B locally. From A, type a unique test sentence, move/click and drag the window. Verify output on B. Record observed responsiveness.
- [ ] Click **Disconnect** on A. Confirm B's visible status returns to ready, remote input stops and the connection closes.
- [ ] Connect again, then choose **End remote session** on B. Confirm A disconnects and further input stops.

**Stop here on failure and report the failing step.** This milestone must pass before expanding unattended access/file transfer. Do not count loopback or automated fixture tests as two physical computers.

## Additional checks on the current build

- [ ] On B with two monitors, choose each in A's viewer and identify the correct actual desktop. Test a monitor left of/above the primary, mixed 100%/150% scaling, resizing and full screen. Verify pointer alignment near corners and letterbox edges.
- [ ] Hold a modifier, disconnect, and confirm B has no stuck key/button. Test focus leaving the viewer.
- [ ] Disconnect the network during a session. Confirm A shows a disconnected status. Restore it, close the viewer, refresh and reconnect with new approval.
- [ ] Close/reopen each app; confirm identities and trust remain. Reboot B, sign in, launch HearthLink manually and confirm trust persists. Automatic reboot startup is **not yet implemented**.
- [ ] Revoke A at B during a session. Confirm the connection terminates and a new session is denied until re-paired.
- [ ] Reinstall/reset a disposable test host identity; confirm A rejects the changed pin. Re-pair only after explicit identity comparison.
- [ ] Lock B or show a secure UAC prompt. Confirm the build does not bypass it. Restore the normal desktop and reconnect if needed. Sign-in, lock and secure UAC control are **unsupported**.
- [ ] Inspect actual session history metadata. Ensure it contains no screen, clipboard, pairing code, keystroke or private key contents.
- [ ] Uninstall B. Confirm the app and `HearthLink Host TCP` firewall rule are removed and no service/startup entry was created. Explicitly remove retained per-user data if desired.

## 0.3.0 relay

One-PC items can be checked against `wrangler dev` (set `HEARTHLINK_RELAY_URL=ws://127.0.0.1:8787` before starting the app; the override is not saved). Two-PC items wait for the deployed relay (plan Task 9).

One PC (TECHNOBIZ-ASUS, Windows 11 Home 10.0.26340, build from `relay-phase1`, local `wrangler dev`, 2026-10-25 18:55–19:08):
- [x] With **Allow incoming sessions** on, the host status shows "Online through relay". Relay stopped: "Relay unreachable • LAN only" within 3 s. Relay restarted: "Online through relay" 0.4 s after it answered again.
- [x] **Also accept direct connections on my network** is off by default. A TCP connect to 127.0.0.1:45831 is refused while it is off and accepted while it is on; status adds "• LAN TCP 45831". The off state survived a restart.
- [x] **Open pairing for 2 minutes** shows a 12-digit code (seen `8533-8160-7959`). Relay down with direct connections on: the 8-digit code marked "LAN only". Relay down with direct connections off: "Relay unreachable • turn on direct connections to pair on your network". After 2 minutes the label read "Pairing is closed" and the relay rejected that code ("That computer is offline or the pairing code has expired.").
- [x] The pair box placeholder reads "Pairing code (1234-5678-9012) or LAN address". `4321 1111-2222` went through the relay (relay's "offline or expired" answer); `no-such-pc.invalid` took the LAN path ("Cannot connect to no-such-pc.invalid on TCP 45831…"). After a restart the box held the LAN text, not a code.
- [ ] Connect to a paired PC that is offline everywhere: the message reads "Not reachable through the relay or on your network. Check HearthLink is running on {name}." *Not checked: this profile has no paired host. Repeat in Task 9.*
- [x] Footer reads "Encrypted end to end  •  You approve access  •  Works on any network". Sidebar reads "Any network". Both removal buttons read **Remove PC**.

Two PCs (Task 9):
- [ ] Pair by 12-digit code through the relay; each PC then lists the other, and the host prompt reads "Trust this PC both ways?".
- [ ] The device list shows `Online` through the relay, `Online (network)` when only the LAN probe answers, and `Offline`, `Trust revoked` or `Identity mismatch` as appropriate; refresh runs every 10 s.
- [ ] **Connect** goes through the relay; with the relay unreachable it falls back to the saved LAN address.
- [ ] **Remove PC** on either side removes the other PC from both lists, and the relay stops showing it as Online to the removed PC.

## File transfer

- [ ] In a control session, **Send files** with three files. Each appears in the host's `Downloads\HearthLink`; the strip shows progress, then "Sent 3 files".
- [ ] **File transfer** opens two panes, local left and remote right. The remote drive dropdown lists every drive of the other PC with free space; pick another drive and a folder. F5 copies selected files and folders each way into the folder open in the other pane, keeping subfolders; F7 makes a folder on either side. The viewer session keeps running while this window covers it. (One-PC check 2026-10-26: both panes, drive dropdown and covered-viewer session confirmed on screen.)
- [ ] **Get files** opens the host's user folder and lists drives. Open a subfolder, go Up, download two files. They appear in this PC's `Downloads\HearthLink`; **Open folder** shows them.
- [ ] Drag three files from Explorer onto the remote screen: all three arrive. Drag a folder with subfolders (one empty): the same tree appears in the other PC's `Downloads\HearthLink`; dragging it again creates `name (2)`. (One-PC checks 2026-10-25: three files, then a folder tree with an empty subfolder plus a loose file, dragged onto a test viewer, arrived complete with exact sizes.)
- [ ] The display and quality menu buttons open and switch; quality offers Responsive, Balanced, Full detail, Sharp and Best.
- [ ] Copy a file over 2 GiB each way: it completes and matches. Copy to a drive with too little space: refused up front with the space needed and available. Open a folder with more than 5,000 files on the remote side: every file is listed.
- [ ] Send a file with the same name twice: the host keeps both, the second as `name (2).ext`.
- [ ] Start a 1 GB transfer and **Cancel**: the strip says "Transfer stopped" and neither PC keeps a partial file.
- [ ] End the session from the host during a transfer: the transfer stops and no partial file remains.
- [ ] A view-only session shows no file buttons.
- [ ] Repeat one send and one get through the relay (PCs on different networks) and on a direct connection; record MB/s for each.

One-PC measurement, 2026-10-25 (TECHNOBIZ-ASUS, both ends on this PC, 50 MiB each way during a live control session): local relay 16–17 MB/s send, 18–20 MB/s get, 4 of 4 runs; deployed relay 4–8 MB/s, but some runs ended when every relay connection was reset by the remote side at once, which the local relay never shows. Not yet seen between two PCs.

## Unattended access (phase 2, 0.4.0)

- [ ] On a host without a Windows Hello PIN, select **Let my paired PCs connect when I'm away**: a message explains how to add a PIN and the box stays unchecked.
- [ ] With Windows Hello set up, select it and cancel the Hello prompt: the box stays unchecked. Select it again and verify: it checks and the host status reads **unattended access on**.
- [ ] From the paired controller, connect: the session starts with no Allow dialog on the host; the host shows the banner with **End session**, the tray icon and a Windows notification; **Session history** shows `unattended-connected`.
- [ ] From a PC that is not paired with the host, try to connect: refused.
- [ ] Leave an unattended session without touching the controller for 30 minutes: it disconnects and history shows `unattended-idle-disconnected`. Moving the mouse in the viewer keeps it open.
- [ ] Remove the controller at the host during an unattended session: the session ends at once.
- [ ] Turn the switch off (Windows Hello again): the next session needs the Allow dialog.

## Future release acceptance (not implemented; not passable yet)

- [ ] Discovery displays real untrusted devices separately and grants no access.
- [ ] Reboot host and verify configured service/agent startup, visible indicator and termination without weakening Windows protections.
- [ ] Text clipboard works both ways only when enabled; disabling stops it immediately.
- [ ] Transfer a test file to an explicitly selected destination. Verify contents, progress, cancellation, partial-file cleanup, overwrite confirmation, denied paths and access permissions.
- [ ] Remote restart requires confirmation; reconnect after reboot obeys authorization and consent settings.
- [ ] Uninstall removes service, startup registration and all created firewall rules.
