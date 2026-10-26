# HearthLink File Transfer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** During a control session, the controller can send files to the host and get files from the host, with progress and cancel, through the relay or a direct connection.

**Architecture:** Files travel on a second pinned-TLS connection to the host (same route as the session: relay or direct), opened with a `files` request that carries the session's `SessionFileGrant` token from the host's `ready` reply. One operation runs at a time on that connection (`list`, `get`, `put`). Chunks use the existing `FileChunk` binary framing; the sender pauses after each 1 MiB window until the receiver acknowledges, so neither the relay nor a slow peer buffers a whole file. Received files go through `FileReceiver` (temp file, exact length, SHA-256, atomic move) into `Downloads\HearthLink` under a name that never overwrites.

**Tech Stack:** .NET 10 Windows Forms, existing `Wire`/`Connection`/`HostServer`, `HearthLink.Core` file types. No packages.

**Spec:** `docs/superpowers/specs/2026-10-22-relay-unattended-design.md`, section "File transfer" (phase 3).

## Global Constraints

- Control sessions only. View-only sessions get no grant.
- Send files: controller → host `Downloads\HearthLink`. Get files: remote folder browser starting at the host user's profile and listing drives; files land in controller `Downloads\HearthLink`.
- `FileChunk` 64 KiB chunks (`FilePolicy.ChunkBytes`), `FilePolicy.MaxFileBytes` 2 GiB, `FilePolicy.ValidName` for every remote-supplied name.
- Existing files are never overwritten: the receiver picks `name (2).ext`, `name (3).ext`, ….
- Progress and cancel in the viewer. Cancel removes the partial file.
- Flow control: at most 1 MiB (16 chunks) unacknowledged per transfer.
- The grant ends with the session: a files connection closes when the session ends, and a new `files` request is refused.
- Code style: dense existing C# style, `TreatWarningsAsErrors`, nullable on. `Wire.ProtocolVersion` stays 3 (0.3.0 is unreleased).
- `Downloads` = `%USERPROFILE%\Downloads` (no known-folder API dependency); folders created on first use.

## Review Focus

1. **Session ends mid-transfer** (Disconnect, host "End remote session", revoke) → transfer stops, partial file removed on the receiving side, viewer shows "Transfer stopped". Test: Task 3 `files connection closes when the session ends`.
2. **Same name already in Downloads\HearthLink** → saved as `name (2).ext`, original untouched. Test: Task 1 `unique name never overwrites`.
3. **Remote path or name tricks** (`..\`, absolute names, reserved names in `put`; listing a path that vanished) → refused with a message, nothing written outside `Downloads\HearthLink`. Test: Task 2 `put rejects unsafe names`, `list of a missing folder is refused`.
4. **File over 2 GiB or unreadable** (locked, access denied) → refused before any bytes move, clear text. Test: Task 2 `get refuses an oversized or unreadable file`.
5. **Corrupted or truncated stream** → `FileReceiver` digest/length check fails, no file appears. Test: Task 2 `tampered digest leaves no file`.

## File structure

```
src/HearthLink.Core/
  FileNames.cs        (new) Unique(folder, name) and FileEntry listing JSON
src/HearthLink/
  FileLink.cs         (new) FileLink.Send/Receive (windowed chunks), FileHost (host handler), FileClient (controller ops)
  HostServer.cs       (modify) grant on control sessions, "files" request, ReceiveFolder
  MainWindow.cs       (modify) pass a files-connection opener and grant token to Viewer
  ViewerWindow.cs     (modify) Send files / Get files buttons, transfer strip
  RemoteFilesWindow.cs (new) host folder browser
tests/HearthLink.Tests/Program.cs, tests/HearthLink.WindowsTests/FileChecks.cs (new), Program.cs (call FileChecks)
```

---

### Task 1: Unique names and listing entries (core)

**Files:** Create `src/HearthLink.Core/FileNames.cs`; Test `tests/HearthLink.Tests/Program.cs`

**Interfaces — Produces:**
- `public static class FileNames { public static string Unique(string folder,string name); }` — returns a full path in `folder` that does not exist: `name`, then `stem (2).ext`, `stem (3).ext`… up to 9999, then throws `IOException`. `name` must satisfy `FilePolicy.ValidName` (else `InvalidDataException`).
- `public sealed record FileEntry(string Name,bool Folder,long Size,DateTimeOffset Modified);` and `public static string FileNames.Encode(IEnumerable<FileEntry>)` / `IReadOnlyList<FileEntry> FileNames.Decode(string json)` (System.Text.Json; Decode drops entries whose `Name` fails `FilePolicy.ValidName` or has negative `Size`, max 5000 entries).

- [ ] **Step 1: Failing checks:** `unique name never overwrites` (temp folder with `report.pdf` and `report (2).pdf` → `Unique` returns `…\report (3).pdf`; `notes` without extension → `notes (2)`); `unique name refuses unsafe names` (`..\x.txt` throws `InvalidDataException`); `listing round trips and drops unsafe names` (encode `[a.txt, Docs(folder), ..\evil]` with `..\evil` forced into the JSON → decode returns 2 entries).
- [ ] **Step 2:** `dotnet run --project tests/HearthLink.Tests -c Release` → build FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Same command → all PASS.
- [ ] **Step 5:** Commit `Add unique receive names and file listing entries`.

### Task 2: File link protocol

**Files:** Create `src/HearthLink/FileLink.cs`, `tests/HearthLink.WindowsTests/FileChecks.cs`; Modify `tests/HearthLink.WindowsTests/Program.cs` (call `FileChecks.Run(root, Check)` before the relay section).

**Interfaces — Consumes:** `Connection`, `Wire`, `FileChunk`, `FileReceiver`, `FilePolicy`, Task 1.
**Produces:**
- Messages on a files connection (all `Packet`): controller `list` (`Text`=folder path, `""` = roots), host `listing` (`Text`=folder, `Data`=`FileNames.Encode`) or `denied` (`Text`=reason); controller `get` (`Text`=full path) → host `file` (`Name`, `Sequence`=length) then chunks then `file-end` (`Code`=SHA-256 hex), or `denied`; controller `put` (`Name`, `Sequence`=length) → host `go` or `denied`, then controller chunks then `file-end` → host `saved` (`Name`=saved file name) or `denied`. Receiver sends `file-ack` (`Sequence`=bytes received) after every 1 MiB and at the end; sender never has more than 1 MiB unacknowledged.
- `public static class FileLink { public const int Window=1024*1024; static Task Send(Connection c,Stream source,long length,IProgress<long>? progress,CancellationToken t); static Task<string> Receive(Connection c,string destination,long length,IProgress<long>? progress,CancellationToken t); }` — Send returns after sending `file-end`; Receive commits via `FileReceiver` and returns the final path.
- `public sealed class FileHost(string receiveFolder)` with `Task Serve(Connection c,CancellationToken lifetime)` — loops over requests until the connection or `lifetime` ends. Roots for `list ""`: the user profile, then each ready drive root.
- `public sealed class FileClient(Connection c) : IDisposable` with `Task<(string Folder,IReadOnlyList<FileEntry> Entries)> List(string folder,CancellationToken t)`, `Task<string> Get(string remotePath,string localFolder,IProgress<long>? p,CancellationToken t)` (returns saved local path), `Task<string> Put(string localPath,IProgress<long>? p,CancellationToken t)` (returns host's saved name). A `denied` reply throws `InvalidOperationException(reply.Text)`.

- [ ] **Step 1: Failing checks** (in-memory `RelayPair` from `Program.cs`, made `internal static` so `FileChecks` can use it; `FileHost` on one end with a temp receive folder, `FileClient` on the other): `put saves into the receive folder` (3 MiB random file, bytes equal, progress reached 3 MiB); `put never overwrites` (second put of same name → `name (2).ext`); `put rejects unsafe names` (`..\x.txt` → `InvalidOperationException`, receive folder empty); `get copies a host file` (2.5 MiB, bytes equal, saved in local folder); `get refuses an oversized or unreadable file` (a file locked with `FileShare.None` → denied; length check via `FilePolicy.MaxFileBytes` guard covered by refusing a path whose `Length` > limit, simulated with a sparse file set to 2 GiB + 1); `list of a missing folder is refused`; `list shows a folder` (temp folder with one file and one subfolder → both entries, folder flag right); `tampered digest leaves no file` (hand-driven host end sends correct chunks but a wrong `file-end` hash → `Get` throws `InvalidDataException`, no file in local folder); `cancel removes the partial file` (cancel token mid-`Get` of 8 MiB → `OperationCanceledException`, local folder has no file and no `.partial`).
- [ ] **Step 2:** `dotnet run --project tests/HearthLink.WindowsTests -c Release` → build FAIL.
- [ ] **Step 3:** Implement. Send: read 64 KiB, `Wire` chunk, after each 1 MiB wait for `file-ack` ≥ sent. Receive: `FileReceiver.AppendAsync`, ack each 1 MiB and at end, then `file-end` → `CommitAsync`.
- [ ] **Step 4:** Same command → all PASS.
- [ ] **Step 5:** Commit `Add file link protocol with windowed chunks`.

### Task 3: Host grants files to control sessions

**Files:** Modify `src/HearthLink/HostServer.cs`; Test `tests/HearthLink.WindowsTests/FileChecks.cs`.

**Interfaces — Produces:** `HostServer.ReceiveFolder { get; init; }` (default `%USERPROFILE%\Downloads\HearthLink`). For a **control** session the `ready` reply carries `Code`=`SessionFileGrant.Token` (view-only: `Code` null). A request `files` (`Code`=token) from a trusted controller whose active grant `Allows(fingerprint, token)` is served by `FileHost(ReceiveFolder).Serve(connection, grant.Lifetime)`; anything else gets `denied` "File transfer needs an active control session.". The grant's lifetime is the session's `stop` token.

- [ ] **Step 1: Failing checks** with the real fixture (`HostServer` with `ReceiveFolder` = temp; approve the session prompt with `NativeDialogs.Answer(…Yes…)`): `control session offers a file grant` (`ready.Code` length 64); `view-only session offers none`; `files connection with the grant puts a file` (put 1 MiB → file in `ReceiveFolder`); `files connection closes when the session ends` (open files link, send `disconnect` on the session → next `List` throws `IOException` or `EndOfStreamException` within 10 s; a new `files` request is `denied`); `wrong token is denied`.
- [ ] **Step 2:** Run Windows checks → FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run both test projects → PASS.
- [ ] **Step 5:** Commit `Host grants file transfer to control sessions`.

### Task 4: Viewer file UI

**Files:** Create `src/HearthLink/RemoteFilesWindow.cs`; Modify `src/HearthLink/ViewerWindow.cs`, `src/HearthLink/MainWindow.cs`.

**Interfaces — Consumes:** Task 2 `FileClient`, Task 3 grant in `ready.Code`. MainWindow passes `Func<Task<Connection>> openFiles` (reuses the route that won `Reach`: direct if the session is direct, else relay; sends `files` with the token and returns the connection) to `Viewer` when `ready.Code` is set.

- Toolbar (control sessions only): **Send files** (multi-select `OpenFileDialog`) and **Get files** (opens `RemoteFilesWindow`: path box, Up, list with name/size/modified, double-click opens a folder, **Download** for selected files). Transfers run one at a time in a queue on one files connection.
- A transfer strip at the bottom of the viewer while transfers run: "Sending report.pdf • 45% • 3 of 5" with a **Cancel** button (cancels the current file and the queue); after finishing, "Saved 5 files to Downloads\HearthLink" with an **Open folder** link for 10 s. Errors show the `denied` text.
- The auto-hiding toolbar stays visible while a dialog or the transfer strip is open.

- [ ] **Step 1:** Add the manual checks to `docs/ACCEPTANCE.md` (new "File transfer" section): send 3 files; get a file from the host profile; cancel a 1 GB transfer; same-name rename; view-only has no file buttons; transfer through the relay and direct.
- [ ] **Step 2:** Implement.
- [ ] **Step 3:** Run both test projects → PASS. Run the throwaway speed harness pattern with a control session through the relay: put and get a 50 MiB file, record MB/s in the acceptance notes.
- [ ] **Step 4:** Commit `Viewer sends and gets files`.

### Task 5: Docs and installer

- [ ] README "Normal use": Send files / Get files; ARCHITECTURE trust boundary row "Files: only during an approved control session, written only to Downloads\HearthLink"; website download page "What to expect" (file transfer available).
- [ ] `build.ps1 -SelfContained` → all checks pass, installer built; update `SHA256SUMS` in the PR description.
- [ ] Commit `Document file transfer` and push.
