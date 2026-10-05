#!/usr/bin/env node
// Restore into a NEW home. Does not modify the backup or an existing NetPI home.
import fs from 'node:fs/promises';
import { createReadStream, createWriteStream } from 'node:fs';
import { pipeline } from 'node:stream/promises';
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

/** An idea image belongs at one plain file name inside the images directory of the home, and nowhere else. */
function imageTarget(name) {
  const parts = typeof name === 'string' ? name.split('/') : [];
  if (parts.length !== 2 || parts[0] !== 'idea-images' || !parts[1] || parts[1] === '.' || parts[1] === '..' || parts[1] !== path.posix.basename(parts[1]))
    throw new Error(`The manifest names an idea image at ${JSON.stringify(name)}, which is not a file of the images directory`);
  return parts[1];
}

/** The SHA-256 of a file, streamed: the database in a snapshot is as big as the store, so no file is held in memory whole. */
async function sha256(file) {
  const hash = createHash('sha256');
  for await (const chunk of createReadStream(file)) hash.update(chunk);
  return hash.digest('hex');
}

/** The first bytes of a file (fewer when the file is shorter). */
async function head(file, length) {
  const handle = await fs.open(file, 'r');
  try {
    const { bytesRead, buffer } = await handle.read(Buffer.alloc(length), 0, length, 0);
    return buffer.subarray(0, bytesRead);
  } finally {
    await handle.close();
  }
}

/** A file copied by streaming, created exclusively and readable by the user alone, like every file a restore writes. */
async function copy(source, target) {
  await pipeline(createReadStream(source), createWriteStream(target, { flags: 'wx', mode: 0o600 }));
}

export async function restoreBackup(backup, destination) {
  backup = path.resolve(backup);
  destination = path.resolve(destination);
  const manifest = JSON.parse(await fs.readFile(path.join(backup, 'manifest.json'), 'utf8'));
  if (manifest.version !== 1) throw new Error('Unsupported backup version');
  // Which store wrote the snapshot decides what is checked below: the provider named the files it wrote, and only the
  // one that ships netpi.db can be checked for its header here. A manifest without it is from a build that did not
  // name the provider, and nothing here can say what reads its files.
  const provider = manifest.provider;
  if (typeof provider !== 'string' || !provider)
    throw new Error('The manifest does not say which storage provider wrote the snapshot (one made before it did)');
  const listed = manifest.files;
  if (!listed || typeof listed !== 'object' || Array.isArray(listed) || Object.keys(listed).length === 0)
    throw new Error('The manifest lists no files');
  const verified = new Set();
  // Whatever the manifest lists, each with a checksum of its own, read from inside the snapshot only. An older
  // snapshot restores exactly what it has (its ideas files, before they became tables in the database).
  for (const [name, expected] of Object.entries(listed)) {
    if (typeof expected !== 'string' || expected.length !== 64) throw new Error(`The manifest has no checksum for ${name}`);
    const hash = await sha256(inside(backup, name));
    if (hash !== expected.toLowerCase()) throw new Error(`Checksum mismatch: ${name}`);
    verified.add(name);
  }
  if (provider === 'sqlite') {
    if (!verified.has('netpi.db')) throw new Error('The snapshot has no netpi.db');
    if ((await head(inside(backup, 'netpi.db'), 16)).toString('ascii') !== 'SQLite format 3\0') throw new Error('Invalid SQLite database');
  }
  if (!verified.has('settings.json')) throw new Error('The snapshot has no settings.json');
  const settings = JSON.parse(await fs.readFile(inside(backup, 'settings.json'), 'utf8'));
  if (!settings || Array.isArray(settings) || typeof settings !== 'object') throw new Error('Invalid settings');
  // The idea images are files under the home, not in the store: the snapshot carries each one as a file of its own and
  // the manifest says which file of the images directory it is, so a restored backlog keeps the pictures its cards show.
  // A snapshot from before this has no ideaImages and simply has none to put back.
  const images = manifest.ideaImages ?? {};
  if (!images || typeof images !== 'object' || Array.isArray(images)) throw new Error('Invalid ideaImages');
  const imageFiles = new Map();
  for (const [name, target] of Object.entries(images)) {
    if (!verified.has(name)) throw new Error(`The manifest lists an idea image, ${name}, that the snapshot does not have`);
    imageFiles.set(imageTarget(target), inside(backup, name));
  }
  // Exclusive creation rejects existing homes, including symlinks. On a write failure keep the partial directory
  // for inspection; another restore must choose a different destination.
  await fs.mkdir(destination);
  for (const name of verified) await copy(inside(backup, name), inside(destination, name));
  if (imageFiles.size) {
    await fs.mkdir(path.join(destination, 'idea-images'), { mode: 0o700 });
    for (const [target, source] of imageFiles) await copy(source, path.join(destination, 'idea-images', target));
  }
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
