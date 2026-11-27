# CRC-32C, 12 paired rounds, 2026-11-26 22:26 to 22:56 UTC

13th Gen Intel(R) Core(TM) i9-13900K; cores = processes confined to logical CPUs 2,4,6,8, the first N for N cores, siblings 3,5,7,9 guarded.
Tools: dotnet 11.0.100; cargo cargo 1.97.1 (c980f4866 2026-06-30); rustc rustc 1.97.1 (8bab26f4f 2026-07-14); go go version go1.27.4 windows/amd64; node v22.14.0; python Python 3.12.12.

Medians over the rounds. Throughput is all cores together; "per call" is one call's time on its core while the others run; "vs asm" is the median of the per-round ratios to the assembly (above 1: faster than the assembly); "x1 core" the median of the per-round ratios to the same entry on one core; allocated is bytes per call (n/a: the runtime keeps no count).

## 64 B

| Entry | Cores | Calls/s | GB/s | Per call | Lowest to highest round, calls/s | vs asm | x1 core | Allocated per call |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| Assembly (AsmSdk) | 1 | 105M | 6.70 | 9.55 ns | 103M to 106M | 1.000x |  | 0 B |
| C#, simple loop | 1 | 234M | 15.0 | 4.28 ns | 231M to 239M | 2.236x |  | 0 B |
| C#, at its best | 1 | 178M | 11.4 | 5.62 ns | 176M to 182M | 1.699x |  | 0 B |
| Rust | 1 | 281M | 18.0 | 3.56 ns | 280M to 284M | 2.687x |  | 0 B |
| Go | 1 | 86.7M | 5.55 | 11.5 ns | 86.4M to 86.8M | 0.828x |  | 2.36E-06 B |
| JavaScript (Node.js) | 1 | 22.8M | 1.46 | 43.9 ns | 22.7M to 22.8M | 0.217x |  | 16.1 B |
| Python | 1 | 210k | 0.0135 | 4.75 us | 206k to 212k | 0.002x |  | n/a |
| Assembly (AsmSdk) | 4 | 385M | 24.6 | 10.4 ns | 350M to 405M | 1.000x | 3.66x | 0 B |
| C#, simple loop | 4 | 911M | 58.3 | 4.39 ns | 828M to 936M | 2.354x | 3.87x | 0 B |
| C#, at its best | 4 | 685M | 43.8 | 5.84 ns | 583M to 736M | 1.779x | 3.88x | 0 B |
| Rust | 4 | 1.06G | 67.7 | 3.78 ns | 933M to 1.11G | 2.740x | 3.76x | 0 B |
| Go | 4 | 338M | 21.6 | 11.8 ns | 330M to 343M | 0.879x | 3.90x | 6.15E-07 B |
| JavaScript (Node.js) | 4 | 89.0M | 5.70 | 44.9 ns | 83.8M to 91.1M | 0.230x | 3.91x | 16.1 B |
| Python | 4 | 809k | 0.0518 | 4.95 us | 730k to 828k | 0.002x | 3.85x | n/a |

## 1 MiB

| Entry | Cores | Calls/s | GB/s | Per call | Lowest to highest round, calls/s | vs asm | x1 core | Allocated per call |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| Assembly (AsmSdk) | 1 | 20.6k | 21.6 | 48.6 us | 20.3k to 20.6k | 1.000x |  | 0 B |
| C#, simple loop | 1 | 5.48k | 5.75 | 182 us | 5.48k to 5.49k | 0.266x |  | 0 B |
| C#, at its best | 1 | 20.6k | 21.6 | 48.6 us | 20.4k to 20.6k | 1.001x |  | 0 B |
| Rust | 1 | 20.6k | 21.6 | 48.6 us | 20.5k to 20.6k | 1.001x |  | 0 B |
| Go | 1 | 20.3k | 21.3 | 49.2 us | 20.3k to 20.4k | 0.988x |  | 0 B |
| JavaScript (Node.js) | 1 | 1.68k | 1.76 | 595 us | 1.68k to 1.68k | 0.082x |  | 24.5 B |
| Python | 1 | 10.7 | 0.0113 | 93.0 ms | 8.25 to 11.8 | 0.001x |  | n/a |
| Assembly (AsmSdk) | 4 | 74.7k | 78.3 | 53.6 us | 71.0k to 80.9k | 1.000x | 3.63x | 0 B |
| C#, simple loop | 4 | 21.2k | 22.2 | 189 us | 20.4k to 21.9k | 0.282x | 3.86x | 0 B |
| C#, at its best | 4 | 78.7k | 82.5 | 50.8 us | 76.9k to 82.2k | 1.052x | 3.82x | 0 B |
| Rust | 4 | 76.1k | 79.8 | 52.6 us | 70.0k to 80.4k | 1.025x | 3.70x | 0 B |
| Go | 4 | 78.9k | 82.7 | 50.7 us | 76.2k to 80.6k | 1.050x | 3.88x | 0.000391 B |
| JavaScript (Node.js) | 4 | 6.51k | 6.83 | 614 us | 6.23k to 6.68k | 0.087x | 3.88x | 24.6 B |
| Python | 4 | 33.3 | 0.0349 | 120 ms | 31.8 to 39.9 | 0.000x | 3.08x | n/a |

## 64 MiB

| Entry | Cores | Calls/s | GB/s | Per call | Lowest to highest round, calls/s | vs asm | x1 core | Allocated per call |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| Assembly (AsmSdk) | 1 | 208 | 13.9 | 4.82 ms | 192 to 225 | 1.000x |  | 0 B |
| C#, simple loop | 1 | 84.4 | 5.66 | 11.8 ms | 83.3 to 84.5 | 0.406x |  | 0 B |
| C#, at its best | 1 | 211 | 14.1 | 4.75 ms | 193 to 225 | 1.021x |  | 0 B |
| Rust | 1 | 205 | 13.7 | 4.89 ms | 189 to 216 | 0.983x |  | 0 B |
| Go | 1 | 208 | 13.9 | 4.81 ms | 199 to 215 | 1.012x |  | 0 B |
| JavaScript (Node.js) | 1 | 14.3 | 0.958 | 70.0 ms | 14.3 to 14.3 | 0.069x |  | 733 B |
| Python | 1 | 0.169 | 0.0113 | 5.92 s | 0.14 to 0.183 | 0.001x |  | n/a |
| Assembly (AsmSdk) | 4 | 603 | 40.5 | 6.63 ms | 571 to 630 | 1.000x | 2.98x | 0 B |
| C#, simple loop | 4 | 324 | 21.8 | 12.3 ms | 310 to 334 | 0.536x | 3.84x | 0 B |
| C#, at its best | 4 | 629 | 42.2 | 6.36 ms | 599 to 654 | 1.030x | 2.97x | 0 B |
| Rust | 4 | 591 | 39.7 | 6.77 ms | 547 to 619 | 0.965x | 2.89x | 0 B |
| Go | 4 | 630 | 42.3 | 6.35 ms | 597 to 646 | 1.027x | 3.03x | 0.0124 B |
| JavaScript (Node.js) | 4 | 55.2 | 3.70 | 72.5 ms | 53.3 to 56.1 | 0.092x | 3.86x | 374 B |
| Python | 4 | 0.559 | 0.0375 | 7.15 s | 0.473 to 0.596 | 0.001x | 3.38x | n/a |
