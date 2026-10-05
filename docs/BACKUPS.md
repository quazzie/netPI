# Backups and recovery

The `netpi.backup` plugin saves the files the storage provider writes, plus the current settings and the idea images, in
`<home>/backups/<timestamp>-<id>/`. A snapshot is those files (`netpi.db` for the `sqlite` provider — a consistent copy
that includes the WAL), `settings.json`, a copy of every file in `<home>/idea-images/` (the pictures attached to ideas,
which are files under the home rather than rows in the database), and a versioned `manifest.json` that names the
provider, carries a SHA-256 checksum per file and says which file of `idea-images/` each copied image is — and nothing
else, because the ideas backlog (the ideas themselves, the cards waiting for an answer, the answers already given, the
per-conversation check marks and the per-repository commit cursors) is plugin-owned tables in that same `netpi.db`. It
therefore travels inside the consistent database copy: a backup needs no Ideas plugin installed or running, and it
cannot be a snapshot that quietly left the backlog out. The images travel as copies of their own, with the manifest
saying where each one belongs, so a restored backlog keeps the screenshots its cards show instead of answering that
they are gone from disk. The copy runs in batches over the online backup API (the store's lock is free between
them, so a long copy does not stall the running server), and it includes committed WAL changes while NetPI runs, so a
snapshot never catches a card answer half applied. The provider's files and the settings are captured sequentially; they are not one cross-file
transaction. Restore copies exactly what the manifest lists, so a snapshot from an older build — one that still
carries the ideas JSON files — restores as it was, and the plugin imports those files at its first start.

## Create and verify

Open Settings → **Data & backups** → **Back up now**. Automatic backups run daily by default; seven automatic snapshots
are retained. Manual snapshots remain until removed. Retention runs only after a successful new snapshot, and skips a
snapshot that carries files a new one would not have (an older snapshot's ideas files are the only copy of anything on a
home that has not been migrated); the idea images a snapshot carries do not count, so a picture deleted since does not
keep every snapshot taken while it existed. An entry that is not a regular file at all (a directory, a broken link) counts as a file a new snapshot would not have, so the snapshot is kept whole — retention never half-deletes one and fails the backup that met it. A failed/incomplete `.pending-*` directory is not listed as a usable backup.

**Verify** is deliberately strict, because a snapshot that *looks* complete is the dangerous kind. A manifest is refused
when it names no `provider`, when it lists no files, when it does not list `settings.json` or the files its provider
writes (`netpi.db` for `sqlite`), when an entry is anything but a 64-character checksum, or when a name is not a plain
file **inside** the snapshot (no directory, no `..`, nothing that resolves outside — checked before the file is opened,
because a manifest is a file anything can write), or when an idea image is not a file of the snapshot or is not named
as a plain file inside the images directory. Every listed file must be there and hash to what the manifest says,
and each of those failures is reported with its reason rather than a generic "corrupt".

From the repository, against a running app:

```powershell
node scripts/netpi.mjs backup.create --write
node scripts/netpi.mjs backup.list
node scripts/netpi.mjs backup.verify id=<snapshot-id>
```

The snapshot contains what the storage provider writes — conversations, projects, plugin database tables (including
usage, and the ideas backlog) — the idea images, and settings. It does **not** include project working files,
instruction files, skills, plugin binaries, SSH keys, or the browser profile. Back those up separately. Settings may
contain API keys; treat snapshots as private.
Checksums detect damage, not malicious replacement of both files and the manifest. Keep a copy on separate storage.

## Restore without overwriting the current home

Requires Node.js 22 or newer, and the same or a newer compatible NetPI build. Choose a destination that does not
exist; its parent directory must exist. The command applies the same manifest rules as **Verify** (a named provider, a
non-empty file list, a checksum for every entry, a plain name inside the snapshot, an idea image named as a plain file
inside `idea-images/`), then the provider's own check and the settings JSON, before creating the destination: a
`sqlite` snapshot must start with `SQLite format 3`, and any other provider restores the files it listed. It refuses
an existing directory or symlink. On a write failure it leaves any partial destination for inspection; retry into a
different directory. A `memory` snapshot (`memory.json`) is restored like any other file, but it is input for a one-off
migration tool, not for a start: the memory provider loads nothing from its home, so a home restored from one starts empty.

A snapshot whose manifest names no provider — one made before the provider was recorded — can only be restored by the
build that made it. A newer build refuses it rather than guess which storage layout it holds.

```powershell
node scripts/restore-backup.mjs "D:\Backups\20260928-120000-id" "C:\AI\netpi-recovered"
artifacts\app\netpi-server.exe --home "C:\AI\netpi-recovered" --open
```

Inspect the recovered chats and settings before choosing this home for everyday use. To use the desktop app,
set `NETPI_HOME` to the restored directory before launching it. Project paths still point to their original
locations; restore project files separately or update their paths. A snapshot made during model calls may retain
reservations for calls whose final bills were unavailable. Those estimates continue to count toward budgets.

The original home and snapshot are never overwritten by the restore command.
