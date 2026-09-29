#!/usr/bin/env node
// Restore into a NEW home. Does not modify the backup or an existing NetPI home.
import fs from 'node:fs/promises';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { pathToFileURL } from 'node:url';

export async function restoreBackup(backup, destination) {
  backup = path.resolve(backup);
  destination = path.resolve(destination);
  const manifest = JSON.parse(await fs.readFile(path.join(backup, 'manifest.json'), 'utf8'));
  if (manifest.version !== 1) throw new Error('Unsupported backup version');
  const contents = new Map();
  // Whatever the manifest lists (netpi.db, settings.json, and the ideas files a newer snapshot carries), each with a
  // checksum of its own. An older snapshot restores exactly what it has.
  for (const name of Object.keys(manifest.files ?? {})) {
    const data = await fs.readFile(path.join(backup, name));
    const hash = createHash('sha256').update(data).digest('hex');
    if (hash !== manifest.files?.[name]?.toLowerCase()) throw new Error(`Checksum mismatch: ${name}`);
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
  for (const [name, data] of contents) await fs.writeFile(path.join(destination, name), data, { flag: 'wx', mode: 0o600 });
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
