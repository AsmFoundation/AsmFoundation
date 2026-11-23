# Benchmarks

The same work, written in other languages at their best, measured against the foundation's assembly. A
project of this repository, not a repository of its own (the owner's words: "a benchmarks project (not
repo)"), whose results go on the website's Evidence page.

> **Status: planned.** The scope below was decided on 2026-11-23; nothing is measured yet. Until a result
> exists here, the Evidence page shows only the measurements the projects made themselves.

## What the owner decided (2026-11-23)

The brief: "we need to also measure a bunch of other languages and have their best and fair attempt."

| Question | Decision |
|---|---|
| What to measure first | **Kernels now, services as they are ready.** Six functions the foundation ships come first; whole servers follow as each assembly service is ready for them. |
| Languages | **C#, C++, Go, Java, JavaScript (Node.js), Python, Rust and Zig**, next to the assembly. More can join by pull request. C was offered and not chosen. |
| A language's attempt | **Its best.** One entry per language: the fastest correct code its experts can write in it. |

## The workloads

### Kernels, now

Each kernel is one our assembly already performs, so every comparison has a real assembly entry.

| Kernel | What it tests | Our assembly | Measured as |
|---|---|---|---|
| CRC-32C, 64 B to 1 MiB | a hardware instruction, kept busy | AsmSdk's Crc32C sample | ns per call and GB/s |
| Keyed hash of a key | hash-table hashing | KvAsm's AES-NI hash, and its SipHash-2-4 | ns per key |
| HPACK Huffman decoding | bit-level, branchy decoding | HttpAsm's and KvAsm's HPACK | MB/s |
| HTTP/1.1 request parsing | text protocol parsing | HttpAsm's parser | requests/s |
| Protobuf varint decoding | small-integer decoding | KvAsm's protobuf codec | values/s |
| Word count of a large file | a whole small program, I/O included | AsmSdk's WordCount sample | GB/s |

### Services, later

| Service | Driven by | Our assembly | Measured as |
|---|---|---|---|
| Key-value GET and SET over gRPC | one gRPC load generator for all | KvAsm | requests/s and server CPU per request |
| gRPC unary call | h2load | GrpcAsm | calls/s and CPU per call |
| HTTP/1.1 and HTTP/2 "hello" | wrk2 and h2load | HttpAsm | requests/s, and p99 at a fixed rate |

For a service, a language's best is its best stack, tuned by people who know it.

## The rules

**A language's best.** Its entry may use anything the language and its toolchain offer: intrinsics, SIMD,
unsafe code, any compiler or runtime flags, and its standard library, even where that library is written
in C (Python's `zlib`, for example). It may not use third-party native libraries or inline assembly: then
the code measured would not be that language's.

**The same for every entry:**

- **One machine, one input set.** Every entry reads the same inputs, and its outputs are checked against
  one reference before any timing counts.
- **One core, always.** Every result gives its one-core figure (the site's rule, "1 core everywhere"),
  and every core's as well where the work is parallel.
- **Paired rounds.** Entries are measured in alternating rounds on the same host, and reported as
  medians with their spread, so a noisy hour cannot favor one of them.
- **Pinned versions.** Each result names its compiler or runtime, its version and its flags.
- **Open entries.** All code is public, and anyone may replace an entry with a faster correct one by
  pull request, with the measurement that shows it.

## How it will work

These are the defaults the implementation starts from; each will be reviewed when it is built.

- **Layout:** `kernels/<kernel>/` holds the kernel's specification, its inputs and reference outputs,
  and one folder per entry (`asm/`, `csharp/`, `cpp/`, `go/`, `java/`, `javascript/`, `python/`,
  `rust/`, `zig/`). `services/` follows the same pattern later. `results/<date>-<machine>/` holds each
  session's raw runs and its summary.
- **Timing:** each entry times itself in-process with its language's standard harness (BenchmarkDotNet,
  Google Benchmark, `testing.B`, JMH, tinybench, `timeit`, criterion, `std.time`), on the same input
  sizes, and writes one JSON summary in a shared format. The assembly is called from a native harness
  with no foreign-function layer in between, so no entry pays for a call another does not make.
- **The host:** the machine the Evidence page's other results come from, an Intel Core i9-13900K under
  Linux. Every run is pinned to the same performance core: the CPU mixes performance and efficiency cores,
  and a run must never land on the other kind.
- **The driver:** a .NET file-based app (`run.cs`) builds every entry, verifies its outputs, runs the
  paired rounds and writes the session's summary, which the site generator turns into charts.
