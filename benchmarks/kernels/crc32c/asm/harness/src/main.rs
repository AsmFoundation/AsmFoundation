//! The assembly entry's harness: loads AsmSdk's Crc32C library (`../lib`, built by AsmFoundation.Sdk)
//! and times `crc32c_append` with the kernel's protocol.
//!
//! The call is a plain native call through a function pointer, which is what any native program
//! pays to call a library: no marshalling, no GC transition, no foreign-function layer, so the
//! assembly pays for no call that another entry does not make. The protocol's code is shared with
//! the Rust entry (`common/harness.rs`).
//!
//! Extra option: `--library <path>`, the library to load (crc32c.dll or libcrc32c.so).

#[path = "../../../common/harness.rs"]
mod harness;

#[global_allocator]
static ALLOCATOR: harness::Counting = harness::Counting;

use std::sync::OnceLock;

/// `uint32_t crc32c_append(uint32_t crc, const uint8_t* data, size_t length)`: the checksum of
/// `data` continued from `crc` (0 for a new one), on the fastest path the CPU has.
type Append = unsafe extern "C" fn(u32, *const u8, usize) -> u32;

/// `int32_t crc32c_implementation(void)`: the path the dispatcher picked (`include/crc32c.inc`:
/// 1 portable, 2 one chain, 3 three-way), reported in the header.
type Implementation = unsafe extern "C" fn() -> i32;

/// The library's `crc32c_append`, resolved once before any thread starts.
static APPEND: OnceLock<Append> = OnceLock::new();

/// The protocol's checksum function: one call into the library.
fn crc32c(data: &[u8]) -> u32 {
    // OnceLock::get is a load and a branch the CPU predicts every time; main sets it first.
    let append = APPEND.get().expect("main loads the library before timing");
    // SAFETY: crc32c_append reads exactly data.len() bytes from data.as_ptr(), which the slice owns.
    unsafe { append(0, data.as_ptr(), data.len()) }
}

/// Loads `path` and resolves the two functions this harness calls.
///
/// # Errors
/// A message naming the library or the function that could not be found.
fn load(path: &str) -> Result<(Append, Implementation), String> {
    #[cfg(windows)]
    {
        #[link(name = "kernel32")]
        unsafe extern "system" {
            fn LoadLibraryW(name: *const u16) -> isize;
            fn GetProcAddress(module: isize, name: *const u8) -> *const core::ffi::c_void;
        }
        let wide: Vec<u16> = path.encode_utf16().chain(std::iter::once(0)).collect();
        // SAFETY: `wide` is a NUL-terminated UTF-16 path that outlives the call.
        let module = unsafe { LoadLibraryW(wide.as_ptr()) };
        if module == 0 {
            return Err(format!("cannot load {path}"));
        }
        // SAFETY: both names are NUL-terminated, and `module` is the library just loaded, which
        // stays loaded for the life of the process.
        let (append, implementation) =
            unsafe { (GetProcAddress(module, c"crc32c_append".as_ptr().cast()), GetProcAddress(module, c"crc32c_implementation".as_ptr().cast())) };
        if append.is_null() || implementation.is_null() {
            return Err(format!("{path} lacks crc32c_append or crc32c_implementation"));
        }
        // SAFETY: the exports have exactly these C signatures (the library's PROTO lines).
        return Ok(unsafe { (std::mem::transmute::<*const core::ffi::c_void, Append>(append), std::mem::transmute::<*const core::ffi::c_void, Implementation>(implementation)) });
    }
    #[cfg(not(windows))]
    {
        Err(format!("loading {path}: this harness is written for Windows, where the benchmark runs"))
    }
}

/// Loads the library, checks the check value, then runs the protocol.
fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let options = harness::parse(&args).unwrap_or_else(|e| {
        eprintln!("{e}");
        std::process::exit(2);
    });
    let library = match options.extra.as_slice() {
        [(name, value)] if name == "--library" => value.clone(),
        _ => {
            eprintln!("the asm entry needs --library <path> and no other extra option");
            std::process::exit(2);
        }
    };
    let (append, implementation) = load(&library).unwrap_or_else(|e| {
        eprintln!("{e}");
        std::process::exit(1);
    });
    APPEND.set(append).ok().expect("set once");
    // SAFETY: a call with no arguments that only reads the CPU's features.
    let path = match unsafe { implementation() } {
        3 => "three-way",
        2 => "one chain",
        1 => "portable",
        _ => "unknown",
    };
    if crc32c(b"123456789") != 0xE306_9283 {
        eprintln!("check value: 0x{:08x}, expected 0xe3069283", crc32c(b"123456789"));
        std::process::exit(1);
    }
    let header = format!("\"entry\":\"asm\",\"kernel\":\"AsmSdk's Crc32C library, crc32c_append, dispatcher path {path}\"");
    if let Err(e) = harness::run_all(&options, &header, crc32c) {
        eprintln!("{e}");
        std::process::exit(1);
    }
}
