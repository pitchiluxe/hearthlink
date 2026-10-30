# HearthLink 0.6.2 — restore the controller option

**Use as a controller** is now visible directly under **Access & security**, below Allow incoming sessions. It remains available under **Settings → General**, so switching to the AI settings tab does not make this option hard to find.

Both controls update the same saved controller-role setting through the existing handler. Changes synchronize in both directions. Opening the app does not alter the saved role; disabling it continues to block home, recent and pending connection paths. Incoming permissions, unattended access and paired-device trust are unchanged.

**Full detail** is now the default image quality for new sessions. Existing profiles receive this default once on upgrade; subsequent choices saved under Settings → General remain respected. The viewer sends the Full detail profile to the host when opening a default session, rather than only changing the displayed menu label. Manual quality changes and the bounded congestion agent continue to operate normally.

Focused Windows checks verify visibility, bidirectional synchronization, persistence through the existing role handler, all sidebar destinations, filtered peer actions and AI scan/settings behavior. The fixed-size main window is retained. No protocol changes; compatible with 0.4.1–0.6.1. Installer execution and physical two-PC checks remain separate acceptance work.
