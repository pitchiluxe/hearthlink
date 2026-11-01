# HearthLink 0.6.5

The viewer previously disabled its Commands (update host) button when the connected PC did not advertise command support. Clicking it gave no explanation.

Commands now remains clickable in a live session. If the remote host lacks command support, it shows instructions to update HearthLink on that remote PC through Help & updates → Check now, reopen it, and disconnect/reconnect the session. Updating just the controlling PC does not enable commands on an older host.

View-only sessions show instructions to reconnect with Keyboard & mouse. Compatible control sessions keep the existing confirmed Restart / Reboot, Sign out and Shut down menu. Host permission checks and capability guards are preserved; unsupported and view-only requests are never sent. The button is disabled after the session ends.

Regression checks click Commands in view-only and old-host fixtures, verify actionable guidance and verify no command packets are sent to older hosts. Existing compatible-host confirmation, cancellation and frame responsiveness checks remain.
