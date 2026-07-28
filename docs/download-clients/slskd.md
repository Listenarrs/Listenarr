# Native Slskd download client

Listenarr can submit audiobook searches directly to [slskd](https://github.com/slskd/slskd), monitor the returned batch, import every successful audio transfer into the canonical library directory, and remove only the verified isolated staging batch after import.

## Path contract

Use an isolated completion destination outside every Listenarr library root:

| Namespace | Example |
| --- | --- |
| slskd batch destination | `listenarr/<reservation-id>` |
| slskd completion root | `/downloads/complete` |
| Listenarr-visible completion root | `/slskd-downloads` |

The value entered as **Listenarr source root** must be an absolute path in the Listenarr runtime namespace. `/slskd-downloads` is the portable default, not a required host path. Listenarr rejects relative roots and validates reconstructed files beneath the configured root and isolated batch destination.

## Docker Compose

See [`docker-compose.slskd.example.yml`](../../docker-compose.slskd.example.yml). Create `./data/slskd/complete` on the host and mount it read/write into both containers under their respective namespaces. Do not mount this directory inside a library directory.

On Linux, the example relative bind paths work directly. On Windows Docker Desktop, replace `./data/slskd/complete` with a generic Docker Desktop shared path such as `D:/containers/listenarr/slskd-complete`; keep the container destinations unchanged.

For native Listenarr, set **Listenarr source root** to the absolute local directory where the same slskd completion files are visible. If slskd and Listenarr run on separate hosts, use a shared filesystem and point each application at its own absolute mount path.

## Configure

1. Create an API key in slskd and keep it in an environment secret or secret manager.
2. In Listenarr, open **Settings → Download Clients → Add → Slskd**.
3. Set the slskd host, port, TLS option, and API key.
4. Set **Listenarr source root** (default `/slskd-downloads`).
5. Choose priority and optionally mark the client as default.
6. Leave protocol fallback disabled unless torrent/Usenet fallback is explicitly desired.
7. Test the connection, then save.

API keys are carried in the standard secret settings dictionary and are redacted by Listenarr's existing API response redactor. Avoid putting keys in Compose files or logs. Non-loopback deployments should expose slskd through HTTPS.

## Semantics

- The configured default enabled client wins; otherwise lower priority wins, with creation time as a stable tie-break.
- Native Slskd routing runs before torrent/NZB indexer search.
- One durable Listenarr download is reserved before external HTTP effects; active or imported duplicates are rejected and never fall back.
- A batch completes only when every non-removed transfer reports both `Completed` and `Succeeded`.
- Multi-chapter transfers remain one batch and import into one canonical author/title directory.
- Missing author metadata imports beneath `Unknown Author/<Title>`.
- Cleanup runs only after successful import. Partial, failed, unsafe, or unverifiable staging is preserved.

## Troubleshooting

- **401/403:** recreate or re-enter the slskd API key and test again.
- **429:** Slskd is rate limiting. Listenarr honors retry timing within its bounded search deadline; retry later if it expires.
- **Search timeout/no safe result:** verify Soulseek connectivity and query metadata. Only safe audio filenames from one coherent response are selected.
- **Queued remotely:** confirm the batch UUID is visible in both slskd and Listenarr activity.
- **Unsafe path:** ensure the source root is absolute and the shared completion mount matches it. Do not use `..`, drive-qualified remote filenames, or a library root as staging.
- **Import/finalization preserved staging:** inspect failed transfers and unexpected files. Listenarr deliberately refuses broad deletion.

Before upgrading, back up Listenarr's database/config and slskd configuration. To roll back, stop both services, restore those backups, and use the previous image digest; preserve staging until imports are reconciled.

Automated tests use deterministic HTTP fixtures. CI and normal tests do not contact public Soulseek peers.
