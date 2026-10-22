# HearthLink updates and website

## Using app updates

1. Install HearthLink 0.2.1 or later on each PC. Version 0.2.0 cannot discover this first update by itself.
2. Open **App updates** in the dashboard's This computer panel.
3. Select **Check now**. Automatic checks also run shortly after launch and every 24 hours while the app is open. Turn them off with the checkbox if preferred.
4. A newer published version opens the Updates window and requests a Windows notification. Windows notification settings may suppress the balloon; the Updates window remains available.
5. Select **Download update**. The app downloads over HTTPS from GitHub and checks the exact length and SHA-256 against the release's checksum file. Cancel stops the download and removes its partial installer.
6. When verification finishes, end remote sessions and select **Install update**. Confirm that HearthLink may close and Windows may start the installer with administrator permission. Paired computer settings stay in the existing user profile.

The app does not silently install or restart. An offline check does not stop remote desktop use. Downloads are saved in the current user's LocalAppData/HearthLink/Updates directory, in a new uniquely named folder for each attempt. Successful installers remain there for retrying installation. The installer is currently unsigned; the checksum protects download integrity, not against compromise of the GitHub publisher account. Release signing is future work.

## Publishing a release

The updater and website use the public repository **pitchiluxe/hearthlink**. Publish only tested artifacts; a Git tag or a website deployment alone is insufficient.

1. Set the same numeric three-part version in the app project, installer version/output filename, and dashboard label.
2. Run `./build.ps1 -SelfContained`. It runs the core and Windows checks, creates the installer, calculates SHA-256 checksums, and packages the sources.
3. Create a GitHub Release tagged `vX.Y.Z`. Do not mark it draft or prerelease when intended for automatic checks.
4. Attach `HearthLink-X.Y.Z-win-x64-setup.exe` and **SHA256SUMS.txt**. The checksum file must contain exactly one matching installer entry. Source ZIP and illustrated manual may also be attached.
5. Verify the release asset links publicly and perform an update check from an older installed version. Test cancellation and successful installation on a spare PC before announcing a release.

The updater rejects missing checksums, mismatched sizes/hashes, external download publishers, HTTPS downgrades, prereleases, and version downgrades. GitHub API rate limits, DNS failures, or unpublished releases show a retryable status.

## Website deployment

The site is buildless HTML/CSS/JavaScript in **website/**. Deploy that directory to a Vercel project named **hearthlink**, with no framework and no build command. The intended production domain is **hearthlink.vercel.app**; its availability and assignment must be confirmed by Vercel. A local preview is not a production deployment.

Home uses a custom two-computer illustration and an actual application render. Download reads GitHub's latest public stable release and links the exact Windows asset. If no release is available, it offers the GitHub release index rather than a fictitious download. Contact opens a draft in the visitor's email application; the site does not send or store messages.

Use the repository's **website** root directory if connecting Vercel's Git integration to the full app repository. Do not deploy the workspace root, build home, desktop configuration, or local diagnostic reports. `website/vercel.json` defines browser security headers.

## Verification boundaries

Core update tests use HTTP fixtures with non-executable bytes and check metadata, versions, digest validation, partial-file cleanup, overwrite refusal, redirects, and cancellation. Windows checks exercise real certificate identities, storage, and loopback TLS. Neither suite installs an update with UAC, publishes a release, nor validates a two-physical-PC remote session. Those require separate acceptance checks.
