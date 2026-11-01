# HearthLink 0.6.3

HearthLink starts minimized after Windows sign-in. Opening 0.6.3 registers the current user's quoted executable path in Windows Run; existing profiles default to enabled. Settings → General has a saved startup toggle. A per-user single-instance guard avoids duplicate hosts. Startup preserves all existing host, pairing, approval and Windows Hello rules; it cannot provide access before Windows sign-in. Closing the app still exits it.

The remote viewer's Commands menu offers Restart / Reboot, Sign out and Shut down. Each requires confirmation, defaults to No, and runs only in an authorized control session. The host accepts a fixed allowlist of shutdown.exe arguments without /f; arbitrary command text and view-only requests are rejected. New hosts advertise support; older hosts keep the menu disabled until updated. Windows rejection is reported to the viewer. The existing wire protocol remains compatible.

Ctrl+Alt+Delete is disabled with an explanation: this build has neither a privileged service nor a signed UIAccess application. No Windows security policies are changed.

Validation uses temporary registry keys, protected fixture profiles, local TLS connections and a test peer that receives commands without executing them. The command policy checks view-only rejection, unknown commands and fixed arguments; viewer tests cover confirmation, cancellation and emitted command packets. Actual reboot, sign-out, shutdown, sign-in startup and two-physical-PC acceptance require a spare PC and are not claimed as tested here.
