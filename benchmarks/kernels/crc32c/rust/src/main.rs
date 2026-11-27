//! CRC-32C in Rust at its best: the assembly entry's own algorithm, written with `std::arch`
//! intrinsics. Three `crc32` chains run over the three lanes of each block and are merged with
//! one carry-less multiply per lane (`pclmulqdq`), on 1,536-byte blocks, then 192-byte blocks,
//! then 8 bytes and single bytes at a time. Only the standard library is used.
//!
//! The process follows the kernel's timing protocol (see `common/harness.rs` and the kernel's
//! README); `.cargo/config.toml` builds it for the machine's own CPU (`-C target-cpu=native`).

#[path = "../../common/harness.rs"]
mod harness;

#[global_allocator]
static ALLOCATOR: harness::Counting = harness::Counting;

use std::arch::x86_64::{
    __m128i, _mm_clmulepi64_si128, _mm_crc32_u64, _mm_crc32_u8, _mm_cvtsi128_si64, _mm_cvtsi64_si128, _mm_set_epi64x,
    _mm_xor_si128,
};

/// CRC-32C's polynomial, bit-reflected (Castagnoli, 0x1EDC6F41 written backwards).
const POLY: u32 = 0x82F6_3B78;

/// x^n mod P, bit-reflected (bit 31 holds x^0): n steps of the bitwise CRC from x^0.
///
/// The same computation as the assembly's `X_POW` macro, here evaluated by the compiler, so the
/// merge constants are derived rather than copied.
const fn x_pow(n: u32) -> u64 {
    let mut r: u32 = 0x8000_0000;
    let mut i = 0;
    while i < n {
        // Multiplying a reflected value by x: shift right, and fold P back in when x^31 falls off.
        r = (r >> 1) ^ (POLY & 0u32.wrapping_sub(r & 1));
        i += 1;
    }
    r as u64
}

/// The merge constants of a lane of `lane` bytes: x^(8 * 2 lane - 33) and x^(8 * lane - 33).
///
/// A carry-less product of two reflected values carries one extra factor x and `crc32` over 8
/// bytes multiplies by x^32, hence the 33: `crc32_u64(0, clmul(c, K)) = c * x^(8n) mod P`.
const fn lane_constants(lane: u32) -> (u64, u64) {
    (x_pow(8 * 2 * lane - 33), x_pow(8 * lane - 33))
}

/// 1,536-byte blocks first: lane 0's chain also waits for each merge, so long lanes pay it rarely.
const BIG: (u64, u64) = lane_constants(512);
/// Then 192-byte blocks, for what is left.
const SMALL: (u64, u64) = lane_constants(64);

/// Reads 8 bytes, little-endian, from any alignment.
///
/// # Safety
/// `p` must point at 8 readable bytes.
#[inline(always)]
unsafe fn rd(p: *const u8) -> u64 {
    // SAFETY: the caller guarantees 8 readable bytes; read_unaligned imposes no alignment.
    unsafe { core::ptr::read_unaligned(p.cast::<u64>()) }
}

/// Runs every whole block of `3 * lane` bytes left, returning the raw CRC register and what is left.
///
/// # Safety
/// `p` must point at `len` readable bytes, and the CPU must have SSE4.2 and PCLMULQDQ.
#[target_feature(enable = "sse4.2,pclmulqdq")]
#[inline]
unsafe fn three_way(mut crc: u64, mut p: *const u8, mut len: usize, lane: usize, k: (u64, u64)) -> (u64, *const u8, usize) {
    // The low quadword multiplies lane 0's register, the high one lane 1's (pclmulqdq's 0x00 and
    // 0x10), exactly as the assembly lays out its constant.
    let kk: __m128i = _mm_set_epi64x(k.1 as i64, k.0 as i64);
    while len >= 3 * lane {
        let (mut c1, mut c2) = (0u64, 0u64);
        let mut off = 0;
        // All but each lane's last 8 bytes: three independent chains, one crc32 per cycle.
        while off < lane - 8 {
            // SAFETY: off + 2 * lane + 8 <= 3 * lane <= len.
            unsafe {
                crc = _mm_crc32_u64(crc, rd(p.add(off)));
                c1 = _mm_crc32_u64(c1, rd(p.add(lane + off)));
                c2 = _mm_crc32_u64(c2, rd(p.add(2 * lane + off)));
            }
            off += 8;
        }
        // SAFETY: the last 8 bytes of lanes 0, 1 and 2 lie inside the block.
        unsafe {
            crc = _mm_crc32_u64(crc, rd(p.add(lane - 8)));
            c1 = _mm_crc32_u64(c1, rd(p.add(2 * lane - 8)));
            // Shift lane 0 by two lanes and lane 1 by one, fold both into lane 2's last 8 bytes,
            // and one crc32 finishes the block.
            let a = _mm_clmulepi64_si128::<0x00>(_mm_cvtsi64_si128(crc as i64), kk);
            let b = _mm_clmulepi64_si128::<0x10>(_mm_cvtsi64_si128(c1 as i64), kk);
            let folded = (_mm_cvtsi128_si64(_mm_xor_si128(a, b)) as u64) ^ rd(p.add(3 * lane - 8));
            crc = _mm_crc32_u64(c2, folded);
            p = p.add(3 * lane);
        }
        len -= 3 * lane;
    }
    (crc, p, len)
}

/// CRC-32C of `data` on the CPU's crc32 and pclmulqdq instructions.
///
/// # Safety
/// The CPU must have SSE4.2 and PCLMULQDQ (main checks before any call).
#[target_feature(enable = "sse4.2,pclmulqdq")]
unsafe fn crc32c_hw(data: &[u8]) -> u32 {
    // SAFETY: the slice's pointer and length describe readable bytes, and the caller vouches for
    // the CPU features.
    unsafe {
        let (crc, p, len) = three_way(0xFFFF_FFFF, data.as_ptr(), data.len(), 512, BIG);
        let (mut crc, mut p, len) = three_way(crc, p, len, 64, SMALL);
        // The single chain for whole quadwords, then single bytes.
        for _ in 0..len / 8 {
            crc = _mm_crc32_u64(crc, rd(p));
            p = p.add(8);
        }
        let mut c = crc as u32;
        for i in 0..len % 8 {
            c = _mm_crc32_u8(c, *p.add(i));
        }
        !c
    }
}

/// The protocol's checksum function.
fn crc32c(data: &[u8]) -> u32 {
    // SAFETY: main stops before any call on a CPU without SSE4.2 and PCLMULQDQ.
    unsafe { crc32c_hw(data) }
}

/// Checks the CPU and the check value, then runs the protocol.
fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let options = harness::parse(&args).unwrap_or_else(|e| {
        eprintln!("{e}");
        std::process::exit(2);
    });
    // This entry takes the protocol's options only; anything else is a mistake in the command line.
    if let Some((name, _)) = options.extra.first() {
        eprintln!("unknown option {name}");
        std::process::exit(2);
    }
    if !(is_x86_feature_detected!("sse4.2") && is_x86_feature_detected!("pclmulqdq")) {
        eprintln!("this entry needs SSE4.2 and PCLMULQDQ");
        std::process::exit(1);
    }
    // CRC-32C's standard check value, before anything is timed.
    if crc32c(b"123456789") != 0xE306_9283 {
        eprintln!("check value: 0x{:08x}, expected 0xe3069283", crc32c(b"123456789"));
        std::process::exit(1);
    }
    let header = "\"entry\":\"rust\",\"kernel\":\"3 crc32 chains per block merged with pclmulqdq, std::arch intrinsics, target-cpu=native\"";
    if let Err(e) = harness::run_all(&options, header, crc32c) {
        eprintln!("{e}");
        std::process::exit(1);
    }
}
