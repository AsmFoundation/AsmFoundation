// The JavaScript (Node.js) entry of the CRC-32C kernel: JavaScript at its best, timed with the
// kernel's protocol (the kernel's README).
//
// Node.js has no CRC-32C of its own (zlib.crc32 is the IEEE CRC-32) and JavaScript has no access
// to the crc32 instruction, so the fastest correct JavaScript is a table method. Measured on the
// benchmark machine before choosing (2026-11-26, 1 MiB): slicing-by-16 over 32-bit words 1.70 GB/s,
// slicing-by-8 over words 1.18, slicing-by-8 over bytes 1.18; at 64 bytes the two word variants
// tie. This entry is slicing-by-16 over 32-bit words, with 16 tables of 256 entries (16 KiB, in
// the L1 cache).
//
// The protocol: N worker threads, each checksumming its own copy of the input, wait at a shared
// barrier after a warm-up; the main thread opens the window, sleeps through it and raises the
// stop flag; each worker counts its calls and times its own window, and checks its checksum
// before and after. "Bytes allocated" comes from V8's GC profiler and heap statistics of each
// worker's isolate across its window.
//
// Usage: node crc32c.mjs --threads N --inputs DIR --sizes A,B --expect X,Y --window S,S --warmup S,S

import fs from 'node:fs';
import path from 'node:path';
import v8 from 'node:v8';
import { Worker, isMainThread, workerData, parentPort } from 'node:worker_threads';

/** CRC-32C's polynomial, bit-reflected. */
const POLY = 0x82f63b78;

/**
 * The slicing tables: T[0] is the byte-wise table; T[k][b] is the CRC register after byte b
 * followed by k zero bytes, so 16 bytes reduce with 16 independent lookups.
 * @type {Int32Array[]}
 */
const T = (() => {
  const t = [new Int32Array(256)];
  for (let i = 0; i < 256; i++) {
    let c = i;
    for (let k = 0; k < 8; k++) c = (c >>> 1) ^ (POLY & -(c & 1));
    t[0][i] = c;
  }
  for (let j = 1; j < 16; j++) {
    t[j] = new Int32Array(256);
    for (let i = 0; i < 256; i++) t[j][i] = (t[j - 1][i] >>> 8) ^ t[0][t[j - 1][i] & 255];
  }
  return t;
})();
const [T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15] = T;

/**
 * CRC-32C of a buffer, slicing-by-16 over little-endian 32-bit words, then single bytes.
 * @param {Uint8Array} u8 the bytes; its buffer must start 4-byte aligned (every buffer here does)
 * @param {Int32Array} w32 the same memory as 32-bit words
 * @returns {number} the checksum, as an unsigned 32-bit number
 */
function crc32c(u8, w32) {
  let crc = -1;
  let i = 0;
  const n = u8.length;
  const n16 = n & ~15;
  for (; i < n16; i += 16) {
    const j = i >> 2;
    // The register folds into the first word; the 16 lookups do not depend on each other.
    const a = w32[j] ^ crc;
    const b = w32[j + 1];
    const c = w32[j + 2];
    const d = w32[j + 3];
    crc = T15[a & 255] ^ T14[(a >>> 8) & 255] ^ T13[(a >>> 16) & 255] ^ T12[a >>> 24] ^
          T11[b & 255] ^ T10[(b >>> 8) & 255] ^ T9[(b >>> 16) & 255] ^ T8[b >>> 24] ^
          T7[c & 255] ^ T6[(c >>> 8) & 255] ^ T5[(c >>> 16) & 255] ^ T4[c >>> 24] ^
          T3[d & 255] ^ T2[(d >>> 8) & 255] ^ T1[(d >>> 16) & 255] ^ T0[d >>> 24];
  }
  for (; i < n; i++) crc = T0[(crc ^ u8[i]) & 255] ^ (crc >>> 8);
  return ~crc >>> 0;
}

/** Slots of the shared control array. */
const READY = 0;
const START = 1;
const STOP = 2;

/**
 * A worker: its own copy of the input, the check, the warm-up, the barrier, the timed loop.
 * Posts {calls, seconds, allocated} or {error}.
 */
function worker() {
  const { file, size, expected, warmupMs, batch, ctrlBuffer } = workerData;
  const ctrl = new Int32Array(ctrlBuffer);
  // The worker's own memory, 4-byte aligned for the word view.
  const own = new ArrayBuffer(size);
  const u8 = new Uint8Array(own);
  u8.set(fs.readFileSync(file));
  const w32 = new Int32Array(own, 0, size >> 2);
  if (crc32c(u8, w32) !== expected) {
    Atomics.add(ctrl, READY, 1);
    Atomics.notify(ctrl, READY);
    parentPort.postMessage({ error: `checksum ${crc32c(u8, w32).toString(16)}, expected ${expected.toString(16)}` });
    return;
  }
  // Warm-up in two halves with a pause between: V8 optimizes on a background thread, which in a
  // process confined to one core needs the core for a moment.
  for (let end = performance.now() + warmupMs / 2; performance.now() < end;) for (let k = 0; k < batch; k++) crc32c(u8, w32);
  Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 100);
  for (let end = performance.now() + warmupMs / 2; performance.now() < end;) for (let k = 0; k < batch; k++) crc32c(u8, w32);
  Atomics.add(ctrl, READY, 1);
  Atomics.notify(ctrl, READY);
  Atomics.wait(ctrl, START, 0);
  const heapBefore = v8.getHeapStatistics().used_heap_size;
  const profiler = new v8.GCProfiler();
  profiler.start();
  const began = performance.now();
  let calls = 0;
  let acc = 0;
  // The flag is read once per batch: microseconds at 64 bytes, one call from 64 KiB up.
  while (Atomics.load(ctrl, STOP) === 0) {
    for (let k = 0; k < batch; k++) acc ^= crc32c(u8, w32);
    calls += batch;
  }
  const seconds = (performance.now() - began) / 1000;
  const gc = profiler.stop();
  const heapAfter = v8.getHeapStatistics().used_heap_size;
  // Allocation volume across the window: the heap's growth between collections, summed.
  let allocated = 0;
  let previous = heapBefore;
  for (const s of gc.statistics) {
    allocated += Math.max(0, s.beforeGC.heapStatistics.usedHeapSize - previous);
    previous = s.afterGC.heapStatistics.usedHeapSize;
  }
  allocated += Math.max(0, heapAfter - previous);
  if (crc32c(u8, w32) !== expected) {
    parentPort.postMessage({ error: 'checksum after the window is wrong' });
    return;
  }
  parentPort.postMessage({ calls, seconds, allocated, acc });
}

/**
 * Parses the protocol's options.
 * @param {string[]} args the command line after the script
 * @returns {{threads:number, inputs:string, sizes:number[], expect:number[], window:number[], warmup:number[]}}
 * @throws {Error} naming the first missing or malformed option
 */
function parse(args) {
  const values = {};
  for (let i = 0; i + 1 < args.length; i += 2) values[args[i]] = args[i + 1];
  /**
   * The value of a required option.
   * @param {string} name the option, such as --threads
   * @returns {string} its value
   * @throws {Error} when the command line lacks it
   */
  const need = (name) => {
    if (!(name in values)) throw new Error(`${name} is required`);
    return values[name];
  };
  /**
   * A required option's comma-separated list of numbers.
   * @param {string} name the option
   * @param {(item: string) => number} parse turns one item into a number (NaN when it cannot)
   * @returns {number[]} the numbers, in order
   * @throws {Error} when the option is missing or an item is not a finite number
   */
  const numbers = (name, parse) => need(name).split(',').map((s) => {
    const v = parse(s.trim());
    if (!Number.isFinite(v)) throw new Error(`${name}: bad item ${s}`);
    return v;
  });
  const o = {
    threads: Number.parseInt(need('--threads'), 10),
    inputs: need('--inputs'),
    sizes: numbers('--sizes', (s) => Number.parseInt(s, 10)),
    expect: numbers('--expect', (s) => Number.parseInt(s.replace(/^0x/, ''), 16)),
    window: numbers('--window', Number.parseFloat),
    warmup: numbers('--warmup', Number.parseFloat),
  };
  if (!(o.threads > 0)) throw new Error('--threads must be above 0');
  if ([o.expect, o.window, o.warmup].some((l) => l.length !== o.sizes.length)) {
    throw new Error('--sizes, --expect, --window and --warmup need one item per size');
  }
  return o;
}

/**
 * Times one cell with `threads` workers and returns their results.
 * @param {string} file the input file, which each worker reads into memory of its own
 * @param {number} size its size in bytes
 * @param {number} expected its CRC-32C, which every worker checks before and after its window
 * @param {number} threads how many workers, one per core the process may use
 * @param {number} windowS the timed window, in seconds
 * @param {number} warmupS each worker's warm-up before the barrier, in seconds
 * @returns {Promise<Array<{calls:number, seconds:number, allocated:number}>>} each worker's count, window and allocated bytes
 * @throws {Error} when a worker's checksum is wrong, or a worker fails
 */
async function runCell(file, size, expected, threads, windowS, warmupS) {
  const ctrlBuffer = new SharedArrayBuffer(16);
  const ctrl = new Int32Array(ctrlBuffer);
  const batch = Math.max(1, Math.floor(65536 / size));
  const workers = [];
  const results = [];
  for (let t = 0; t < threads; t++) {
    const w = new Worker(new URL(import.meta.url), {
      workerData: { file, size, expected, warmupMs: warmupS * 1000, batch, ctrlBuffer },
    });
    results.push(new Promise((resolve, reject) => {
      w.once('message', resolve);
      w.once('error', reject);
    }));
    workers.push(w);
  }
  // The barrier: wait until every worker is ready, then open the window for all at once.
  for (let ready; (ready = Atomics.load(ctrl, READY)) < threads;) Atomics.wait(ctrl, READY, ready, 50);
  Atomics.store(ctrl, START, 1);
  Atomics.notify(ctrl, START);
  Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, windowS * 1000);
  Atomics.store(ctrl, STOP, 1);
  const streams = await Promise.all(results);
  await Promise.all(workers.map((w) => w.terminate()));
  const failed = streams.find((s) => s.error);
  if (failed) throw new Error(failed.error);
  return streams;
}

/** Checks the check value, then runs every cell of the command line. */
async function main() {
  let o;
  try {
    o = parse(process.argv.slice(2));
  } catch (e) {
    console.error(e.message);
    process.exit(2);
  }
  const check = new ArrayBuffer(12);
  const checkBytes = new Uint8Array(check, 0, 9);
  checkBytes.set(Buffer.from('123456789'));
  if (crc32c(checkBytes, new Int32Array(check, 0, 2)) !== 0xe3069283) {
    console.error('check value of 123456789 is wrong');
    process.exit(1);
  }
  console.log(JSON.stringify({
    type: 'header',
    entry: 'javascript',
    kernel: 'slicing-by-16 over 32-bit words, Int32Array tables',
    runtime: `node ${process.version}, V8 ${process.versions.v8}`,
    threads: o.threads,
    affinity: 'unknown',
  }));
  for (let i = 0; i < o.sizes.length; i++) {
    const size = o.sizes[i];
    const file = path.join(o.inputs, `${size}.bin`);
    if (fs.statSync(file).size !== size) {
      console.error(`${file} does not hold ${size} bytes`);
      process.exit(1);
    }
    let streams;
    try {
      streams = await runCell(file, size, o.expect[i], o.threads, o.window[i], o.warmup[i]);
    } catch (e) {
      console.error(`${size} bytes on ${o.threads} threads: ${e.message}`);
      process.exit(1);
    }
    const allocated = streams.reduce((sum, s) => sum + s.allocated, 0);
    console.log(JSON.stringify({
      type: 'cell',
      size,
      threads: o.threads,
      streams: streams.map((s) => ({ calls: s.calls, seconds: Number(s.seconds.toFixed(6)) })),
      allocatedBytes: allocated,
    }));
  }
}

if (isMainThread) {
  await main();
} else {
  worker();
}
