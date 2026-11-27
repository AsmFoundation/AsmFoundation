//! The CRC-32C kernel's timing protocol for the two native entries, `rust` and `asm`, which
//! include this file with `#[path]` so that both time their checksum the same way.
//!
//! Every entry of the kernel, in every language, follows the same protocol (the kernel's README
//! has it in full):
//!
//! - **Command line:** `--threads N --inputs DIR --sizes A,B --expect X,Y --window S,S --warmup S,S`.
//!   `DIR/<size>.bin` holds each input, `--expect` its CRC-32C in hex.
//! - **One cell per size:** N threads each copy the input into a buffer of their own, check their
//!   checksum, run the warm-up, then wait at a barrier; the main thread opens the window, sleeps
//!   through it and raises the stop flag; each thread counts its calls and times its own window,
//!   then checks its checksum again.
//! - **Output:** one JSON line per cell on stdout, after a header line; problems go to stderr with
//!   exit code 1, and nothing that failed a check is ever printed as a result.
//!
//! The driver (`benchmarks/run.cs`) confines the process to N performance cores, so "N cores" here
//! means N threads in a process that may use exactly N cores.

use std::alloc::{GlobalAlloc, Layout, System};
use std::fmt::Write as _;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Barrier};
use std::time::{Duration, Instant};

/// Every byte any thread of this process has asked the allocator for, since the start.
///
/// The cell reads it before opening the window and after the last thread has stopped, so the
/// difference is what the timed loops allocated (the protocol's "bytes allocated per call" after
/// dividing by the calls). Frees are not subtracted: the figure is allocation volume, as .NET's
/// and Go's counters report it, not live memory.
pub static ALLOCATED: AtomicU64 = AtomicU64::new(0);

/// The system allocator, counting every allocation into [`ALLOCATED`].
///
/// Each entry installs it with `#[global_allocator]`; it adds one relaxed atomic add per
/// allocation, which the timed loops never reach because they allocate nothing.
pub struct Counting;

// SAFETY: every method forwards to `System` with the caller's own arguments, so `Counting` keeps
// all of `System`'s guarantees; the counter is a side effect that cannot affect the memory.
unsafe impl GlobalAlloc for Counting {
    unsafe fn alloc(&self, layout: Layout) -> *mut u8 {
        ALLOCATED.fetch_add(layout.size() as u64, Ordering::Relaxed);
        // SAFETY: the caller's contract for `alloc` is passed on unchanged.
        unsafe { System.alloc(layout) }
    }

    unsafe fn alloc_zeroed(&self, layout: Layout) -> *mut u8 {
        ALLOCATED.fetch_add(layout.size() as u64, Ordering::Relaxed);
        // SAFETY: as above.
        unsafe { System.alloc_zeroed(layout) }
    }

    unsafe fn dealloc(&self, ptr: *mut u8, layout: Layout) {
        // SAFETY: `ptr` came from this allocator, which is `System` underneath.
        unsafe { System.dealloc(ptr, layout) }
    }

    unsafe fn realloc(&self, ptr: *mut u8, layout: Layout, new_size: usize) -> *mut u8 {
        // A reallocation can move the block: count the new size, as a fresh allocation would.
        ALLOCATED.fetch_add(new_size as u64, Ordering::Relaxed);
        // SAFETY: as above.
        unsafe { System.realloc(ptr, layout, new_size) }
    }
}

/// The parsed command line of the protocol.
pub struct Options {
    /// Worker threads, one per core the process may use.
    pub threads: usize,
    /// The folder holding `<size>.bin` for every size.
    pub inputs: String,
    /// The input sizes in bytes, in the order they are timed.
    pub sizes: Vec<usize>,
    /// The expected CRC-32C of each input.
    pub expect: Vec<u32>,
    /// Each size's timed window.
    pub window: Vec<Duration>,
    /// Each size's warm-up.
    pub warmup: Vec<Duration>,
    /// Options this harness does not know, as `(name, value)` pairs, for the entry itself.
    pub extra: Vec<(String, String)>,
}

/// Parses the protocol's command line.
///
/// # Errors
/// A message for stderr when an option is missing, malformed, or the lists differ in length.
pub fn parse(args: &[String]) -> Result<Options, String> {
    let mut threads = None;
    let (mut inputs, mut sizes, mut expect, mut window, mut warmup) = (None, None, None, None, None);
    let mut extra = Vec::new();
    let mut i = 0;
    while i < args.len() {
        // Every option takes exactly one value.
        let value = args.get(i + 1).ok_or_else(|| format!("{} needs a value", args[i]))?.clone();
        match args[i].as_str() {
            "--threads" => threads = Some(value.parse::<usize>().map_err(|e| format!("--threads: {e}"))?),
            "--inputs" => inputs = Some(value),
            "--sizes" => sizes = Some(list(&value, |s| s.parse::<usize>().ok())?),
            "--expect" => expect = Some(list(&value, |s| u32::from_str_radix(s.trim_start_matches("0x"), 16).ok())?),
            "--window" => window = Some(list(&value, |s| s.parse::<f64>().ok().map(Duration::from_secs_f64))?),
            "--warmup" => warmup = Some(list(&value, |s| s.parse::<f64>().ok().map(Duration::from_secs_f64))?),
            name => extra.push((name.to_string(), value)),
        }
        i += 2;
    }
    let options = Options {
        threads: threads.filter(|&n| n > 0).ok_or("--threads N (N > 0) is required")?,
        inputs: inputs.ok_or("--inputs DIR is required")?,
        sizes: sizes.ok_or("--sizes is required")?,
        expect: expect.ok_or("--expect is required")?,
        window: window.ok_or("--window is required")?,
        warmup: warmup.ok_or("--warmup is required")?,
        extra,
    };
    let n = options.sizes.len();
    if options.expect.len() != n || options.window.len() != n || options.warmup.len() != n {
        return Err("--sizes, --expect, --window and --warmup need one item per size".into());
    }
    Ok(options)
}

/// Parses a comma-separated list.
///
/// # Errors
/// The item that `item` rejected.
fn list<T>(text: &str, item: impl Fn(&str) -> Option<T>) -> Result<Vec<T>, String> {
    text.split(',').map(|s| item(s.trim()).ok_or_else(|| format!("bad list item '{s}' in '{text}'"))).collect()
}

/// One thread's share of a cell: its calls and its own measure of its window.
pub struct Stream {
    /// Checksums completed inside the window.
    pub calls: u64,
    /// From the barrier's release to the end of the thread's last call.
    pub seconds: f64,
}

/// Times one cell: `threads` threads run `checksum` over their own copy of `input` until the
/// window closes.
///
/// `checksum` must be a plain function: each call computes the CRC-32C of the whole slice. The
/// threads call it back to back on the same buffer, so independent checksums may overlap in the
/// CPU, as they would in a loop over many messages.
///
/// # Errors
/// A message when a thread's checksum differs from `expected`, before or after the window, or
/// when a thread panicked; no partial result is returned then.
pub fn run_cell(
    checksum: fn(&[u8]) -> u32,
    input: &[u8],
    expected: u32,
    threads: usize,
    window: Duration,
    warmup: Duration,
) -> Result<(Vec<Stream>, u64), String> {
    let barrier = Arc::new(Barrier::new(threads + 1));
    let stop = Arc::new(AtomicBool::new(false));
    // A batch takes microseconds at 64 bytes and is one call from 64 KiB up, so the stop flag is
    // read often enough to close the window within one batch, and rarely enough to cost nothing.
    let batch = (65_536 / input.len().max(1)).max(1) as u64;
    let shared: Arc<Vec<u8>> = Arc::new(input.to_vec());
    let mut handles = Vec::with_capacity(threads);
    for _ in 0..threads {
        let (barrier, stop, shared) = (barrier.clone(), stop.clone(), shared.clone());
        handles.push(std::thread::spawn(move || -> Result<Stream, String> {
            // The thread's own copy, allocated and written by the thread itself, so each core
            // checksums its own memory, as each request on a server would be its own buffer.
            let own: Vec<u8> = shared.as_slice().to_vec();
            let first = checksum(&own);
            if first != expected {
                // Still take part in the barrier, so the other threads and the main thread are
                // not left waiting; the error is reported after the window.
                barrier.wait();
                return Err(format!("checksum 0x{first:08x}, expected 0x{expected:08x}"));
            }
            let warm_end = Instant::now() + warmup;
            while Instant::now() < warm_end {
                for _ in 0..batch {
                    std::hint::black_box(checksum(std::hint::black_box(&own)));
                }
            }
            barrier.wait();
            let start = Instant::now();
            let mut calls = 0u64;
            let mut sink = 0u32;
            while !stop.load(Ordering::Relaxed) {
                for _ in 0..batch {
                    // black_box keeps the compiler from hoisting the checksum of an unchanged
                    // buffer out of the loop: every call must really run.
                    sink ^= checksum(std::hint::black_box(&own));
                }
                calls += batch;
            }
            let seconds = start.elapsed().as_secs_f64();
            std::hint::black_box(sink);
            let last = checksum(&own);
            if last != expected {
                return Err(format!("checksum after the window 0x{last:08x}, expected 0x{expected:08x}"));
            }
            Ok(Stream { calls, seconds })
        }));
    }
    // Everything this thread needs after the window is allocated before it, so the count below
    // holds only what the timed loops allocate.
    let mut streams = Vec::with_capacity(threads);
    let mut problem = None;
    // Read after the barrier: every thread has finished its setup (its own copy of the input is
    // the one allocation each makes), and the timed loops that run from here allocate nothing
    // unless the checksum itself does, which is what the count is for.
    barrier.wait();
    let before = ALLOCATED.load(Ordering::SeqCst);
    std::thread::sleep(window);
    stop.store(true, Ordering::Relaxed);
    for handle in handles {
        match handle.join() {
            Ok(Ok(stream)) => streams.push(stream),
            Ok(Err(message)) => problem = problem.or(Some(message)),
            Err(_) => problem = problem.or(Some("a thread panicked".to_string())),
        }
    }
    let allocated = ALLOCATED.load(Ordering::SeqCst) - before;
    match problem {
        Some(message) => Err(message),
        None => Ok((streams, allocated)),
    }
}

/// Runs every cell of the command line with `checksum` and prints the protocol's lines.
///
/// `header` is the entry's own description (its name, kernel and anything it detected), written
/// as JSON members without braces, for example `"entry":"rust","kernel":"3-way"`.
///
/// # Errors
/// A message for stderr when an input cannot be read, has the wrong size, or a cell fails.
pub fn run_all(options: &Options, header: &str, checksum: fn(&[u8]) -> u32) -> Result<(), String> {
    println!("{{\"type\":\"header\",{header},\"threads\":{},\"affinity\":\"{}\"}}", options.threads, affinity());
    for (i, &size) in options.sizes.iter().enumerate() {
        let path = std::path::Path::new(&options.inputs).join(format!("{size}.bin"));
        let input = std::fs::read(&path).map_err(|e| format!("{}: {e}", path.display()))?;
        if input.len() != size {
            return Err(format!("{} holds {} bytes, not {size}", path.display(), input.len()));
        }
        let (streams, allocated) = run_cell(checksum, &input, options.expect[i], options.threads, options.window[i], options.warmup[i])
            .map_err(|e| format!("{size} bytes on {} threads: {e}", options.threads))?;
        let mut line = format!("{{\"type\":\"cell\",\"size\":{size},\"threads\":{},\"streams\":[", options.threads);
        for (j, s) in streams.iter().enumerate() {
            let _ = write!(line, "{}{{\"calls\":{},\"seconds\":{:.6}}}", if j > 0 { "," } else { "" }, s.calls, s.seconds);
        }
        // The mask as of this cell, after its warm-up: the driver applies it right after starting
        // the process, so the header's reading can predate it, but a cell's never does.
        let _ = write!(line, "],\"allocatedBytes\":{allocated},\"affinity\":\"{}\"}}", affinity());
        println!("{line}");
    }
    Ok(())
}

/// The process's affinity mask in hex, so the driver can check it confined the process as asked;
/// "unknown" where the OS gives none.
fn affinity() -> String {
    #[cfg(windows)]
    {
        #[link(name = "kernel32")]
        unsafe extern "system" {
            fn GetCurrentProcess() -> isize;
            fn GetProcessAffinityMask(process: isize, process_mask: *mut usize, system_mask: *mut usize) -> i32;
        }
        let (mut process_mask, mut system_mask) = (0usize, 0usize);
        // SAFETY: both out-pointers are valid locals, and GetCurrentProcess's pseudo-handle needs
        // no closing.
        if unsafe { GetProcessAffinityMask(GetCurrentProcess(), &mut process_mask, &mut system_mask) } != 0 {
            return format!("0x{process_mask:x}");
        }
    }
    "unknown".to_string()
}
