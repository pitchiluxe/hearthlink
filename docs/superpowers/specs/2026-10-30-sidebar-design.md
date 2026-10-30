# HearthLink navigation

The user approved adding the eight sidebar options described in chat: Your computers, Recent sessions, File transfers, Access & security, Connection health, Session history, Settings, Help & updates. This implements them in the existing Windows Forms application with its navy/blue identity and no new packages.

Implementation choices: use one shell and persistent page controls so navigation never restarts hosts, loses selection, or disconnects viewers. Keep Help & updates at the bottom of a scrollable sidebar. Add an active-session shortcut when viewers or incoming sessions exist. Status and transfer badges come from real events only.

- Your computers retains pairing/connect and the status-striped table, with search and favorites. All connect/remove actions identify peers by pinned fingerprint rather than filtered row index.
- Recent sessions reads outbound-connected history and resolves each fingerprint against current trusted hosts. Reconnect uses the existing approval/pinned-connect flow. Removed computers cannot reconnect from history.
- File transfers displays real in-process batches, progress and outcomes from viewer and file-manager strips, with Cancel for active batches and Open session/file manager for live control sessions. No detached/background transfers or pretend progress. Completed rows are retained for the current app run, bounded to 200 entries.
- Access & security contains existing trust revocation and incoming-session/Windows Hello controls. No changes to consent or unattended policy.
- Connection health shows host relay state, last device check, per-peer state, and existing session route. Refresh uses existing probes. Failed relay checks must appear as unavailable rather than proven offline.
- Session history provides a searchable in-app metadata table and export. History reads tolerate malformed/truncated lines, read a bounded tail, and use the selected IdentityStore folder, including isolated tests. No raw private keys, pairing secrets, file contents or names in exported audit events.
- Settings exposes computer name, controller role, default keyboard/mouse mode, default quality and automatic update checks. Values persist using existing protected settings; old settings load with compatible defaults. Startup/tray and theme options are not added in this iteration.
- Help & updates provides the existing updates window, built-in getting-started instructions and links to the existing user manual and releases. Available update badge reflects actual update checks.

No automatic release publication is inferred from this feature request. Build a reviewable local installer with a new version. Validate page navigation, filtering action identity, safe history parsing, transfer lifecycle, preferences and existing drawing/security checks. Render the real UI using isolated preview identities; do not use customer data or create live sessions for screenshots. Physical two-PC acceptance remains separate.
