# CRC-32C, every language at its best, 2026-11-27, Intel Core i9-13900K

The benchmarks project's first results: CRC-32C in C# (the simple loop and C# at its best), Rust,
Go, JavaScript and Python against the foundation's assembly. Each ran on one performance core and on
four, in 12 paired rounds, with every checksum checked before and after each window. The kernel's
README ([`kernels/crc32c`](../../kernels/crc32c/README.md)) has the entries, the protocol and the
trials behind the method.

- `summary.md`: every figure, with each round's spread, the ratio to the assembly and the scaling
  from one core;
- `summary.json`: the same, which the website's charts copy;
- `raw.jsonl`: every line every entry printed, with its round and core count;
- `environment.json`: the tools, the CPUs and the windows;
- `clock.csv`: the timed cores' clock every 5 seconds.

## Setup

- **Machine:** Intel Core i9-13900K, Windows 11 25H2, natively. The timed cores ran at 2.9 GHz:
  1,107 of the 1,427 clock samples (four CPUs every 5 seconds) read exactly 2,900 MHz, and all of
  them were between 2,865 and 2,906.
- **Load:** other Claude Code sessions were working on the machine, one of them running a QEMU
  virtual machine (about 2.3 cores).
- **Cores:**
  - each run is one process confined to performance cores 1 to 4 (logical CPUs 2, 4, 6, 8; the
    first one for one core), at High priority with power throttling off;
  - `tools/guard` held their other hyperthreads (3, 5, 7, 9) with `tpause` waits for the whole
    session.
- **Tools:**
  - the .NET SDK 11.0.100, running the C# entry on .NET 10;
  - Rust 1.97.1;
  - Go 1.27.4;
  - Node.js 22.14.0;
  - CPython 3.12.12;
  - AsmFoundation.Sdk 0.4.0, which built the assembly.
- **Windows:** 64 B and 1 MiB 0.5 s each, 64 MiB 2 s, after a warm-up of 2 s, 0.6 s and 1 s. The C#
  entry runs with `DOTNET_TC_CallCountingDelayMs=0` (the kernel's README says why).
- **Rounds:** a round runs every entry once on each core count, in a process of its own. The order of
  the core counts alternates and the entries' order rotates. Rounds took 146 to 162 s, from 22:26 to
  22:56 UTC on 2026-11-26.

## Results

Medians of the 12 rounds. Totals are all cores together; the ratio is to the assembly, round by round.

**64 bytes** (millions of checksums a second):

| Entry | 1 core | 4 cores | vs assembly, 1 core | 4 cores ÷ 1 core | Bytes allocated per call |
|---|---:|---:|---:|---:|---:|
| Assembly (AsmSdk) | 105 | 385 | 1.00x | 3.66x | 0 |
| C#, simple loop | 234 | 911 | 2.24x | 3.87x | 0 |
| C#, at its best | 178 | 685 | 1.70x | 3.88x | 0 |
| Rust | 281 | 1,060 | 2.69x | 3.76x | 0 |
| Go | 86.7 | 338 | 0.83x | 3.90x | 0 |
| JavaScript (Node.js) | 22.8 | 89.0 | 0.22x | 3.91x | 16.1 |
| Python | 0.210 | 0.809 | 0.002x | 3.85x | not counted |

**1 MiB** (GB/s):

| Entry | 1 core | 4 cores | vs assembly, 1 core | 4 cores ÷ 1 core |
|---|---:|---:|---:|---:|
| Assembly (AsmSdk) | 21.6 | 78.3 | 1.00x | 3.63x |
| C#, simple loop | 5.75 | 22.2 | 0.27x | 3.86x |
| C#, at its best | 21.6 | 82.5 | 1.00x | 3.82x |
| Rust | 21.6 | 79.8 | 1.00x | 3.70x |
| Go | 21.3 | 82.7 | 0.99x | 3.88x |
| JavaScript (Node.js) | 1.76 | 6.83 | 0.08x | 3.88x |
| Python | 0.0113 | 0.0349 | 0.001x | 3.08x |

**64 MiB from RAM** (time per checksum on each core):

| Entry | 1 core | 4 cores, each | GB/s, 4 cores together |
|---|---:|---:|---:|
| Assembly (AsmSdk) | 4.82 ms | 6.63 ms | 40.5 |
| C#, simple loop | 11.8 ms | 12.3 ms | 21.8 |
| C#, at its best | 4.75 ms | 6.36 ms | 42.2 |
| Rust | 4.89 ms | 6.77 ms | 39.7 |
| Go | 4.81 ms | 6.35 ms | 42.3 |
| JavaScript (Node.js) | 70.0 ms | 72.5 ms | 3.70 |
| Python | 5.92 s | 7.15 s | 0.0375 |

## What they show

- **At 64 bytes the languages that inline the checksum win.**
  - Rust leads at 2.69 times the assembly, then the C# loop at 2.24 and C# at its best at 1.70.
  - The assembly is a library called through a function pointer. Every checksum also pays the
    call, the dispatcher's indirect jump and the kernel's size checks. Inlined code pays none of
    them, and its successive checksums can overlap in the CPU.
  - Go's standard-library call costs a little more (0.83x).
  - The simple C# loop beats C# at its best here; both run a single chain at 64 bytes. Why the
    loop is the faster of the two at this size was not measured.
- **From 1 MiB up, C# at its best, Rust and Go equal the assembly.** The first two run the
  assembly's own algorithm (three `crc32` chains per block, merged with `pclmulqdq`) in
  intrinsics; Go's standard library runs Go's own assembly. So the assembly's 3.8x lead over the
  simple C# loop at 1 MiB is the algorithm's, not the language's.
- **JavaScript and Python cannot reach the `crc32` instruction.** Their best table methods run at
  8% and 0.05% of the assembly's speed at 1 MiB.
- **From RAM, four cores share the memory.** The four fastest entries reach 40 to 42 GB/s together,
  and each checksum takes a third longer than on one core alone (4.82 against 6.63 ms for the
  assembly). The simple C# loop, at about 5.5 GB/s per core, stays under that limit and scales 3.84x.
- **No runtime collapses on one core** once warmed up fairly: at 64 bytes every entry does 3.66 to
  3.91 times its one-core total on four cores. C#'s collapse came from .NET's tiering delay
  (the kernel's README), and the C# entry runs with that delay off.
- **Bytes allocated per call:**
  - **JavaScript allocates 16 bytes per call at 64 bytes:** V8 boxes the returned 32-bit
    checksum as a heap number. With the result masked into V8's 31-bit small integers, the
    allocation measured 0.00 bytes per call.
  - **Every compiled entry allocates nothing.** Go's process-wide count shows a few bytes per
    million calls, the runtime's own.
  - **Above 64 bytes,** JavaScript's per-call figures (25 B at 1 MiB, 733 B at 64 MiB) come to
    about 21 KB per window at both sizes, whatever the number of calls: V8's own allocation spread
    over few calls, not the checksum's.
  - **Python's are not counted:** CPython keeps no running total.
- **Python on four cores** scales 3.85x at 64 bytes but 3.08x at 1 MiB. Each process holds its
  own 10 MB of tables, four times that does not fit the 36 MB L3, and the table lookups miss.
  That is an inference from the sizes, not a measurement of the misses.

## Not comparable

- **With AsmSdk's own Crc32C runs** (`samples/Crc32C.Bench/results` in AsmSdk): those called the
  assembly from C# through P/Invoke, pinned threads instead of whole processes, and held the
  siblings with a guard spinning on `pause`.
- **With the other charts on the Benchmarks page:** KvAsm's and GrpcAsm's runs were made in Linux
  containers.
