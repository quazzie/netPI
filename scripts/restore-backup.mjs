#!/usr/bin/env node
// Restore into a NEW home. Does not modify the backup or an existing NetPI home.
import fs from 'node:fs/promises';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { pathToFileURL } from 'node:url';

/** A snapshot's files are plain names inside it: no directory, no "..", nothing that resolves outside. */
function inside(dir, name) {
  if (typeof name !== 'string' || !name || name !== path.basename(name) || name === '.' || name === '..')
    throw new Error(`The manifest names ${JSON.stringify(name)}, which is not a file of the snapshot`);
  const full = path.resolve(dir, name);
  if (path.dirname(full) !== path.resolve(dir)) throw new Error(`The manifest names ${name}, which is outside the snapshot`);
  return full;
}

export async function restoreBackup(backup, destination) {
  backup = path.resolve(backup);
  destination = path.resolve(destination);
  const manifest = JSON.parse(await fs.readFile(path.join(backup, 'manifest.json'), 'utf8'));
  if (manifest.version !== 1) throw new Error('Unsupported backup version');
  const listed = manifest.files;
  if (!listed || typeof listed !== 'object' || Array.isArray(listed) || Object.keys(listed).length === 0)
    throw new Error('The manifest lists no files');
  const contents = new Map();
  // Whatever the manifest lists, each with a checksum of its own, read from inside the snapshot only. An older
  // snapshot restores exactly what it has (its ideas files, before they became tables in the database).
  for (const [name, expected] of Object.entries(listed)) {
    if (typeof expected !== 'string' || expected.length !== 64) throw new Error(`The manifest has no checksum for ${name}`);
    const data = await fs.readFile(inside(backup, name));
    const hash = createHash('sha256').update(data).digest('hex');
    if (hash !== expected.toLowerCase()) throw new Error(`Checksum mismatch: ${name}`);
    contents.set(name, data);
  }
  if (!contents.has('netpi.db')) throw new Error('The snapshot has no netpi.db');
  if (!contents.has('settings.json')) throw new Error('The snapshot has no settings.json');
  if (contents.get('netpi.db').subarray(0, 16).toString('ascii') !== 'SQLite format 3\0') throw new Error('Invalid SQLite database');
  const settings = JSON.parse(contents.get('settings.json').toString('utf8'));
  if (!settings || Array.isArray(settings) || typeof settings !== 'object') throw new Error('Invalid settings');
  // Exclusive creation rejects existing homes, including symlinks. On a write failure keep the partial directory
  // for inspection; another restore must choose a different destination.
  await fs.mkdir(destination);
  for (const [name, data] of contents) await fs.writeFile(inside(destination, name), data, { flag: 'wx', mode: 0o600 });
  return destination;
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  const [backup, destination] = process.argv.slice(2);
  if (!backup || !destination) {
    console.error('Usage: node scripts/restore-backup.mjs <snapshot-directory> <new-home-directory>');
    process.exitCode = 2;
  } else {
    try { console.log(`Restored to ${await restoreBackup(backup, destination)}. Start NetPI with --home pointing there.`); }
    catch (e) { console.error(`Restore failed: ${e.message}`); process.exitCode = 1; }
  }
}
