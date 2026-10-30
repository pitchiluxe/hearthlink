# HearthLink 0.6.1 — installed Ollama scan reliability

The reported 0.6.0 scan error was reproduced against the local Ollama endpoint. `/api/tags` responded with installed models, but a subsequent `/api/show` failed because a referenced GGUF blob file did not exist. The old sequential scanner abandoned the entire result and its UI kept the pending verification label while displaying a generic cloud/provider error. Later diagnostic calls also found Ollama stopped/unreachable; that is reported separately now.

## Result

- Each model is verified independently. Missing, damaged, unsupported, oversized or timed-out metadata cannot discard healthy verified models.
- The installed-model dropdown receives only verified local chat models. Cloud-backed and non-chat models remain excluded; configured inference still re-verifies its selected model.
- Model-specific warnings name the affected entry and suggest repairing/re-downloading it in Ollama. No automatic repair, download or deletion occurs.
- Metadata checks use concurrency three, a three-second timeout per model, a five-second model-list timeout and a fifteen-second overall scan budget. Up to the first 128 listed entries are considered; partial scans are labeled. Caller cancellation remains distinct and preserves the previous dropdown.
- Settings shows checked/total progress, a Cancel scan action, a readable warning-details box and a terminal status after success, cancellation or failure. Late progress cannot replace the completed/error message.
- If Ollama is not responding locally, Settings says to start Ollama and retry. This scan does not require cloud credentials or internet access.
- Automatic foreground model discovery also retains healthy models when another entry fails. Background and explicit inference still respect their existing caller time budgets and local-verification boundaries.

## Validation

The missing-blob mocked regression failed on the original scanner and passed after the fix. Additional checks cover the named repair warning, independent timeout with healthy results preserved, explicit cancellation, the actual Settings result/error labels, and an unreachable local service. No installed model data was modified or downloaded. After Ollama resumed responding, a live post-fix scan checked all 30 eligible entries in 3.8 seconds, verified 25 local chat models and flagged five unreadable entries; it was not partial. The user-specific inventory stays out of release notes/source.

Final results: **129 core checks passed; 119 Windows checks passed**. The Windows audit assertion was also switched to the app's shared-read snapshot reader after a pre-existing race with concurrent audit appends surfaced during verification.

Core and full Windows results are recorded in `artifacts/model-scan-final-core.log` and `artifacts/model-scan-final-windows.log`. The existing fixed 1096×783 main window and all opt-in cloud/agent safeguards are preserved. Protocol unchanged; compatible with 0.4.1–0.6.0. Physical two-PC and installation acceptance remain separate checks.
