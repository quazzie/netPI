# Backups and recovery

The `netpi.backup` plugin saves a consistent SQLite snapshot, the current settings and the ideas backlog in
`<home>/backups/<timestamp>-<id>/`. Each snapshot has `netpi.db`, `settings.json`, the ideas files (the backlog under the
name `ideas.fileName` says, `ideas-pending.json` with the cards waiting for an answer, and `ideas-migration.json`), and
a versioned `manifest.json` with a SHA-256 checksum per file. `VACUUM INTO` includes committed WAL changes while NetPI
runs. The database and settings are captured sequentially; they are not one cross-file transaction. The ideas files are
copied while the ideas plugin's own lock is held, so a snapshot never catches a card answer half applied — if that
lock cannot be taken within five seconds, the files are left out of that snapshot and the log says so (an older snapshot
beats a torn one). Restore copies exactly what the manifest lists, so a snapshot from an older build restores as it was.

## Create and verify

Open Settings → **Data & backups** → **Back up now**. **Verify** checks the files against their manifest.
Automatic backups run daily by default; seven automatic snapshots are retained. Manual snapshots remain until
removed. Retention runs only after a successful new snapshot. A failed/incomplete `.pending-*` directory is not
listed as a usable backup.

From the repository, against a running app:

```powershell
node scripts/netpi.mjs backup.create --write
node scripts/netpi.mjs backup.list
node scripts/netpi.mjs backup.verify id=<snapshot-id>
```

The snapshot contains conversations, projects, plugin database tables (including usage) and settings.
It does **not** include project working files, instruction files, skills, plugin binaries, SSH keys, or the browser
profile. (It *does* include the global ideas backlog and its pending cards.) Back those up separately. Settings may contain API keys; treat snapshots as private.
Checksums detect damage, not malicious replacement of both files and the manifest. Keep a copy on separate storage.

## Restore without overwriting the current home

Requires Node.js 22 or newer, and the same or a newer compatible NetPI build. Choose a destination that does not
exist; its parent directory must exist. The command verifies both checksums, the SQLite header and settings JSON
before creating the destination. It refuses an existing directory or symlink. On a write failure it leaves any
partial destination for inspection; retry into a different directory.

```powershell
node scripts/restore-backup.mjs "D:\Backups\20260928-120000-id" "C:\AI\netpi-recovered"
artifacts\app\netpi-server.exe --home "C:\AI\netpi-recovered" --open
```

Inspect the recovered chats and settings before choosing this home for everyday use. To use the desktop app,
set `NETPI_HOME` to the restored directory before launching it. Project paths still point to their original
locations; restore project files separately or update their paths. A snapshot made during model calls may retain
reservations for calls whose final bills were unavailable. Those estimates continue to count toward budgets.

The original home and snapshot are never overwritten by the restore command.
