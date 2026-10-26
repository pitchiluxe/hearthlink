# HearthLink relay, unattended access and file transfer: design

Date: 2026-10-22. Status: approved by the owner on 2026-10-22.

## Intent

HearthLink should work like TeamViewer between the owner's own PCs. Once two PCs are paired, the owner opens HearthLink on one, sees the other as Online, and connects with one click, whether both are at home or the owner is away on any network (hotel Wi-Fi, phone hotspot, office). Files can be copied both ways during a session.

Why now: in 0.2.2 the controller must open a direct TCP connection to the host on port 45831. On the owner's network that fails ("Could not reach TECHNODESK on TCP 45831 within 5 seconds") because the Wi-Fi profile is Public and Windows Firewall drops all inbound traffic. Direct connections also cannot cross the internet or home routers. TeamViewer avoids this by having both PCs connect outward to a cloud service.

Success, in the owner's words: "once the PC is paired from home, even away, the user should still connect because they have already been paired", with one click.

## Users

Only the owner, connecting between PCs they own (today TECHNOBIZ-ASUS and TECHNODESK). Not a support tool for strangers: there is no TeamViewer-style "read me your ID and password" flow.

## Goals

1. **Outbound-only connections.** Both PCs connect out to a relay over HTTPS/WSS (port 443). No inbound firewall rule, port forward, IP address or Private network profile is needed.
2. **Works on any network**, including across the internet.
3. **Pair once, connect forever.** A PC paired in person stays trusted from any network until revoked. Being paired is the permission.
4. **One-click connect** from a paired PC. No extra prompt on the controller (owner's decision).
5. **Unattended access**: a host can be reached with nobody at it, including when it is locked or has just restarted (the last item is the final phase).
6. **Device list** shows each paired PC as Online or Offline, wherever the owner is.
7. **File transfer** both ways during a session.
8. **Existing pairings survive.** Upgrading must not change any PC's fingerprint or force re-pairing.

## Non-goals

- Connecting to PCs the owner has not paired, or to other people's PCs.
- Accounts, logins or passwords on the relay.
- Audio, clipboard sync, remote printing, multi-user hosting.
- Direct peer-to-peer hole punching (a later optimisation to cut relay traffic).
- Pairing a new PC while away (owner chose to pair only at home).
- Mobile apps.

## Anti-goals (failure even if it "works")

- The relay can read, alter or inject session content (screen, input, files), or impersonate either PC.
- An unpaired device obtains a screen image, input, files, or the host's device list.
- Unattended access becomes enabled without someone physically at that host verifying with Windows Hello.
- Any inbound firewall rule or port opening is required for normal use.
- A session happens without a visible indication on the host.
- Upgrading changes fingerprints or silently drops pairings.

## Decisions made with the owner

| Decision | Owner's choice |
|---|---|
| Who it's for | Only the owner's own PCs |
| Away-from-home access | Required, over the internet |
| Trust model | Pair in person once; paired PCs connect from anywhere until revoked |
| Controller verification per connect | None: one click (owner accepted that the controller's own Windows sign-in is the only lock) |
| Pairing while away | Not needed |
| Relay hosting | Cloudflare Workers on a free account the owner will create |
| File transfer | Required, both directions |
| Pairing direction | **Both ways**: one pairing lets each PC connect to the other. Existing one-way 0.2.x pairings are upgraded automatically the next time the two PCs connect (each adds the other to the list it was missing), with no re-pairing. Every PC therefore keeps relay presence open as a host and sends the relay its full paired list as its allowed list. The pairing prompt reads "Trust this PC both ways?". Revoking a PC removes it in both roles. |
| Free-limit overrun | **Stop and ask.** If measured use approaches Cloudflare's free limits, the builder pauses and reports the numbers; nothing that costs money is turned on without the owner. |

## Design calls made by Claude (owner may override)

- Unattended is a **per-host switch, off by default**. Turning it on or off requires Windows Hello (PIN, face or fingerprint) at that host. Every Windows 11 PC can set up a Hello PIN.
- When unattended is on, **every paired controller** may connect unattended, with keyboard and mouse control. No per-controller toggle.
- **30-minute idle disconnect** (no input from the controller) for unattended sessions.
- **Pairing also goes through the relay**, so it no longer needs IP addresses or inbound ports. The in-person part stays the fingerprint comparison on both screens.
- **Direct LAN connection is kept as a fallback** for paired PCs when the relay is unreachable (for example, internet down at home), using the existing 0.2.x code path and last-known LAN address.
- The relay URL is built in but overridable in settings, so the relay can move to a ~$5/month VPS later without an app redesign.
- Delivered in **four phases**, each a normal release through the existing auto-updater.
- **Revocation happens at the host**: in person, or through a remote session from another paired PC (connect to the host, click Revoke there). No new remote-revoke protocol. With only two PCs and a stolen laptop, the owner revokes at the remaining PC when next at it; until then the stolen laptop is usable only while signed in and unlocked.
- **LAN fallback is opt-in, as today**: the host listens on TCP 45831 only when the existing "Allow incoming sessions on my network" listener and its firewall task are on. Unattended auto-accept applies to LAN connections too, since they use the same pinned trust.
- **Phase 4 key access**: the single device identity moves to machine-scope protection whose access list is SYSTEM, Administrators and the Windows user who enabled the service. The per-user app keeps using the same key for controller TLS and relay sign-in, so fingerprints never change. Malware running as that user could already drive the app, so this adds no new exposure.
- **Phase 4 lock screen behaviour**: at the sign-in or lock screen, screen view and input are allowed; file transfer is disabled. File operations always run as the signed-in Windows user (impersonated by the agent), never as SYSTEM. The secure desktop cannot show the normal banner, so the agent shows a small "HearthLink session active" window on it where Windows permits; in all cases the session is written to history and a notification appears at the next sign-in.
- **Phase 4 security review**: Claude reviews the service, agent, pipe and key migration against a written checklist (privilege boundaries, pipe access control, desktop switching, file impersonation, key ACL, uninstall cleanup) before the 0.6.0 release, and the owner signs off. Building can start after phase 3 ships; the review gates release, not coding.

## Architecture

```
 Controller PC                     Cloudflare relay                      Host PC
 HearthLink.exe  ── WSS 443 ──►  Worker + Durable Object  ◄── WSS 443 ──  HearthLink.exe / service
       │                          (per host device ID)                         │
       └──────── mutual TLS (pinned fingerprints) tunnelled inside ───────────┘
                         existing Wire protocol, unchanged
```

The relay is a dumb pipe plus a presence board. All security decisions stay on the PCs:

- Each PC already has an RSA-3072 self-signed certificate. Its **device ID is its existing fingerprint** (SHA-256 of the certificate DER, 64 hex characters), so no IDs change.
- Inside a relayed connection, the PCs run the **same `SslStream` mutual TLS with pinned fingerprints** they use today, and the same `Wire` packets. The relay only ever sees TLS ciphertext.
- Today `Connection` and `HostServer` sit on `TcpClient` + `SslStream`. The change is to put `SslStream` on top of a new `RelayStream : Stream` that carries bytes over a `System.Net.WebSockets.ClientWebSocket` (built into .NET; no NuGet package). `HostServer.Serve` takes a `Stream` (plus a peer label for audit and pairing limits) instead of a `TcpClient`.

### Relay (new `relay/` folder, TypeScript, Cloudflare Worker + one Durable Object class)

One Durable Object instance per **host** device ID. Endpoints (all WebSocket upgrades over `wss://`):

| Endpoint | Who | Purpose |
|---|---|---|
| `/v1/presence` | Host | Long-lived control socket. Marks the host Online; receives "incoming session" notices. Uses the Durable Object WebSocket Hibernation API so an idle host costs no compute time. |
| `/v1/status` | Controller | Asks which of its hosts are Online. Answers only for hosts that list this controller as allowed. |
| `/v1/connect?to=<hostId>` | Controller | Requests a session or a pairing. Relay checks the controller is on the host's allowed list (or a pairing window is open), creates a session ID, notifies the host over its presence socket, and waits up to 15 s. |
| `/v1/accept?session=<sid>` | Host | Host's data socket for that session. Relay then pipes binary messages between the two sockets until either closes. |

**Relay authentication (device ID ownership).** On every socket the relay sends a random 32-byte nonce. The client replies with its certificate DER and an RSA PKCS#1 v1.5 SHA-256 signature over `"hearthlink-relay-v1" || endpoint || nonce`. The relay computes `SHA-256(certificate DER)` as the device ID and verifies the signature with the certificate's public key (WebCrypto; the Worker extracts the SubjectPublicKeyInfo from the DER). This proves key possession, so nobody can register presence as your host or impersonate your controller at the relay. It is a denial-of-service and privacy control; real authorization is still the pinned TLS inside.

**Allowed list.** On presence connect, and whenever pairings change, the host sends the relay its list of paired controller fingerprints. The relay uses it only to decide who may see that host's status or start a session. Fingerprints are public-key hashes, not secrets.

**Message sizing.** Cloudflare caps a WebSocket message at 1 MiB. `RelayStream` splits writes into binary messages of at most 64 KiB; the receiving side concatenates. The relay never parses payloads.

**Relay stores nothing durable** except, per host, its allowed list and current presence. No screen data, files, codes or keys.

### Pairing through the relay

1. Host clicks **Open pairing for 2 minutes**. It shows a 12-digit code `LLLL-SSSS-SSSS`. The first four digits are a lookup number the host registers with the relay for that window (relay rejects a lookup already in use; host retries). The last eight are the existing one-time secret and never reach the relay in clear text.
2. On the controller the owner enters the 12-digit code. The controller asks the relay to connect by lookup number.
3. Inside the pinned TLS tunnel, the controller shows the host's full fingerprint for comparison (as today), sends the 8-digit secret, and the host shows its existing "Trust a new controller?" prompt with the controller's fingerprint.
4. On success both sides store each other; the host pushes its updated allowed list to the relay.

All 0.2.2 pairing protections remain: two-minute window, single use, 5 attempts per peer, 50 in total, cleaned peer names.

### Unattended access

- Host setting **"Let my paired PCs connect when I'm away"**, off by default. Changing it requires a Windows Hello check at the host (`Windows.Security.Credentials.UI.UserConsentVerifier`; needs the app's target framework to become `net10.0-windows10.0.19041.0`, which is not a third-party dependency). If Hello is not set up, the app explains how to add a Windows Hello PIN and leaves the switch off.
- When on, a session request from a paired controller is accepted without the Allow dialog. Off, the existing attended dialog is unchanged.
- The host always shows the existing banner and tray icon with **End session**, plus a Windows notification, writes `unattended-connected` to history, and disconnects after 30 minutes without controller input.
- Revoking a controller ends its session immediately and updates the relay allowed list.
- So the host is reachable while the owner is away, HearthLink starts at sign-in (per-user Run key), and closing the main window minimises it to the tray while hosting is on. Tray **Exit** stops hosting.

### File transfer

- Control sessions only. The existing `SessionFileGrant` ties file permission to the session and the controller's fingerprint.
- **Send files** (controller to host): pick files on the controller; they land in the host's `Downloads\HearthLink` folder.
- **Get files** (host to controller): a simple remote folder browser in the viewer, starting at the host user's profile and listing drives; chosen files land in the controller's `Downloads\HearthLink`.
- Uses the existing `FileChunk` binary framing (64 KiB chunks), `FileReceiver` (temp file, exact length, SHA-256 check, atomic move) and `FilePolicy` (name validation, 2 GiB limit). Progress and cancel in the viewer. Existing files are never overwritten silently; the receiver renames (`name (2).ext`).

### Locked, signed-out and just-restarted hosts (final phase)

The per-user app cannot capture the lock screen or run before sign-in. The final phase adds a Windows service, as TeamViewer does:

- `HearthLinkService` runs as LocalSystem, starts with Windows, and owns the host's relay presence.
- It launches a capture/input agent (`HearthLink.exe --agent`) into the active console session, able to follow the input desktop between the sign-in/lock screen and the user desktop. Service and agent talk over a named pipe restricted to SYSTEM.
- The host identity moves from per-user DPAPI to machine-scope protection in a folder only SYSTEM and Administrators can read. The **existing key is migrated**, so the fingerprint and all pairings stay the same. Migration and enabling the service require administrator approval plus the Windows Hello check.
- ARCHITECTURE.md already says a service needs an authenticated local broker, per-session agent, explicit admin enablement and a separate security review. That review is a gate for this phase.

## Phases (each shippable through the auto-updater)

| Phase | Version | Delivers | Fixes for the owner |
|---|---|---|---|
| 1 | 0.3.0 | Relay deployed; outbound presence; Online/Offline list; relayed sessions; pairing through the relay; LAN fallback | The firewall/Public-network error; connecting away while someone is at the host |
| 2 | 0.4.0 | Unattended switch with Windows Hello; start at sign-in; tray; idle disconnect | Away use while the host is signed in |
| 3 | 0.5.0 | File transfer both ways | File copying |
| 4 | 0.6.0 | Windows service; lock screen and after-restart access; identity migration | Away use when the host is locked or rebooted |

`Wire.ProtocolVersion` increases in phase 1. 0.2.x and 0.3.0 PCs cannot relay to each other; the app tells the owner to update both PCs (the updater makes this one click on 0.2.1+).

## Constraints

- Windows 11, .NET 10 Windows Forms, no third-party NuGet packages (existing project rule). WebSockets and Windows Hello are built into .NET and Windows.
- Relay: TypeScript on Cloudflare Workers with a Durable Object. `wrangler` is a development tool only. No runtime npm dependencies unless the builder finds the SubjectPublicKeyInfo extraction unreasonable by hand.
- Cloudflare free plan. Builder must confirm current limits before building (from memory: about 100,000 Worker requests per day, incoming WebSocket messages billed at 20 per request, Durable Object compute measured in GB-seconds, no egress fees). Estimated personal use, a few hours of streaming a day, should fit. If not, stop and ask the owner; the $5/month Workers plan or a VPS relay are the options to present.
- The relay must be deployable from the repo with one command, and its URL overridable in the app.
- Installer must no longer need the inbound firewall task for normal use. Keep the task, unchecked, only for the LAN fallback.

## Security summary

| Threat | Protection |
|---|---|
| Relay operator or attacker who controls the relay | End-to-end mutual TLS with pinned fingerprints inside the tunnel; relay cannot decrypt, alter or impersonate. Worst case: it blocks or delays connections and sees metadata (which device IDs are online, when, how much data). |
| Stranger on the internet | Must possess a paired private key. Relay refuses status/connect for devices not on the host's allowed list, so strangers can't even reach the host's TLS handshake outside an open pairing window. |
| Someone at the host | Cannot turn on unattended access without Windows Hello. Sees a banner during every session. |
| Stolen controller laptop | Usable only while it is signed in and unlocked (owner's choice). Revoke it at the host, in person or through a session from another paired PC; revocation ends any session immediately and updates the relay allowed list. |
| Guessing a pairing code | 8-digit secret, two-minute window, 5 tries per peer, 50 total, plus host fingerprint approval. |

## Testing and acceptance

- Relay: unit tests with Cloudflare's local Workers test runner, covering signature authentication, allowed-list enforcement, pairing lookups, session pairing and timeouts, and 64 KiB message relay.
- .NET: `RelayStream` chunking and reassembly; relay authentication signing; `HostServer` over a `Stream`; existing 47 core and 17 Windows checks keep passing.
- Integration: two `IdentityStore` instances on one PC talking through `wrangler dev` (local relay), full pinned-TLS session.
- Owner acceptance per phase on two physical PCs: TECHNODESK on home Wi-Fi left Public, TECHNOBIZ-ASUS on a phone hotspot. Phase 4 adds a locked host and a host just after restart.

## Left to the builder

- Exact relay message formats, error codes and reconnect backoff for the presence socket.
- How `RelayStream` handles back-pressure, and idle keep-alive intervals.
- Device-list UI details (Online dot, last seen), file-browser UI layout, progress display.
- Whether status uses its own socket or a short HTTPS request (both must authenticate as above).
- Service/agent implementation details in phase 4, subject to the security review.

## Owner actions needed

1. Create a free Cloudflare account (cloudflare.com, no card needed).
2. When phase 1 is ready to deploy, run `npx wrangler login` once and approve in the browser, so the relay can be deployed to that account.
