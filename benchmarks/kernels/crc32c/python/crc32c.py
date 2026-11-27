"""The Python entry of the CRC-32C kernel: Python at its best, timed with the kernel's protocol.

Python's standard library has no CRC-32C (zlib.crc32 and binascii.crc32 are the IEEE CRC-32), and
the rules allow no third-party native code, so the fastest correct Python is a table method in
pure Python. Measured on the benchmark machine before choosing (2026-11-26, CPython 3.12): 16-bit
tables over 64-bit words 8.5 MB/s at 1 MiB and 11.0 MB/s at 64 B; slicing-by-8 over words 7.2
and 6.4; byte at a time 4.9 and 4.9. This entry uses the 16-bit tables: four 65,536-entry tables,
four lookups per 8 bytes.

Several cores need several processes: the GIL lets one thread at a time run Python code, so each
"core" is a process of its own (multiprocessing, spawn), as Python's own documentation
recommends for CPU-bound work. Each worker reads the input into memory of its own, checks its
checksum, warms up, waits at a barrier, runs until the stop event, and checks again. Windows does
not pass a process's priority or power-throttling setting to its children, so each worker sets
both on itself; it inherits the affinity mask, and reports it so the driver can check.

"Bytes allocated per call" is not counted: CPython keeps no running total of allocations
(tracemalloc tracks live memory and its peak only), so the entry reports null. Every arithmetic
result above 256 is a new int object here.

Usage: python crc32c.py --threads N --inputs DIR --sizes A,B --expect X,Y --window S,S --warmup S,S
"""

import json
import multiprocessing
import os
import sys
import time

POLY = 0x82F63B78
"""CRC-32C's polynomial, bit-reflected."""


def _byte_tables():
    """Builds the eight byte tables of slicing-by-8.

    Returns:
        A list of eight 256-entry lists: T[k][b] is the CRC register after byte b followed by k
        zero bytes, so T[0] is the classic byte-at-a-time table.
    """
    t0 = []
    for i in range(256):
        c = i
        for _ in range(8):
            c = (c >> 1) ^ (POLY if c & 1 else 0)
        t0.append(c)
    tables = [t0]
    for k in range(1, 8):
        tables.append([(tables[k - 1][i] >> 8) ^ t0[tables[k - 1][i] & 255] for i in range(256)])
    return tables


T = _byte_tables()
"""The byte tables; T[0] also finishes the bytes after the last whole 8-byte word."""


def _symbol_table(k):
    """Builds the table of a 16-bit symbol that has k more symbols after it in an 8-byte word.

    The symbol's low byte comes first in memory, so it is followed by 2k + 1 bytes and its high
    byte by 2k: the table combines those two byte tables, and one lookup replaces two.

    Args:
        k: 0 for a word's last 16-bit symbol, up to 3 for its first.

    Returns:
        A 65,536-entry list of CRC registers.
    """
    low, high = T[2 * k + 1], T[2 * k]
    return [low[v & 255] ^ high[v >> 8] for v in range(65536)]


W = [_symbol_table(k) for k in range(4)]
"""The 16-bit tables, W[3] for a word's first symbol down to W[0] for its last."""


def crc32c(data):
    """Computes the CRC-32C of data: four 16-bit lookups per 8-byte word, then single bytes.

    Args:
        data: the bytes (any object a memoryview can cast to 8-byte words).

    Returns:
        The checksum, an int below 2**32.
    """
    w3, w2, w1, w0 = W[3], W[2], W[1], W[0]
    t0 = T[0]
    crc = 0xFFFFFFFF
    n8 = len(data) & ~7
    # 'Q' reads native 8-byte words: little-endian on x86-64, the order the tables assume.
    for word in memoryview(data)[:n8].cast("Q"):
        x = crc ^ (word & 0xFFFFFFFF)
        h = word >> 32
        crc = w3[x & 0xFFFF] ^ w2[x >> 16] ^ w1[h & 0xFFFF] ^ w0[h >> 16]
    for b in data[n8:]:
        crc = t0[(crc ^ b) & 255] ^ (crc >> 8)
    return crc ^ 0xFFFFFFFF


def _own_process_settings():
    """Turns power throttling off and raises this process to High priority, as the driver does
    for the main process; Windows passes neither on to a child process.

    Returns:
        The process's affinity mask in hex, or "unknown" off Windows or when Windows refuses.
    """
    if os.name != "nt":
        return "unknown"
    import ctypes
    from ctypes import wintypes

    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    # Declared types: the pseudo-handle of GetCurrentProcess is all ones, which ctypes would
    # otherwise try to pass as a 32-bit int.
    kernel32.GetCurrentProcess.restype = wintypes.HANDLE
    kernel32.SetProcessInformation.argtypes = [wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD]
    kernel32.SetProcessInformation.restype = wintypes.BOOL
    kernel32.SetPriorityClass.argtypes = [wintypes.HANDLE, wintypes.DWORD]
    kernel32.SetPriorityClass.restype = wintypes.BOOL
    kernel32.GetProcessAffinityMask.argtypes = [wintypes.HANDLE, ctypes.POINTER(ctypes.c_size_t), ctypes.POINTER(ctypes.c_size_t)]
    kernel32.GetProcessAffinityMask.restype = wintypes.BOOL
    process = kernel32.GetCurrentProcess()

    class _State(ctypes.Structure):
        """PROCESS_POWER_THROTTLING_STATE, version 1."""

        _fields_ = [("Version", wintypes.ULONG), ("ControlMask", wintypes.ULONG), ("StateMask", wintypes.ULONG)]

    # ProcessPowerThrottling = 4; control the execution-speed knob (1) and set it to 0: never throttle.
    state = _State(1, 1, 0)
    kernel32.SetProcessInformation(process, 4, ctypes.byref(state), ctypes.sizeof(state))
    kernel32.SetPriorityClass(process, 0x00000080)  # HIGH_PRIORITY_CLASS
    process_mask, system_mask = ctypes.c_size_t(), ctypes.c_size_t()
    if kernel32.GetProcessAffinityMask(process, ctypes.byref(process_mask), ctypes.byref(system_mask)):
        return hex(process_mask.value)
    return "unknown"


def _worker(index, path, expected, warmup, batch, barrier, stop, results):
    """One "core": its own copy of the input, the check, the warm-up, the barrier, the timed loop.

    A worker that fails its first check still waits at the barrier, so the others and the main
    process are not left waiting; the failure travels in the result.

    Args:
        index: the worker's number, returned with its result so results can be ordered.
        path: the input file, read into memory of the worker's own.
        expected: the input's CRC-32C, checked before the warm-up and after the window.
        warmup: seconds of checksums before the barrier.
        batch: checksums between two reads of the stop event.
        barrier: the multiprocessing.Barrier the main process opens the window with.
        stop: the multiprocessing.Event that closes the window.
        results: the multiprocessing.Queue the result goes to, as
            (index, calls, seconds, affinity, error), error being None unless a check failed.
    """
    affinity = _own_process_settings()
    with open(path, "rb") as f:
        own = f.read()
    if crc32c(own) != expected:
        barrier.wait()
        results.put((index, 0, 0.0, affinity, f"checksum {crc32c(own):#010x}, expected {expected:#010x}"))
        return
    end = time.perf_counter() + warmup
    while time.perf_counter() < end:
        for _ in range(batch):
            crc32c(own)
    barrier.wait()
    began = time.perf_counter()
    calls = 0
    acc = 0
    # The event is read once per batch; a batch is milliseconds at 64 bytes, one call above.
    while not stop.is_set():
        for _ in range(batch):
            acc ^= crc32c(own)
        calls += batch
    seconds = time.perf_counter() - began
    error = None if crc32c(own) == expected else "checksum after the window is wrong"
    results.put((index, calls, seconds, affinity, error))


def _parse(args):
    """Parses the protocol's options.

    Args:
        args: the command line after the script name.

    Returns:
        A dict with threads, inputs, sizes, expect, window and warmup.

    Raises:
        ValueError: an option is missing or malformed, or the lists differ in length.
    """
    values = dict(zip(args[0::2], args[1::2]))

    def need(name):
        """Returns the value of the required option name; raises ValueError when it is missing."""
        if name not in values:
            raise ValueError(f"{name} is required")
        return values[name]

    o = {
        "threads": int(need("--threads")),
        "inputs": need("--inputs"),
        "sizes": [int(s) for s in need("--sizes").split(",")],
        "expect": [int(s, 16) for s in need("--expect").split(",")],
        "window": [float(s) for s in need("--window").split(",")],
        "warmup": [float(s) for s in need("--warmup").split(",")],
    }
    if o["threads"] < 1:
        raise ValueError("--threads must be above 0")
    if not len(o["expect"]) == len(o["window"]) == len(o["warmup"]) == len(o["sizes"]):
        raise ValueError("--sizes, --expect, --window and --warmup need one item per size")
    return o


def main():
    """Checks the check value, then runs every cell of the command line.

    Returns:
        0 when every cell ran, 1 when a check failed (nothing is printed as a result then), 2 for
        a bad command line.
    """
    try:
        o = _parse(sys.argv[1:])
    except ValueError as e:
        print(e, file=sys.stderr)
        return 2
    if crc32c(b"123456789") != 0xE3069283:
        print("check value of 123456789 is wrong", file=sys.stderr)
        return 1
    ctx = multiprocessing.get_context("spawn")
    print(json.dumps({
        "type": "header",
        "entry": "python",
        "kernel": "16-bit tables over 64-bit words, pure Python",
        "runtime": f"CPython {sys.version.split()[0]}",
        "threads": o["threads"],
        "affinity": _own_process_settings(),
    }), flush=True)
    for i, size in enumerate(o["sizes"]):
        path = os.path.join(o["inputs"], f"{size}.bin")
        if os.path.getsize(path) != size:
            print(f"{path} does not hold {size} bytes", file=sys.stderr)
            return 1
        barrier = ctx.Barrier(o["threads"] + 1)
        stop = ctx.Event()
        results = ctx.Queue()
        batch = max(1, 65536 // size)
        workers = [
            ctx.Process(target=_worker, args=(t, path, o["expect"][i], o["warmup"][i], batch, barrier, stop, results))
            for t in range(o["threads"])
        ]
        for w in workers:
            w.start()
        barrier.wait()
        time.sleep(o["window"][i])
        stop.set()
        streams = sorted(results.get() for _ in workers)
        for w in workers:
            w.join()
        failed = [s for s in streams if s[4]]
        if failed:
            print(f"{size} bytes on {o['threads']} processes: {failed[0][4]}", file=sys.stderr)
            return 1
        print(json.dumps({
            "type": "cell",
            "size": size,
            "threads": o["threads"],
            "streams": [{"calls": s[1], "seconds": round(s[2], 6)} for s in streams],
            "allocatedBytes": None,
            "workerAffinity": [s[3] for s in streams],
        }), flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
