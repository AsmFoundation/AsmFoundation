# Benchmarks

The same work, written in other languages at their best, measured against the foundation's assembly. A
project of this repository, not a repository of its own (the owner's words: "a benchmarks project (not
repo)"), whose results go on the website's [Benchmarks page](https://asmfoundation.github.io/benchmarks/).

> **Status: the first kernel is built.** The scope below was decided on 2026-11-23. On 2026-11-26 the owner
> asked for the first competition: "best achievable in python, rust, go, C#, nodejs - try to have them
> implement the best possible for 1 core and 4 cores", with a simple and an optimized C#, data from RAM and
> bytes allocated per operation. That is [`kernels/crc32c`](kernels/crc32c/README.md), measured in
> [`results/`](results/). The other five kernels and the other languages (C++, Java, Zig) are not built yet.

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
| CRC-32C, 64 B to 64 MiB ([built](kernels/crc32c/README.md)) | a hardware instruction, kept busy; data from RAM | AsmSdk's Crc32C sample | checksums/s, GB/s, time per call, bytes allocated per call |
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

## How it works

The defaults the plan started from, and what building the first kernel changed (2026-11-26):

- **Layout, as planned:**
  - `kernels/<kernel>/` holds the kernel's specification and one folder per entry (`asm/`,
    `csharp/`, `go/`, `javascript/`, `python/`, `rust/` so far);
  - `tools/` holds what the driver needs besides the entries (the core guard);
  - `results/<date>-<machine>-<kernel>/` holds each session's raw runs and its summary;
  - the inputs are generated, not committed: each kernel's README specifies them.
- **Timing: one protocol, written in every language,** instead of each language's standard harness.
  - BenchmarkDotNet, criterion, `testing.B` and the like time one thread, and the owner's question is
    one core against four.
  - So every entry runs the same windowed protocol: N threads, a barrier, a timed window, a stop
    flag, checksums checked before and after. Each writes the same JSON lines.
  - It also counts the bytes its runtime allocates. The kernel's README has the protocol.
  - The assembly is still called from a native harness with no foreign-function layer in between.
- **The host: the i9-13900K natively on Windows,** instead of under Linux.
  - Windows can confine a process to chosen logical CPUs. Linux under WSL2 cannot keep a thread
    off the efficiency cores: Hyper-V places the VM's CPUs where it likes.
  - **"N cores" is a process confined to N performance cores** (the first hyperthread of
    performance cores 1 to 4), at High priority, with power throttling (EcoQoS) off.
  - Each timed core's other hyperthread is held for the whole run by `tools/guard`, which waits
    there in `tpause`. A guard spinning on `pause` takes a varying share of the core (the CRC-32C
    README has the trials).
- **The driver, as planned:** `dotnet run benchmarks/run.cs -- <kernel>` builds every entry,
  checks it against its own reference, runs the paired rounds, and writes the session's summary
  (`summary.json` for the site's charts).
