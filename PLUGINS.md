# Plugins

Listenarr supports **runtime-installable plugins** — self-contained add-ons you install from the
**Settings → Plugins** page without rebuilding or restarting the container yourself. A plugin can
add backend API endpoints, frontend views and navigation, and its own data storage, all without
any changes to Listenarr core.

Plugins are distributed through **plugin repositories**: a repository is just a URL to a JSON index
that lists the plugins it offers. Adding a repository does **not** install anything — it populates a
menu of available plugins, and you install only the ones you want.

## Get started

A ready-made repository of plugins is maintained at
**[shuff57/listenarr-plugins](https://github.com/shuff57/listenarr-plugins)**.

1. In Listenarr, go to **Settings → Plugins**.
2. Under **Repositories**, paste this URL and add it:

   ```
   https://raw.githubusercontent.com/shuff57/listenarr-plugins/main/listenarr-plugins.json
   ```

3. The available plugins appear in the list. Click **Install** on the ones you want. The app
   restarts briefly to load each plugin. **Uninstall** and **Update** work the same way.

That's it — install what you need, ignore the rest, and remove anything later without a trace.

## Available plugins

From the repository above:

| Plugin | What it does |
| --- | --- |
| **Profiles** | Multi-user accounts: per-user "My Library" shelves on the shared pool, admin user management, self-service password change. |
| **Inline Player** | In-app audiobook player — chapters, bookmarks, resume, and a Listen library page. |
| **Discover** | Suggests audiobooks you might like, seeded from the authors and genres you already have, with one-click hand-off into Add New. |
| **Library Stats** | A dashboard: totals, top authors/narrators, and (with the player) in-progress/finished counts. |
| **Theme** | Inject custom CSS at runtime — accent presets, a full CSS editor, and Apply/Save/Reset. |
| **Library Export** | Export your library to JSON or CSV in one click. |
| **Backup** | Download your config directory as a zip. Admin-gated. |
| **Health** | File-integrity scan: flags missing, zero-byte, or unreadable audiobook files on disk. Admin-gated. |

## How it works

- **A repository is a catalog, not a bundle.** The index (`listenarr-plugins.json`) lists each
  plugin's `id`, `name`, `version`, `description`, and a `package` URL. You install per-plugin.
- **Each plugin is a small zip** containing a `plugin.json` manifest and any of: a backend `.dll`,
  a frontend bundle (`ui/<id>.js` + `ui/<id>.css`).
- **Backend** assemblies are loaded at startup and their controllers run in Listenarr's own API
  pipeline (so they share auth, CSRF, and routing). A plugin owns its **own** data — its own SQLite
  file or config — and never modifies the core schema.
- **Frontend** bundles are injected at runtime and register their own routes, sidebar items, and an
  optional always-mounted root component, against a small stable host SDK (`window.LISTENARR`).
- **Install / uninstall / update** write to the plugins directory and restart the app so the change
  loads on the next boot.

> **Trust:** installing a plugin from a repository downloads and runs its code — the same trust
> model as *arr custom repositories. Only add repositories you trust, and keep the Plugins page
> behind authentication / your local network.

## Run your own repository

A repository is just a static JSON file you host anywhere (a GitHub repo, a release asset, any web
server). Point Listenarr at its URL and it appears in the list.

```json
{
  "name": "My Listenarr plugins",
  "plugins": [
    {
      "id": "example",
      "name": "Example",
      "version": "1.0.0",
      "description": "What it does.",
      "author": "you",
      "package": "https://example.com/example.zip"
    }
  ]
}
```

Each `package` is a zip laid out as:

```
plugin.json                 # { "id", "name", "version", "frontend"?, "styles"? }
Listenarr.Plugins.Example.dll   # optional backend (implements IListenarrPlugin)
ui/example.js               # optional frontend bundle
ui/example.css              # optional styles
```

The [shuff57/listenarr-plugins](https://github.com/shuff57/listenarr-plugins) repository is a
working example of the catalog + packaged releases.
