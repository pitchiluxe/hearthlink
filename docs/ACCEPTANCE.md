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

## Future release acceptance (not implemented; not passable yet)

- [ ] Discovery displays real untrusted devices separately and grants no access.
- [ ] Administrator enables unattended mode explicitly for a paired controller; disabling it restores attended consent.
- [ ] Reboot host and verify configured service/agent startup, visible indicator and termination without weakening Windows protections.
- [ ] Text clipboard works both ways only when enabled; disabling stops it immediately.
- [ ] Transfer a test file to an explicitly selected destination. Verify contents, progress, cancellation, partial-file cleanup, overwrite confirmation, denied paths and access permissions.
- [ ] Remote restart requires confirmation; reconnect after reboot obeys authorization and consent settings.
- [ ] Uninstall removes service, startup registration and all created firewall rules.
