# HearthLink website

Buildless, responsive Home / Download / Contact pages. Serve this directory as the web root. The desktop app source is one directory above; do not deploy that whole workspace.

Preview: `python -m http.server 5181 --bind 127.0.0.1 --directory website` from the app workspace.

Vercel: create/select project **hearthlink**, use **website** as the root directory for a Git-connected deployment, choose **Other** for the framework, and leave the build command empty. The desired domain is **hearthlink.vercel.app** and must be assigned/verified by Vercel. Nothing in this source file proves that domain has been deployed or is available.

The Download page discovers the latest stable installer from **pitchiluxe/hearthlink** GitHub Releases. Publish the installer before announcing the site; without a public release, the page shows a release-index fallback. See `../docs/UPDATES.md` for exact asset filenames and checksum requirements. Contact opens an email draft and sends nothing itself.

No server dependencies, analytics, customer accounts, or contact-message storage are included. Fonts load from Google Fonts with system fallbacks. Browser security headers are in vercel.json.
