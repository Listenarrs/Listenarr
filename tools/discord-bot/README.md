# Discord Bot (dev/test stub)

This folder contains a minimal, local stub for a Discord bot used by tests and local development.

Purpose
- Provide a tiny long-running process that prints startup and periodic heartbeat messages.
- Used by tests and developer workflows to emulate a running bot process (not a real Discord bot).

Current status in repo
- `index.js` is present and acts as a harmless placeholder that keeps a child process alive and emits periodic log lines.

Requirements
- Node.js installed to run `index.js`.

How to run
```powershell
# From repo root (PowerShell)
node .\listenarr.api\tools\discord-bot\index.js

# Or from the folder
cd listenarr.api\tools\discord-bot
node index.js
```

How tests / dev use it
- Tests and some local diagnostic flows may spawn this process (or a similar executable) to emulate a running Discord bot. Removing the file can break those tests or local scripts that expect a long-running process in `listenarr.api/tools/discord-bot`.

Notes
- This stub is not intended for production use. Keep the file present to avoid breaking tests or local diagnostic flows that spawn the bot process.
- If you prefer not to run Node during tests, mock `IDiscordBotService` in tests instead of spawning the process.
- API note: primary Discord bot metadata calls now use `/api/metadata/{asin}` (and `/api/metadata/audible/{asin}`); legacy `/api/search/metadata` routes are deprecated and may be removed.

Optional enhancements
- Add a small `package.json` with a `start` script so `npm start` runs the stub. A `package.json` in this folder is lightweight and optional.

If you want, the repository maintainers can add the `package.json` and a short note here. (I'll add one if you confirm.)
