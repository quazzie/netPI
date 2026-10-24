// The Phone tab's QR encoder (plugins/NetPI.Phone/ui/qr.js): byte mode, ECC level L, versions 1-10. Pinned against the
// QR spec (ISO/IEC 18004): the Reed-Solomon generator and error-correction bytes, the 15-bit format and 18-bit version
// information, the symbol geometry (size, finder, timing, alignment), the version chosen at each capacity boundary, and
// the zero-syndrome property of every data+ECC block (GF(256) computed here, not reused from the encoder).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  qrMatrix, qrCodewords, rsGenerator, rsRemainder, formatBits, versionBits, alignPositions,
} from '../../plugins/NetPI.Phone/ui/qr.js';

// An independent GF(256) (poly 0x11d, alpha = 2), so the syndrome check does not reuse the encoder's own arithmetic.
function gmul(a, b) {
  let r = 0;
  for (let i = 0; i < 8; i++) { r = (r << 1) ^ ((r & 0x80) ? 0x11d : 0); r ^= (b & (1 << (7 - i))) ? a : 0; }
  return r & 255;
}
const POW2 = (() => { const t = new Array(256); let v = 1; for (let i = 0; i < 255; i++) { t[i] = v; v = gmul(v, 2); } t[255] = t[0]; return t; })();
// The RS syndromes of a codeword: evaluate data.x^t + ecc at alpha^0 .. alpha^(t-1); all zero iff it is a valid codeword.
function syndromes(data, ecc) {
  const full = data.concat(ecc);
  return ecc.map((_, i) => full.reduce((s, c) => gmul(s, POW2[i]) ^ c, 0));
}
// The longest byte-mode text each version holds at ECC L (and, past version 10, the encoder throws).
const CAPS = [[1, 17], [2, 32], [3, 53], [4, 78], [5, 106], [6, 134], [7, 154], [8, 192], [9, 230], [10, 271]];

test('phone-qr: the 7-byte generator polynomial (version 1-L) is the spec value', () => {
  assert.deepEqual(rsGenerator(7), [0x7f, 0x7a, 0x9a, 0xa4, 0x0b, 0x44, 0x75]);
});

test('phone-qr: the error-correction bytes of a data block', () => {
  assert.deepEqual(rsRemainder([1, 2, 3, 4, 5, 6, 7, 8], 7), [0xba, 0xdd, 0xc8, 0xac, 0xb6, 0x89, 0x34]);
  assert.deepEqual(rsRemainder([1, 2, 3, 4, 5, 6, 7, 8, 9, 10], 10), [0xe5, 0xfa, 0xb1, 0xc4, 0xf3, 0x1e, 0x26, 0x46, 0x77, 0x9b]);
  assert.deepEqual(rsRemainder([0xff], 7), [0x80, 0xa4, 0x61, 0x2f, 0xb7, 0xea, 0xc8]);
  assert.deepEqual(rsRemainder([0], 7), [0, 0, 0, 0, 0, 0, 0], 'all-zero data has all-zero ecc');
});

test('phone-qr: the 15-bit format information for ECC L, every mask', () => {
  const expected = [0x77c4, 0x72f3, 0x7daa, 0x789d, 0x662f, 0x6318, 0x6c41, 0x6976];
  expected.forEach((bits, mask) => assert.equal(formatBits(mask), bits, `mask ${mask}`));
});

test('phone-qr: the 18-bit version information for versions 7-10', () => {
  assert.equal(versionBits(7), 0x07c94);
  assert.equal(versionBits(8), 0x085bc);
  assert.equal(versionBits(9), 0x09a99);
  assert.equal(versionBits(10), 0x0a4d3);
});

test('phone-qr: the symbol size is 17 + 4*version', () => {
  for (const [v, max] of CAPS) assert.equal(qrMatrix('a'.repeat(max)).length, 17 + 4 * v, `version ${v}`);
});

test('phone-qr: the finder and timing patterns are in place', () => {
  const m = qrMatrix('a'.repeat(18)); // version 2, size 25
  const size = m.length;
  assert.equal(size, 25);
  // The 7x7 finder: a dark square with a light ring at distance 2 from its center.
  const finder = (cx, cy) => {
    for (let dy = -3; dy <= 3; dy++) for (let dx = -3; dx <= 3; dx++)
      assert.equal(m[cy + dy][cx + dx], Math.max(Math.abs(dx), Math.abs(dy)) !== 2, `finder (${cx + dx},${cy + dy})`);
  };
  finder(3, 3); finder(size - 4, 3); finder(3, size - 4);
  // The timing pattern: row 6 and column 6 alternate, dark on even coordinates, between the finders.
  for (let i = 9; i <= size - 10; i++) {
    assert.equal(m[6][i], i % 2 === 0, `timing (6,${i})`);
    assert.equal(m[i][6], i % 2 === 0, `timing (${i},6)`);
  }
});

test('phone-qr: the alignment pattern positions for versions 1-10', () => {
  assert.deepEqual(alignPositions(1), []);
  assert.deepEqual(alignPositions(2), [6, 18]);
  assert.deepEqual(alignPositions(3), [6, 22]);
  assert.deepEqual(alignPositions(4), [6, 26]);
  assert.deepEqual(alignPositions(5), [6, 30]);
  assert.deepEqual(alignPositions(6), [6, 34]);
  assert.deepEqual(alignPositions(7), [6, 22, 38]);
  assert.deepEqual(alignPositions(8), [6, 24, 42]);
  assert.deepEqual(alignPositions(9), [6, 26, 46]);
  assert.deepEqual(alignPositions(10), [6, 28, 50]);
});

test('phone-qr: an alignment pattern is drawn at its center', () => {
  const m = qrMatrix('a'.repeat(154)); // version 7, size 45, alignment centers 6/22/38
  const c = 22;
  for (let dy = -2; dy <= 2; dy++) for (let dx = -2; dx <= 2; dx++)
    assert.equal(m[c + dy][c + dx], Math.max(Math.abs(dx), Math.abs(dy)) !== 1, `alignment (${c + dx},${c + dy})`);
});

test('phone-qr: version selection at the capacity boundaries', () => {
  for (const [v, max] of CAPS) {
    assert.equal(qrMatrix('a'.repeat(max)).length, 17 + 4 * v, `the longest text v${v} holds is ${max} bytes`);
    if (v < 10) assert.equal(qrMatrix('a'.repeat(max + 1)).length, 17 + 4 * (v + 1), `one byte more overflows to v${v + 1}`);
  }
  assert.throws(() => qrMatrix('a'.repeat(272)), /too long/, 'past version 10 throws');
});

test('phone-qr: every data+ECC block has zero Reed-Solomon syndromes', () => {
  // The check has teeth: a wrong ecc byte is detected.
  assert.ok(!syndromes([1, 2, 3, 4, 5, 6, 7, 8], [0, 0, 0, 0, 0, 0, 0]).every((s) => s === 0), 'a wrong ecc is detected');
  for (const [v, max] of CAPS) {
    const { codewords, eccLen } = qrCodewords('a'.repeat(max));
    for (const { data, ecc } of codewords) {
      assert.equal(ecc.length, eccLen, `v${v} block ecc length`);
      const s = syndromes(data, ecc);
      assert.ok(s.every((x) => x === 0), `v${v} block ${data.length}+${ecc.length} has zero syndromes, got ${s.join(' ')}`);
    }
  }
});
