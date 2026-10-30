# AI Settings Implementation Plan

**Goal:** Ollama-first helper routing, explicit cloud fallback, current model discovery, bounded local performance assistance.
**Spec:** docs/superpowers/specs/2026-10-30-ai-settings-design.md
**Architecture:** Core provider/router and numeric performance policy; protected options in IdentityStore; WinForms AI settings panel; shared router passed into file helper; local performance coordinator per Viewer.

- [x] Add failing mocked-provider and agent policy tests.
- [x] Implement bounded provider HTTP clients, runtime local-model verification and catalog discovery.
- [x] Add DPAPI-backed AI options and settings UI with masked keys, model scans and explicit cloud opt-in.
- [x] Route the existing file helper through Ollama/OpenAI/Claude; preserve validated Run-only copy plans.
- [x] Add rate-limited numeric-only local agent with quality bounds, cooldown, manual override and visible health.
- [x] Run tests, inspect UI renders, conduct fresh review, fix material findings and package a local installer.

Rulings: background inference never uses cloud; API/model IDs are user-configured or discovered rather than hardcoded latest aliases; no actual model download or paid inference occurs as part of development.

## Execution ledger

- User approved cloud fallback only after enabling it in Settings, and bounded automatic quality adjustment with no file/security actions.
- Core: 125 checks passed. Windows: 115 checks passed. Official public catalog smoke check returned 235 local-variant candidates; no model downloads or live-key requests occurred.
- Fresh design/final review identified stale congestion accumulation and missing UI error handling for denied saves/browser/clipboard/regex timeouts. Fixed healthy-sample reset and late congestion revalidation; added recoverable error boundaries. The denied-save regression failed before the fix, then passed, preserving prior credentials.
- User requested restoring the previous fixed main window. Fixed border, no maximize and equal 1096×783 min/max bounds were restored; regression failed before and passed afterward. Page content remains scrollable.
- One pre-fix validation run exposed an existing notice test depending on the user's pointer position. The fixture now injects its pointer source instead of moving the mouse; the full suite passed afterward.
- Ruling: catalog scans read metadata and offer official model/license links and copyable installation commands, with no automatic downloads. Catalog metadata alone is never sufficient to permit inference; local /api/show verification is required.
- Ruling: no inference at minimum quality; no adjustments above the agent range; no promise of measured performance gains. Live model inference and physical-PC acceptance remain explicit limitations.
- Self-contained 0.6.0 win-x64 publish and Inno Setup compilation succeeded. Installer is local only; no installation or release publication performed.
