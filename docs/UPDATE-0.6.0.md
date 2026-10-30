# HearthLink 0.6.0 — AI providers and fixed main window

## Using the new settings

Open **Settings → AI providers & performance**. Save stays at the top, with operation status at the bottom while the content scrolls.

1. Start Ollama and select **Scan installed models**. The app verifies local chat capability and remote metadata before offering a model; it verifies again before inference. Choose a model or Automatic and save.
2. To discover models, scroll to **Discover recent local models** and **Scan Ollama Library**. This reads the official public newest listing, filters out embedding and hosted-only entries, shows size variants/update metadata, and links to each model's details/license. **Copy install command** copies an `ollama pull` command; HearthLink does not download or install models. A catalog scan does not prove a model is suitable for your hardware or license requirements.
3. Cloud fallback is off by default, even if keys are entered. If wanted, enable it, enter API keys/model IDs, and **Save AI settings**. Model-list buttons then query that provider using the saved key. Routing is **Ollama → OpenAI → Claude**, with one attempt per provider. Cancellation and invalid/blocked replies stop the request rather than triggering further providers.
4. Enabling fallback permits file-helper prompts, conversation and folder/path metadata to leave the PC. File contents and screenshots are not sent. Keys are masked and included only in the existing DPAPI-protected settings, never history or request bodies. Requests use fixed HTTPS provider endpoints and do not follow redirects. Provider API access, model permissions and charges require your own account; the app does not supply credits or credentials.
5. The file helper identifies the provider/model that answered. It still validates plans against the displayed folder listings and requires **Run** before a copy. Same-named destination files can be replaced; the prompt and documentation now state that accurately.

## Local performance agent

The user's approved default is enabled, with allowed profiles **Responsive (0)** through **Full detail (2)**. It is a narrow image-quality adviser, not a general-purpose computer-control agent.

- Only a live, visible viewer with fresh frames is eligible. Idle/static low FPS is not treated as congestion.
- Three fresh congested samples are required (round trip at least 180 ms or capture/encode at least 100 ms). Healthy samples reset the count.
- The agent may request a suggestion at most once per two minutes per session. A process-wide gate prevents simultaneous AI inference and skips background work while another AI request owns it.
- Background requests use verified local Ollama models only, choosing the smallest installed chat model up to 3 GiB in model-file size. The request has an eight-second timeout, a 160-output-token/2,048-context budget, two CPU-thread option and immediate model unload preference. These request controls do not guarantee a particular GPU/memory footprint or that another Ollama client is idle.
- It sends only six numeric values: FPS, round trip, host timing, current profile, minimum and maximum. It has no screenshot, input, file, security, credentials or general tools. No background cloud fallback occurs.
- Recommendations must be a valid profile integer, stay within the configured range, and reduce by at most one step. They never raise quality. At minimum quality, or outside the allowed range, inference is skipped.
- After inference, the app rechecks session visibility/lifetime, fresh frames, current congestion, settings identity and current quality. Stale recommendations are discarded. A manual quality choice pauses automatic changes for the remainder of that viewer session.
- Status appears in the session toolbar and Connection health. Missing/unavailable small models leave quality unchanged. You can disable the agent in Settings.

Inference itself uses resources. Actual benefit depends on hardware, selected models and network conditions; no measured speedup is claimed by this build.

## Fixed window

At the user's request the main app returns to its previous **1096 × 783 outer size**, with a fixed border, equal minimum/maximum bounds, and maximize disabled. Navigation and Settings remain scrollable. Viewer/file-manager behavior is unchanged.

## Verification

Core checks cover provider ordering, opt-in and changes during a request, cancellation, bounded output and request shape, credential/header isolation, remotely backed Ollama metadata, catalog parsing, sustained congestion/reset/cooldown, numeric-only context, shared inference exclusion, stale/manual decisions and minimum-profile resource avoidance.

Final results: **125 core checks passed; 115 Windows checks passed**. Live relay integration remains skipped unless `HEARTHLINK_RELAY_URL` is set.

Windows checks cover masked controls, DPAPI roundtrip, denied-save rollback, fixed window bounds and all existing navigation/security/transfer checks. A denied-save regression failed before the UI error boundary was corrected. The notice-collapse fixture now injects its pointer position, so the test neither depends on nor moves the user's mouse. Normal and scaled previews use isolated unenrolled profiles and empty credentials.

A live public-library check on October 30, 2026 returned **235 entries with local variants** from the current listing; this is a dated scan result, not a promise of exhaustive availability or universally free licensing. No actual model download, paid cloud inference or real-key request was made. Live AI inference, measured performance gains, physical two-PC sessions and native mixed-DPI transitions remain acceptance work. The relay protocol did not change; 0.6.0 connects to 0.4.1–0.5.0. Installer execution and production publishing are separate actions.

Local validation output: `artifacts/ai-final-core.log`, `artifacts/ai-fixed-final-windows.log`, `artifacts/ai-catalog-live.log`; previews: `artifacts/design/0.6.0-fixed/`.

Self-contained win-x64 publish and Inno Setup compilation succeeded. Local installer: `artifacts/HearthLink-0.6.0-win-x64-setup.exe`; build logs: `artifacts/publish-0.6.0.log` and `artifacts/installer-0.6.0.log`. It has not been installed or published to App updates.

## Official integration references

- [Ollama model listing](https://docs.ollama.com/api/tags), [chat API](https://docs.ollama.com/api/chat), [current library](https://ollama.com/library?sort=newest).
- [OpenAI Responses text generation and output parsing](https://developers.openai.com/api/docs/guides/text).
- [Claude API authentication](https://platform.claude.com/docs/en/api/overview), [Messages API](https://platform.claude.com/docs/en/api/messages/create).
