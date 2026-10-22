# HearthLink visual design

Version 0.1.1 applies a cohesive Windows desktop identity to the existing attended connection milestone.

* Navy `#142541` anchors a quiet left sidebar. Blue `#2563EB` identifies primary pairing/connect actions. White working surfaces sit on cool `#F2F6FB`. Text uses `#1F2F46`, with secondary text `#65748B` and structural borders `#E1E8F1`.
* Windows Segoe UI carries controls and body copy, with Segoe UI Variable Display for the page title. Consolas is restricted to certificate fingerprints and pairing-code entry.
* The central workspace holds enrolled computers and controller permissions. A separate right column holds this computer's role, host state, pairing code and identity. The actual empty state invites pairing without inventing online devices.
* The custom linked-screen mark uses white and cyan monitor outlines on a blue rounded tile. The ICO contains 16, 20, 24, 32, 40, 48, 64, 128 and 256 pixel images. It is embedded in the executable, window titles and installer.
* Keyboard focus remains visible through standard Windows focus cues. System message dialogs retain native Windows styling. The access column scrolls at shorter window sizes; the computer table resizes its columns.

Source: `Brand.cs` is the deterministic vector drawing and shared control palette. `tools/HearthLink.Assets` generates the ICO/PNG. `tools/HearthLink.Preview` renders the actual application controls at two sizes using an isolated, unenrolled identity. It does not capture the Windows screen, start hosting, approve a connection or add fake peers. Preview images are design evidence only, not evidence of a two-PC connection.

Regenerate assets: `dotnet run --project tools/HearthLink.Assets -c Release -- assets`.

Regenerate previews: `dotnet run --project tools/HearthLink.Preview -c Release -- artifacts/design`.
