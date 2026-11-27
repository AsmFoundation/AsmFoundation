# CRC-32C

The benchmarks project's first kernel: the same checksum written in every language at its best,
timed against the foundation's assembly on one core and on four. CRC-32C (Castagnoli) is the
checksum of iSCSI, ext4, SCTP and many storage and network formats; modern x86-64 CPUs compute it
with one instruction, `crc32`, so the question is how close each language gets to the hardware.

The owner's brief (2026-11-26): "We need also other competition, best achievable in python, rust,
go, C#, nodejs - try to have them implement the best possible for 1 core and 4 cores", with both a
simple and an optimized C#, data from RAM ("Less is better") and bytes allocated per operation.

## What every entry computes

- **CRC-32C:** the reflected polynomial `0x82F63B78`, initial value and final XOR `0xFFFFFFFF`.
  Every entry checks the standard check value first: `CRC-32C("123456789") = 0xE3069283`.
- **Three inputs:**
  - 64 bytes, a short message, where calling the checksum costs as much as computing it;
  - 1 MiB, a large buffer that stays in the core's own cache;
  - 64 MiB, data that must come from RAM (four times the CPU's 36 MB of L3, per thread).
- **The bytes** are splitmix64 seeded with the size, each 64-bit output written little-endian, cut
  to the size. `run.cs` writes them (`Reference.Input`); any language can regenerate them.

| Size | First 8 bytes | CRC-32C |
|---|---|---|
| 64 B | `c38ce6fb487296d6` | `0xdd776e82` |
| 1 MiB | `2d1c2a00248c5433` | `0x28372720` |
| 64 MiB | `895151233128bfa2` | `0x9c03d0ce` |

The driver computes each expected checksum with its own reference, byte at a time, which shares
no code with any entry.

## The protocol

Every entry is one program that takes the same command line and prints the same lines:

```cli
<entry> --threads N --inputs DIR --sizes 64,1048576,67108864 --expect 0xdd776e82,0x28372720,0x9c03d0ce --window 0.5,0.5,2 --warmup 2,0.6,1
```

- **One cell per size.** N threads (Python: N processes) each read the input into memory of
  their own and check their checksum. Each then runs the warm-up and waits at a barrier.
- **The window.** The main thread opens the window, sleeps through it and raises a stop flag.
  Each thread reads the flag between batches (a batch is one call from 64 KiB up, and a few
  microseconds of calls at 64 bytes). It counts its calls, times its own window, and checks its
  checksum again.
- **Output.** A header line, then one JSON line per cell:

  ```json
  {"type":"cell","size":64,"threads":4,"streams":[{"calls":123,"seconds":0.5}],"allocatedBytes":0,"affinity":"0x154"}
  ```

  A wrong checksum, before or after the window, ends the program with exit code 1, and nothing is
  printed as a result.
- **Throughput** is the sum of the threads' own rates (each one's calls over its own window).
  "Per call" is one call's time on its core while the others run: cores divided by the
  throughput.
- **Bytes allocated per call** is the runtime's own count across the window, divided by the
  calls; where the runtime keeps no count, it says so.

### Cores

"N cores" means **the process may use exactly N cores**, as in KvAsm's and GrpcAsm's benchmarks.
The driver confines each run to N performance cores of the i9-13900K: its affinity mask is
logical CPUs 2, 4, 6 and 8, the first hyperthread of performance cores 1 to 4, the first N of
them. Core 0 keeps the interrupts Windows sends it. The driver also:

- raises the process to High priority and turns Windows' power throttling (EcoQoS) off, which
  otherwise runs a process without a window on efficiency cores at about half speed;
- holds each timed core's other hyperthread (CPUs 3, 5, 7 and 9) for the whole run with
  `tools/guard`, which waits there in `tpause`. Windows then schedules no other process's thread
  on a timed core, and the waiting hyperthread leaves the core to the timed thread.

**Why the guard waits in `tpause`** (trials of 2026-11-26, one core at 64 B, millions of
checksums a second):

| Sibling hyperthread | JavaScript | Rust |
|---|---:|---:|
| free, on a quiet machine | 23.0 | 262 |
| free, while another session ran a QEMU VM | 11.8 to 14.0 | 147 to 179 |
| held by a guard spinning on `pause` | 13.6 to 16.1 | 187 to 243 |
| held by the `tpause` guard | 17.3 to 22.6 | 195 to 261 |

- **A free sibling** is used by other work as soon as the machine is busy.
- **A spinning guard** keeps other work off but takes issue slots of its own. In an earlier
  trial, .NET's `SpinWait` guard cost JavaScript a third (23.0 against 15.5), and in another the
  same guard cost nothing: its cost varies from one guard process to the next.
- **The `tpause` guard** gave the figures closest to the quiet machine, and in the driver's runs
  it reproduced them (22.7 and 281).

A runtime's helper threads (its JIT compiler, garbage collector, event loop) share the same N
cores. On one core they compete with the checksum, which is where a runtime's one-core figure can
collapse; the owner's rule is that the four-core figure is then the one to present.

**C# collapsed on one core, but only during warm-up.** With .NET's default tiered compilation, a
process confined to one core still ran the 64-byte checksum unoptimized after 3 s of warm-up.
Tiering delays promotion until no new method has been compiled for a while, and on one core that
point kept moving. Measured on one core at 64 B, the C# loop, millions of checksums a second:

| Setting | Rate |
|---|---:|
| defaults, 0.6 s to 3 s of warm-up | 8.7 to 9.1 |
| defaults, 4 or 5 s of warm-up | 242 to 244 |
| `DOTNET_TC_CallCountingDelayMs=0`, 2 s of warm-up | 242 |
| tiering on, dynamic PGO off, 2 s | 169 |
| loops fully optimized from the first call, 2 s | 160 |

So the driver runs both C# variants with `DOTNET_TC_CallCountingDelayMs=0`. It reaches the
steady state, the same profile-guided code a long warm-up reaches, within the 2-second first
warm-up. Turning tiering or dynamic PGO off would cost C# about 30%. The steady state is what a
long-running service gets; a .NET service on one core takes seconds to get there.

Every entry reports its affinity mask with each cell (JavaScript cannot read it), and the driver
stops if a run used other cores than asked.

## The entries

The rules are the benchmarks project's: each language at its best, its standard library allowed
whatever it is written in, and no third-party native code or inline assembly.

| Entry | Language and runtime | The checksum | Bytes allocated, counted by |
|---|---|---|---|
| `asm` | AsmSdk's Crc32C library: x64 NASM built by AsmFoundation.Sdk 0.4.0 from nuget.org; a small Rust program loads it and calls `crc32c_append` directly | three `crc32` chains per block merged with `pclmulqdq`, the path its dispatcher picks on this CPU | the harness's counting allocator |
| `csharp-loop` | C#, .NET 10, `DOTNET_TC_CallCountingDelayMs=0` | `BitOperations.Crc32C` over 8-byte words: the loop a C# developer writes first | `GC.GetAllocatedBytesForCurrentThread` |
| `csharp-best` | C#, .NET 10, `DOTNET_TC_CallCountingDelayMs=0` | C# at its best: the assembly's 3-way algorithm in `Sse42.X64` and `Pclmulqdq` intrinsics | the same |
| `rust` | Rust, edition 2024, `-C target-cpu=native` | the assembly's 3-way algorithm in `std::arch` intrinsics | a counting global allocator |
| `go` | Go 1.27 | `hash/crc32` with the Castagnoli table, the standard library's own SSE4.2 assembly | `runtime.MemStats.TotalAlloc` |
| `javascript` | Node.js | slicing-by-16 over 32-bit words; one worker thread per core | V8's `GCProfiler` and heap statistics |
| `python` | CPython | 16-bit tables over 64-bit words; one process per core (the GIL) | not counted: CPython keeps no running total |

- **No standard CRC-32C.** .NET, Node.js and Python have none of their own: .NET's
  System.IO.Hashing, Node's `zlib.crc32` and Python's `zlib`/`binascii` are the IEEE CRC-32. So
  their entries are written here.
- **JavaScript and Python cannot reach the `crc32` instruction,** so their best is a table
  method. Each entry's comment records the measurement that chose its variant.
- **The assembly is called without a foreign-function layer:** a plain call through a function
  pointer, as any native program calls a library. So it pays for no call that another entry
  does not make; called from C# through P/Invoke it pays more (AsmSdk's
  `samples/Crc32C.Bench` measures that call).
- **The library's sources** (`asm/lib/crc32c.asm`, `include/crc32c.inc`, `crc32c.map`) are AsmSdk
  7c4ad4f's `samples/Crc32C` files, unchanged:

  | File | SHA-256 |
  |---|---|
  | `crc32c.asm` | `2f22d980c110f79c08c32d8941219776a5f437e3eaa4a67d5ba3985a30e21abb` |
  | `include/crc32c.inc` | `c87eea359fe16b20d8500f76390e90ab95e58ca5d5a32d09725f696abb1d5707` |
  | `crc32c.map` | `adc6fef421a04684180249c1411f971bbf0530faf66b21f2ea92a7642231f946` |

## Running it

On Windows, with the .NET 10 SDK, Rust (cargo), Go, Node.js and Python on PATH (or Go given with
`--go`):

```sh
dotnet run benchmarks/run.cs -- crc32c
# a smoke run: one round, into a temporary folder
dotnet run benchmarks/run.cs -- crc32c --quick
```

The driver builds every entry outside the repository
(`%LOCALAPPDATA%\asmfoundation-benchmarks\work\crc32c`), checks each one, and runs 12 paired
rounds. It writes `results/<date>-<machine>-crc32c/`:

- `raw.jsonl`: every line every entry printed, with its round and core count;
- `summary.md` and `summary.json`: medians, spreads, ratios to the assembly paired by round,
  scaling from one core, bytes allocated per call;
- `environment.json`: the tools' versions and the setup;
- `clock.csv`: the timed cores' clock every 5 seconds.

A round takes every entry on every core count once. The order of the core counts alternates
from round to round, and the order of the entries rotates.
