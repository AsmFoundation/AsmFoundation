//! The benchmarks' core guard: holds logical CPUs so that no other process's thread runs on them,
//! while taking as little as possible from the hyperthread beside each.
//!
//! Usage: `guard <cpu>[,<cpu>...]`. One thread per CPU pins itself there and waits in `tpause`
//! (the WAITPKG instruction, in its deeper C0.2 state) over and over, so Windows sees the CPU as
//! busy and schedules nothing else on it. A `tpause` wait leaves the core to its other
//! hyperthread; a guard spinning on `pause` instead took up to a third of a timed thread's speed
//! in the driver's trials of 2026-11-26 (benchmarks/README.md). Without WAITPKG it falls back to
//! `pause`, and says so.
//!
//! The guard runs until its standard input closes: the driver holds the other end, so the guard
//! ends with the driver even when the driver dies.

use std::arch::asm;
use std::io::Read;

#[link(name = "kernel32")]
unsafe extern "system" {
    fn GetCurrentThread() -> isize;
    fn SetThreadAffinityMask(thread: isize, mask: usize) -> usize;
}

/// True when the CPU has WAITPKG: CPUID.(EAX=7,ECX=0):ECX bit 5.
fn waitpkg() -> bool {
    core::arch::x86_64::__cpuid_count(7, 0).ecx & (1 << 5) != 0
}

/// Holds the calling thread's CPU forever: `tpause` in C0.2 with a deadline about 100
/// microseconds ahead (Windows also caps each wait), or `pause` without WAITPKG.
fn hold(use_tpause: bool) -> ! {
    loop {
        if use_tpause {
            // SAFETY: rdtsc only reads a counter.
            let deadline = unsafe { core::arch::x86_64::_rdtsc() } + 300_000;
            // SAFETY: tpause only waits, and the caller checked WAITPKG; ecx = 0 selects C0.2.
            unsafe {
                asm!("tpause ecx", in("ecx") 0u32, in("eax") deadline as u32, in("edx") (deadline >> 32) as u32, options(nomem, nostack));
            }
        } else {
            std::hint::spin_loop();
        }
    }
}

/// Starts one guard thread per CPU on the command line, then waits for standard input to close.
fn main() {
    let cpus: Vec<u32> = match std::env::args().nth(1).map(|s| s.split(',').map(str::parse).collect::<Result<Vec<u32>, _>>()) {
        Some(Ok(list)) if !list.is_empty() && list.iter().all(|&c| c < 64) => list,
        _ => {
            eprintln!("usage: guard <cpu>[,<cpu>...]  (logical CPUs of processor group 0)");
            std::process::exit(2);
        }
    };
    let use_tpause = waitpkg();
    eprintln!("guarding CPUs {cpus:?} with {}", if use_tpause { "tpause (C0.2)" } else { "pause: this CPU lacks WAITPKG" });
    for cpu in cpus {
        std::thread::spawn(move || {
            // SAFETY: pins the calling thread to one CPU of processor group 0 (checked above).
            if unsafe { SetThreadAffinityMask(GetCurrentThread(), 1usize << cpu) } == 0 {
                eprintln!("cannot pin a guard to CPU {cpu}");
                std::process::exit(1);
            }
            hold(use_tpause)
        });
    }
    // Block until the driver closes the pipe (or dies); whatever arrives is ignored.
    let mut sink = Vec::new();
    let _ = std::io::stdin().read_to_end(&mut sink);
}
