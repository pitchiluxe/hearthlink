# HearthLink 0.3.0 Relay (Phase 1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Both PCs connect outbound to a Cloudflare relay, so paired PCs see each other as Online and connect from any network, with pairing by a 12-digit code instead of an IP address.

**Architecture:** A Cloudflare Worker with two Durable Object classes (`HostRoom` per host device ID, `PairingIndex` singleton) authenticates each WebSocket by certificate signature and pipes binary messages between controller and host. On the PCs, a new `RelayStream : Stream` carries bytes over `ClientWebSocket`; the existing `SslStream` mutual TLS with pinned fingerprints and the existing `Wire` protocol run unchanged on top. Direct LAN on TCP 45831 stays as a fallback.

**Tech Stack:** .NET 10 Windows Forms (no NuGet packages), `System.Net.WebSockets`; TypeScript Cloudflare Worker + Durable Objects (SQLite-backed), `wrangler`, `vitest` with `@cloudflare/vitest-pool-workers`.

**Spec:** `docs/superpowers/specs/2026-10-22-relay-unattended-design.md` (phase 1 only: relay, presence, Online/Offline list, relayed sessions, pairing through the relay, two-way pairing with automatic upgrade, LAN fallback).

## Global Constraints

- Windows 11, .NET 10 (`global.json` SDK 10.0.402), `TreatWarningsAsErrors`, nullable on. No third-party NuGet packages.
- Relay: TypeScript, Cloudflare Workers free plan. No runtime npm dependencies; dev dependencies only (`wrangler`, `vitest`, `@cloudflare/vitest-pool-workers`, `typescript`, `@cloudflare/workers-types`).
- Device ID = existing fingerprint: uppercase hex SHA-256 of the certificate DER (`IdentityStore.FingerprintOf`), 64 characters. Fingerprints must never change.
- Relay auth signature: RSA PKCS#1 v1.5 SHA-256 over UTF-8 `"hearthlink-relay-v1"` + `"\n"` + endpoint path (e.g. `/v1/presence`) + `"\n"` + the 32 raw nonce bytes.
- WebSocket binary messages carry at most 64 KiB (65,536 bytes). Cloudflare's hard cap is 1 MiB.
- Pairing code: 12 digits shown `LLLL-SSSS-SSSS`; `LLLL` = relay lookup (sent to relay), `SSSSSSSS` = existing 8-digit secret (sent only inside TLS). Window 2 minutes; 5 attempts per peer, 50 total (existing `PairingGate`).
- Connect wait: relay waits 15 s for the host to accept.
- `Wire.ProtocolVersion` becomes 3. App/installer version 0.3.0.
- Pairing is two-way. Existing one-way pairings upgrade automatically on the next successful pinned-TLS exchange; no re-pairing.
- Relay stores only per-host allowed lists, presence and open pairing lookups. Nothing about screens, files, codes' secret part or keys.
- If measured relay use approaches free-plan limits: stop and ask the owner. Do not enable paid plans.
- Code style: match the existing dense C# style (expression bodies, compact one-line members, few comments).

## Review Focus

1. **Relay or internet goes down at the host** → presence reconnects with backoff (1, 2, 4 … 60 s), host status says "Relay unreachable • LAN only", and the list falls back to LAN probes. Test: Task 6 `presence reconnects after relay restart`.
2. **Host loses Wi-Fi or sleeps without closing the socket** → it shows Offline within 60 s, not forever Online. Relay marks presence stale when no ping arrives for 60 s; client pings every 20 s. Test: Task 2 `stale presence reports offline`.
3. **One PC on 0.2.x, the other on 0.3.0** → a clear "Update HearthLink on BOTH PCs to 0.3.0" message, never a hang. Test: Task 5 `protocol 2 session gets update message`.
4. **Large or fragmented data** (16 MB max Wire frame; WebSocket reads returning partial messages) → bytes arrive intact and in order. Test: Task 4 `16 MB payload round-trips through 64 KiB messages`.
5. **Second controller while a session is active, or two PCs open pairing with the same lookup** → existing "Host already has an active session" reaches the controller; lookup collision makes the host pick a new lookup. Tests: Task 3 `second session gets busy message`, Task 2 `taken lookup rejected`.

---

## Relay protocol (contract shared by Tasks 1–3 and 6)

All endpoints are WebSocket upgrades. Text frames are JSON `{ "t": <type>, ... }`; binary frames are tunnel bytes.

**Authentication (every endpoint).** On open, relay sends `{"t":"challenge","nonce":"<base64 32 bytes>"}`. Client sends `{"t":"auth","cert":"<base64 DER>","sig":"<base64>"}`. Relay verifies (Global Constraints) and replies `{"t":"ok","id":"<fingerprint>"}`, or closes with code `4401`. Client sends nothing else before `ok`. Auth must finish within 10 s or close `4408`.

| Endpoint | After `ok` |
|---|---|
| `/v1/presence?id=<fp>` (host) | Client → `{"t":"allow","ids":[fp,...]}` (replaces list), `{"t":"pairing","lookup":"1234"}` (2-minute window), `{"t":"pairing-close"}`, `"ping"` text every 20 s. Relay → `"pong"`, `{"t":"pairing-ok"}`, `{"t":"pairing-taken"}`, `{"t":"incoming","session":"<32 hex>","from":fp}`. A new presence for the same ID replaces the old (old closed `4409`). |
| `/v1/status` (controller) | Client → `{"t":"query","ids":[fp,...]}`. Relay → `{"t":"status","online":[fp,...]}`, listing only hosts that are present **and** whose allow list contains the caller. Relay closes after answering. |
| `/v1/connect?to=<fp>` or `/v1/connect?pair=<LLLL>` (controller) | Relay checks caller is in host's allow list (`to`) or the lookup is open (`pair`), sends host `incoming`, waits 15 s. Relay → `{"t":"ready"}` then pipes binary. Close codes: `4403` not allowed, `4404` host offline or lookup unknown, `4408` host did not accept. |
| `/v1/accept?host=<fp>&session=<sid>` (host) | Caller ID must equal `host`. Relay pipes binary with the waiting controller. Unknown/expired session: close `4404`. |

Either side closing a piped socket closes the other (code `1000`).

## File structure

```
relay/
  package.json, tsconfig.json, wrangler.toml, vitest.config.ts
  src/index.ts          Worker routing: endpoint → Durable Object, /v1/status fan-out
  src/auth.ts           challenge, SubjectPublicKeyInfo extraction from DER, signature verify, fingerprint
  src/host-room.ts      HostRoom DO: presence, allow list, pairing lookup, session rendezvous + pipe
  src/pairing-index.ts  PairingIndex DO (name "index"): lookup → host fingerprint with expiry
  test/fixtures/        test-only RSA-3072 cert + key (PEM), generated once
  test/*.test.ts
src/HearthLink.Core/
  RelayProtocol.cs      endpoint names, close codes, SignatureInput(), PairingCode
  RelayStream.cs        Stream over System.Net.WebSockets.WebSocket, 64 KiB messages
src/HearthLink/
  Transport.cs          (modify) Connection.Secure(Stream, ...) used by TCP and relay
  HostServer.cs         (modify) Serve(Stream, string peer) for TCP and relay
  RelayClient.cs        authenticated sockets, Connect/Pair/Status, RelayPresence loop
  IdentityStore.cs      (modify) Settings.RelayUrl, TrustBothWays()
  MainWindow.cs         (modify) code pairing, relay-first connect, relay status list
tests/HearthLink.Tests/Program.cs          core checks
tests/HearthLink.WindowsTests/RelayChecks.cs  integration against `wrangler dev`
```

---

### Task 1: Relay project and signature authentication

**Files:**
- Create: `relay/package.json`, `relay/tsconfig.json`, `relay/wrangler.toml`, `relay/vitest.config.ts`, `relay/src/auth.ts`, `relay/src/index.ts` (stub routing returning 404 for unknown paths), `relay/test/fixtures/{cert.pem,key.pem,other-cert.pem,other-key.pem}`, `relay/test/auth.test.ts`
- Modify: `.gitignore` (add `relay/node_modules/`, `relay/.wrangler/`)

**Interfaces:**
- Produces (`relay/src/auth.ts`):
  - `newChallenge(): { nonce: Uint8Array; message: string }`
  - `verifyAuth(endpoint: string, nonce: Uint8Array, certB64: string, sigB64: string): Promise<string | null>` → fingerprint (uppercase hex) or `null`
  - `fingerprintOf(der: Uint8Array): Promise<string>`
  - `spkiFromCertificate(der: Uint8Array): Uint8Array` (throws on malformed DER)
  - `authenticate(ws: WebSocket, endpoint: string): Promise<string | null>` — runs the challenge exchange with the 10 s limit, closes `4401`/`4408` on failure.
- `wrangler.toml`: `name = "hearthlink-relay"`, `main = "src/index.ts"`, `compatibility_date = "2026-10-01"`, Durable Object bindings `HOSTS` → `HostRoom`, `INDEX` → `PairingIndex`, migration tag `v1` with `new_sqlite_classes = ["HostRoom","PairingIndex"]`.

- [ ] **Step 1: Scaffold and install.** Create the files above; `cd relay && npm install` with the dev dependencies from Global Constraints. Generate fixtures with `openssl req -x509 -newkey rsa:3072 -sha256 -days 3650 -nodes -subj "/CN=HearthLink" -keyout test/fixtures/key.pem -out test/fixtures/cert.pem` (repeat for `other-*`). Add `"test": "vitest run"`.
- [ ] **Step 2: Write failing tests** in `relay/test/auth.test.ts`:
  - `fingerprint matches .NET definition`: `fingerprintOf(der)` equals uppercase hex SHA-256 of the fixture DER (compute expected with `crypto.subtle.digest`).
  - `valid signature accepted`: sign `"hearthlink-relay-v1\n/v1/presence\n"+nonce` with `key.pem` (WebCrypto `RSASSA-PKCS1-v1_5`/`SHA-256`) → `verifyAuth` returns the fingerprint.
  - `signature for another endpoint rejected`: signed for `/v1/status`, verified for `/v1/presence` → `null`.
  - `signature by other key rejected`: `cert.pem` with signature from `other-key.pem` → `null`.
  - `malformed certificate rejected`: random bytes as cert → `null` (no throw escapes).
- [ ] **Step 3: Run** `npm test` in `relay/`. Expected: the five tests FAIL (functions missing).
- [ ] **Step 4: Implement `relay/src/auth.ts`.** `spkiFromCertificate` walks the DER: Certificate SEQUENCE → tbsCertificate SEQUENCE → skip optional `[0]` version, serialNumber, signature AlgorithmIdentifier, issuer, validity, subject → return the next element (SubjectPublicKeyInfo) bytes including its tag/length. Import with `crypto.subtle.importKey("spki", ..., {name:"RSASSA-PKCS1-v1_5", hash:"SHA-256"}, false, ["verify"])`.
- [ ] **Step 5: Run** `npm test`. Expected: 5 PASS.
- [ ] **Step 6: Commit** `git add relay .gitignore && git commit -m "Add Cloudflare relay project with certificate signature authentication"`

### Task 2: Presence, allow list and pairing lookups

**Files:**
- Create: `relay/src/host-room.ts`, `relay/src/pairing-index.ts`, `relay/test/presence.test.ts`, `relay/test/helpers.ts`
- Modify: `relay/src/index.ts` (route `/v1/presence?id=<fp>` to `HOSTS.idFromName(fp)`)

**Interfaces:**
- Consumes: `authenticate`, `verifyAuth` (Task 1).
- Produces:
  - `relay/test/helpers.ts`: `openAuthed(path: string, keyPem: string, certPem: string): Promise<WebSocket>` (test client doing the challenge), `nextJson(ws): Promise<any>`.
  - `HostRoom` internal RPC: `isOnlineFor(caller: string): Promise<boolean>`, `hasLookup(lookup: string): boolean`.
  - `PairingIndex` RPC: `claim(lookup: string, host: string, until: number): Promise<boolean>`, `resolve(lookup: string): Promise<string | null>`, `release(lookup: string, host: string): Promise<void>`.
- Routing: a WebSocket cannot be handed to a Durable Object after the Worker has accepted it, so the host names its ID in the URL: `/v1/presence?id=<fp>` (and `/v1/accept?host=<fp>&session=…`). The Worker forwards the upgrade request to `HOSTS.idFromName(fp)`; `HostRoom` runs `authenticate` and closes `4401` if the authenticated fingerprint ≠ the named ID. This adds `?id=` to the presence row of the protocol table; Task 6 uses it.

- [ ] **Step 1: Write failing tests** (`relay/test/presence.test.ts`, using `SELF.fetch` upgrades from `cloudflare:test`):
  - `presence goes online for allowed caller`: host opens presence, sends `allow:[controllerFp]`; `isOnlineFor(controllerFp)` → `true`, `isOnlineFor(strangerFp)` → `false`.
  - `presence with wrong id rejected`: `?id=` set to another fingerprint → closed `4401`.
  - `replacement presence closes old socket`: second presence for same host → first closes `4409`.
  - `stale presence reports offline`: with the test clock advanced 61 s and no `"ping"` → `isOnlineFor` → `false`; `"ping"` gets `"pong"`.
  - `pairing lookup registered`: `{"t":"pairing","lookup":"1234"}` → `pairing-ok`; `PairingIndex.resolve("1234")` → host fp.
  - `taken lookup rejected`: a second host claims `1234` within 2 min → `pairing-taken`.
  - `lookup expires`: after 121 s `resolve("1234")` → `null`.
- [ ] **Step 2: Run** `npm test`. Expected: new tests FAIL.
- [ ] **Step 3: Implement** `HostRoom` with the WebSocket Hibernation API (`state.acceptWebSocket`, `setWebSocketAutoResponse(new WebSocketRequestResponsePair("ping","pong"))`, `getWebSocketAutoResponseTimestamp` for staleness), allow list in `state.storage`, and `PairingIndex` with entries stored as `{host, until}`.
- [ ] **Step 4: Run** `npm test`. Expected: all PASS.
- [ ] **Step 5: Commit** `git commit -am "Relay presence, allow lists and pairing lookups"` (add new files first).

### Task 3: Session rendezvous, piping and status

**Files:**
- Modify: `relay/src/host-room.ts`, `relay/src/index.ts`
- Create: `relay/test/session.test.ts`

**Interfaces:**
- Consumes: Task 2 helpers and RPCs.
- Produces: `/v1/connect`, `/v1/accept`, `/v1/status` exactly as in the protocol table.

- [ ] **Step 1: Write failing tests:**
  - `allowed controller connects and bytes flow both ways`: host presence + allow; controller `/v1/connect?to=` → host receives `incoming` with `from`=controller fp; host opens `/v1/accept`; controller gets `ready`; a 64 KiB binary message each way arrives byte-identical.
  - `stranger connect refused`: not in allow list → `4403`.
  - `offline host`: no presence → `4404`.
  - `host never accepts`: advance 16 s → `4408`.
  - `pairing connect by lookup`: open lookup `1234`; stranger `/v1/connect?pair=1234` → host `incoming` (with `from` stranger), pipe works.
  - `accept for someone else's session refused`: other identity opens `/v1/accept?host=<hostFp>` → `4401`.
  - `closing one side closes the other`: controller closes → host data socket closes `1000`.
  - `second session gets busy message`: the relay allows a second pipe; the host app is what answers "busy", so this test only asserts the relay creates a second, independent session ID (no cross-talk between pipes).
  - `status lists only hosts that allow the caller`: two hosts online, one allows caller → `status.online` = that one.
- [ ] **Step 2: Run** `npm test`. Expected: FAIL.
- [ ] **Step 3: Implement.** Pending sessions held in memory in `HostRoom` (`Map<sid, {controller: WebSocket, timer}>`, sid = 16 random bytes hex). Data sockets are accepted without hibernation (they are active while piping). `/v1/status`: Worker accepts the socket, authenticates, then calls `isOnlineFor(caller)` on each queried host stub (max 64 IDs per query; extra IDs ignored).
- [ ] **Step 4: Run** `npm test`. Expected: all PASS.
- [ ] **Step 5: Commit** `git commit -am "Relay session rendezvous, binary piping and status"`

### Task 4: Core relay protocol and RelayStream

**Files:**
- Create: `src/HearthLink.Core/RelayProtocol.cs`, `src/HearthLink.Core/RelayStream.cs`
- Modify: `tests/HearthLink.Tests/Program.cs`

**Interfaces:**
- Produces:
  - `public static class RelayProtocol` with `const string Presence="/v1/presence", Status="/v1/status", Connect="/v1/connect", Accept="/v1/accept"`, `const int MessageBytes=65536`, close codes `NotAuthenticated=4401, Forbidden=4403, Offline=4404, Timeout=4408, Replaced=4409`, and `static byte[] SignatureInput(string endpoint, ReadOnlySpan<byte> nonce)`.
  - `public readonly record struct PairingCode(string Lookup, string Secret)` with `static PairingCode New(string secret)` (random 4-digit lookup via `RandomNumberGenerator`), `string Display` → `"LLLL-SSSS-SSSS"`, `static bool TryParse(string text, out PairingCode code)` (accepts 12 digits with optional spaces or dashes; rejects anything else).
  - `public sealed class RelayStream(WebSocket socket) : Stream` — `CanRead/CanWrite` true, `CanSeek` false; `WriteAsync` sends binary messages of ≤ `MessageBytes`; `ReadAsync` returns bytes from the current message, reading the next on exhaustion, returning 0 on close; `Dispose` closes with `1000`. Sync `Read`/`Write` throw `NotSupportedException` (SslStream uses async paths).
- [ ] **Step 1: Write failing checks** in `tests/HearthLink.Tests/Program.cs` (existing `Check` style). Use a loopback `TcpListener` pair wrapped with `WebSocket.CreateFromStream(stream, isServer, null, TimeSpan.FromSeconds(30))`:
  - `relay signature input is domain separated`: `SignatureInput("/v1/presence", nonce)` equals `UTF8("hearthlink-relay-v1\n/v1/presence\n")+nonce` and differs from the `/v1/status` input.
  - `pairing code display and parse`: `new PairingCode("0420","12345678").Display=="0420-1234-5678"`; `TryParse("0420 1234 5678")` and `TryParse("042012345678")` succeed; `TryParse("0420-1234-567")`, `TryParse("abcd-1234-5678")` fail.
  - `16 MB payload round-trips through 64 KiB messages`: write `Wire.MaxLength` random bytes in one `WriteAsync`; the receiver reads them back identical; a counting wrapper confirms no message exceeded 65,536 bytes.
  - `relay stream carries mutual TLS`: `SslStream` client/server over two `RelayStream`s complete `AuthenticateAs…` with test certificates and exchange one `Wire` packet.
  - `closed relay stream reads zero`: peer `Dispose` → `ReadAsync` returns 0.
- [ ] **Step 2: Run** `dotnet run --project tests/HearthLink.Tests -c Release`. Expected: build FAIL (types missing).
- [ ] **Step 3: Implement** both files.
- [ ] **Step 4: Run** the same command. Expected: all checks PASS (47 existing + 5 new).
- [ ] **Step 5: Commit** `git commit -m "Add relay protocol constants, pairing codes and RelayStream"`

### Task 5: Transport and host over any Stream

**Files:**
- Modify: `src/HearthLink/Transport.cs`, `src/HearthLink/HostServer.cs`, `src/HearthLink.Core/Wire.cs` (`ProtocolVersion=3`), `tests/HearthLink.WindowsTests/Program.cs`

**Interfaces:**
- Produces:
  - `Connection(Stream transport, SslStream stream)` replaces `Connection(TcpClient, SslStream)`; `Dispose` disposes both. Remove the public `Tcp` field (callers use `NoDelay` only at creation).
  - `static Task<Connection> Connection.Secure(Stream transport, IdentityStore identity, string? expected, Func<string,bool>? confirm, CancellationToken token)` — the TLS client half of today's `Connect`. `Connect(address, …)` keeps its signature and error messages and calls `Secure`.
  - `Task HostServer.Serve(Stream transport, string peer)` — today's `Serve` body minus TCP specifics; `peer` is the pairing-attempt key (`MapToIPv4` logic stays in the TCP accept path; relay passes `"relay:"+fromFingerprint`).
  - `HostServer.ServeRelay(Stream transport, string fromFingerprint)` → `Serve(transport, "relay:"+fromFingerprint)` behind the same `clients` semaphore.
  - Session version message becomes `"Update HearthLink on BOTH PCs to version 0.3.0 or later. Existing pairing is preserved."` on both sides.
- [ ] **Step 1: Write failing Windows checks:** `HostServer serves a session over an in-memory relay stream` (loopback WebSocket pair from Task 4 → `ServeRelay` → trusted `probe` returns `online`); `protocol 2 session gets update message` (send `session` with `Value:2` → reply `denied` whose `Text` contains `0.3.0`).
- [ ] **Step 2: Run** `dotnet run --project tests/HearthLink.WindowsTests -c Release` (close any running HearthLink first). Expected: FAIL.
- [ ] **Step 3: Implement** the refactor. Behavior over TCP must be unchanged.
- [ ] **Step 4: Run** both test projects. Expected: all PASS, including the 17 existing Windows checks.
- [ ] **Step 5: Commit** `git commit -m "Run transport and host over any stream; protocol 3"`

### Task 6: RelayClient and presence loop

**Files:**
- Create: `src/HearthLink/RelayClient.cs`, `tests/HearthLink.WindowsTests/RelayChecks.cs`
- Modify: `tests/HearthLink.WindowsTests/Program.cs` (call `RelayChecks.Run` when env `HEARTHLINK_RELAY_URL` is set; print `SKIP: relay checks (HEARTHLINK_RELAY_URL not set)` otherwise), `src/HearthLink/IdentityStore.cs` (`Settings.RelayUrl`, default `RelayClient.DefaultUrl`)

**Interfaces:**
- Consumes: `RelayProtocol`, `RelayStream`, `PairingCode` (Task 4); `Connection.Secure`, `HostServer.ServeRelay` (Task 5).
- Produces (`RelayClient.cs`):
  - `public static class RelayClient` with `const string DefaultUrl="wss://hearthlink-relay.REPLACE.workers.dev"` (set in Task 9),
    `Task<ClientWebSocket> Open(string relayUrl, string endpointWithQuery, IdentityStore identity, CancellationToken token)` (does the challenge, throws `RelayException(int closeCode, string message)` on failure),
    `Task<Connection> ConnectHost(string relayUrl, IdentityStore identity, string hostFingerprint, CancellationToken token)`,
    `Task<Connection> ConnectForPairing(string relayUrl, IdentityStore identity, PairingCode code, Func<string,bool> confirm, CancellationToken token)`,
    `Task<HashSet<string>> Online(string relayUrl, IdentityStore identity, IEnumerable<string> hosts, CancellationToken token)`.
  - `public sealed class RelayPresence(IdentityStore identity, Func<IReadOnlyList<string>> allowed, Func<Stream,string,Task> incoming) : IDisposable` with `void Start()`, `Task PublishAllowed()`, `Task<bool> OpenPairing(string lookup)` (true on `pairing-ok`), `Task ClosePairing()`, `event Action<string>? StateChanged` (`"Online through relay"`, `"Connecting to relay…"`, `"Relay unreachable • LAN only"`). Reconnect backoff 1, 2, 4 … 60 s; ping every 20 s; on `incoming` opens `/v1/accept` and calls `incoming(new RelayStream(socket), from)` on a background task.
  - `RelayException` maps close codes to user text: `4403` "This PC is not paired with that computer.", `4404` "That computer is offline or the pairing code has expired.", `4408` "That computer did not answer. Check HearthLink is running there."
- [ ] **Step 1: Write failing integration checks** in `RelayChecks.cs` (run with `cd relay && npx wrangler dev --port 8787` in the background and `HEARTHLINK_RELAY_URL=ws://127.0.0.1:8787`):
  - `relay presence shows host online to allowed controller`, `relay hides host from stranger`,
  - `relayed probe uses pinned TLS end to end` (`ConnectHost` + `probe` → `online`; `Stream.IsMutuallyAuthenticated`),
  - `relayed connect to wrong fingerprint fails TLS` (host presence under its real ID, controller expects another fingerprint → `AuthenticationException`),
  - `pairing through relay by code` (host `OpenPairing(lookup)` + `PairingGate`; controller `ConnectForPairing` → `paired`),
  - `presence reconnects after relay restart` (stop and restart the `wrangler dev` process from the check; state returns to `Online through relay` within 10 s of the relay coming back),
  - `second relayed session gets busy message` (one session active through the relay; a second controller's `session` request gets `denied` with `Text` "Host already has an active session.").
- [ ] **Step 2: Run** Windows tests with the env var set. Expected: FAIL. Without the env var: `SKIP` line and the rest PASS.
- [ ] **Step 3: Implement** `RelayClient.cs`. Relay URL scheme `wss://` required except `ws://127.0.0.1` and `ws://localhost` (tests).
- [ ] **Step 4: Run** again with and without the env var. Expected: PASS / SKIP.
- [ ] **Step 5: Commit** `git commit -m "Add relay client and host presence loop"`

### Task 7: Two-way pairing and automatic upgrade

**Files:**
- Modify: `src/HearthLink/IdentityStore.cs`, `src/HearthLink/MainWindow.cs` (`PairController`), `src/HearthLink/HostServer.cs` (pair reply and probe/session success), `tests/HearthLink.WindowsTests/Program.cs`

**Interfaces:**
- Produces:
  - `bool IdentityStore.TrustBothWays(string fingerprint, string name, string? lanAddress)` — adds the fingerprint to `Config.Controllers` (if missing) and to `Config.Hosts` (if missing, `Peer(PeerName.Clean(name) or "Paired PC", lanAddress ?? "", fingerprint)`); returns `true` if anything changed; saves when changed. Never changes an existing entry's name or address except filling an empty address.
  - Pair success on the host calls `TrustBothWays(controllerFp, controllerName, lanAddressOrNull)`; the controller, on `paired`, calls `TrustBothWays(hostFp, hostName, typedLanAddressOrNull)`.
  - Upgrade: after any successful pinned-TLS `probe`/`session` where the other side is already trusted in one direction, both sides call `TrustBothWays`. The host does it after verifying the controller is in `Controllers`; the controller after the pinned handshake to a host in `Hosts`. Write `"trust-upgraded"` to history when it changes something.
  - Prompt text on host: `"Trust this PC both ways?\n\nPC name: {name}\nCompare this fingerprint with the other PC's own identity:\n{fingerprint}\n\nEach PC will be able to connect to the other. The other PC will still need approval for each session."`
  - Revoke removes the fingerprint from both `Controllers` and `Hosts`. Forget does the same (rename both buttons to **Remove PC** in Task 8).
- [ ] **Step 1: Write failing Windows checks:** `pairing stores both directions`; `one-way pairing upgrades on next probe` (host has controller in `Controllers` only, controller has host in `Hosts` only; after a probe each side has both); `upgrade never trusts an unpaired PC` (stranger probe changes nothing); `remove PC clears both roles`.
- [ ] **Step 2: Run** Windows tests. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** both test projects. Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "Two-way pairing with automatic upgrade of one-way pairings"`

### Task 8: Dashboard over the relay

**Files:**
- Modify: `src/HearthLink/MainWindow.cs`, `src/HearthLink/ViewerWindow.cs` (only if the viewer touches `Connection.Tcp`)

**Interfaces:**
- Consumes: Tasks 4–7.
- Behaviour to implement:
  - Hosting starts a `RelayPresence` whenever **Allow incoming sessions** is on; the TCP listener starts only if the user also enables **Also accept direct connections on my network** (new checkbox, off by default, saved as `Settings.LanListener`). Allowed list = all fingerprints in `Controllers`. Host status label shows `RelayPresence` state.
  - **Open pairing for 2 minutes** shows `PairingCode.Display` (calls `OpenPairing`; on `pairing-taken` generates a new lookup, up to 5 tries). Closing or expiry calls `ClosePairing`.
  - Pair box placeholder: `"Pairing code (1234-5678-9012) or LAN address"`. Input that `PairingCode.TryParse` accepts uses `ConnectForPairing`; anything else uses the existing LAN `PairHost` path.
  - **Connect** tries `RelayClient.ConnectHost` first; on `RelayException` with `Offline`/network failure and a non-empty `Peer.Address`, tries LAN `Connection.Connect`; final error text explains both (`"Not reachable through the relay or on your network. Check HearthLink is running on {name}."`).
  - Refresh: one `RelayClient.Online` call for all hosts every 10 s; hosts not online through the relay but with an address get the existing LAN probe. Status column values: `Online`, `Online (network)`, `Offline`, `Trust revoked`, `Identity mismatch`.
  - Footer text: `"Encrypted end to end  •  You approve access  •  Works on any network"`.
- [ ] **Step 1: Manual check list written into `docs/ACCEPTANCE.md`** (new "0.3.0 relay" section) covering each bullet above; this task is UI wiring over tested parts, so its gate is the Windows checks plus that list.
- [ ] **Step 2: Implement.**
- [ ] **Step 3: Run** both test projects (relay checks with `wrangler dev`). Expected: all PASS.
- [ ] **Step 4: Walk the ACCEPTANCE list on this PC.** At startup, if env `HEARTHLINK_RELAY_URL` is set it overrides `Settings.RelayUrl` for that run (not saved). Start `wrangler dev`, run `dotnet run --project src/HearthLink -c Release` with the env var set, check each item that one PC can show (hosting status, pairing code display and expiry, pair-box parsing, LAN fallback text), and record results in `docs/ACCEPTANCE.md`. Two-PC items wait for Task 9.
- [ ] **Step 5: Commit** `git commit -m "Dashboard pairs, lists and connects through the relay"`

### Task 9: Version 0.3.0, docs, deploy and acceptance

**Files:**
- Modify: `src/HearthLink/HearthLink.csproj` (`<Version>0.3.0</Version>`), `installer/HearthLink.iss` (`AppVersion` and `OutputBaseFilename` 0.3.0; firewall task text `"Also allow direct connections on Private networks (TCP 45831, local subnet only) — not needed for normal use"`), `src/HearthLink/MainWindow.cs` (sidebar label `Attended test build 0.3.0`), `src/HearthLink/RelayClient.cs` (`DefaultUrl`), `README.md`, `docs/ARCHITECTURE.md` (replace "No rendezvous, relay…" with the relay trust boundary from the spec's Security summary), `docs/UPDATES.md`, `website/download.html` FAQ "Can I connect across the internet?" → yes, through the encrypted relay.

- [ ] **Step 1: Owner action.** Ask the owner to create the Cloudflare account (if not done) and run `cd relay && npx wrangler login`, approving in the browser. Do not proceed without it.
- [ ] **Step 2: Deploy** `cd relay && npx wrangler deploy`. Expected: output lists `https://hearthlink-relay.<subdomain>.workers.dev`. Set `RelayClient.DefaultUrl` to its `wss://` form.
- [ ] **Step 3: Run relay checks against production**: `HEARTHLINK_RELAY_URL=wss://hearthlink-relay.<subdomain>.workers.dev dotnet run --project tests/HearthLink.WindowsTests -c Release`. Expected: all PASS.
- [ ] **Step 4: Version bump and docs** as listed above.
- [ ] **Step 5: Full build** `powershell -ExecutionPolicy Bypass -File .\build.ps1 -SelfContained`. Expected: all core and Windows checks pass; `artifacts/HearthLink-0.3.0-win-x64-setup.exe` created.
- [ ] **Step 6: Commit** `git commit -m "Release 0.3.0: connections through the HearthLink relay"` and push.
- [ ] **Step 7: Owner acceptance on two PCs** (spec "Testing and acceptance"): install 0.3.0 on TECHNODESK (Wi-Fi left Public) and TECHNOBIZ-ASUS on a phone hotspot. Existing pairing upgrades without re-pairing; device list shows Online; connect, view, control; disconnect; pair a fresh profile by 12-digit code; turn the relay URL to an invalid host and confirm LAN fallback message. Record in `docs/VERIFICATION.md`.
- [ ] **Step 8: Measure relay use** after acceptance in the Cloudflare dashboard (Workers requests, Durable Object duration). If projected daily use exceeds 50% of a free-plan limit, stop and report numbers to the owner.
- [ ] **Step 9: Publish GitHub release v0.3.0** only after the owner confirms acceptance (installer + single-entry `SHA256SUMS.txt` + source ZIP, as for v0.2.2).
