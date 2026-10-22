# HearthLink 0.2.0 test update

Install this update on both PCs. Existing saved identities and pairing are preserved. This is still an attended LAN/VPN test build.

## Pair and connect

1. On the remote PC, keep HearthLink open and enable Allow incoming sessions.
2. For a new pairing, select Open pairing for 2 minutes on that PC.
3. On your controlling PC, enter its hostname (for example TECHNODESK) or actual LAN address (10.0.0.231) in the box.
4. Click Pair / connect or press Enter. The existing text is used and remembered between launches.
5. For a new computer, compare fingerprints, enter its pairing code, and approve pairing on the remote PC. HearthLink then requests a session automatically.
6. For a previously paired computer, the button uses its saved identity and requests a session directly.
7. Approve the session on the remote PC. It displays a persistent connection notice with an End session button and requests a Windows notification.

Windows may suppress notification balloons; the persistent connection notice remains available.

## Streaming changes

Binary JPEG frames replace Base64 JSON video. Capture buffers are reused, decoding runs off the interface thread, mouse motion is coalesced, and at most one video frame waits for presentation. Responsive, Balanced, and Full detail profiles are available. The viewer reports measured frame rate and processing timings.

Local pipeline benchmarks improved from about 3.6 frames/sec to about 7.2 frames/sec in Responsive mode on this machine. This is not a network or input-to-screen measurement. Native-computer responsiveness and two-PC operation have not been verified; current capture remains CPU-based.

## Connection troubleshooting

The remote host must accept TCP 45831. Name resolution alone is insufficient. Use the connected Ethernet/Wi-Fi address with a gateway, rather than a virtual adapter address such as 192.168.56.1. The installer firewall option permits Private networks on the local subnet. Do not disable the firewall or open Public networks broadly. TECHNODESK's listener/profile report remains needed to diagnose its observed timeout.

## Remaining work

File transfer protocol and receiver components are under development and are not exposed as a working transfer feature in this build. Unattended access, clipboard sync, internet relay, and remote restart are not implemented. Use the versioned 0.1.1 illustrated manual for the common dashboard and pairing steps; the changed button behavior is documented above.
