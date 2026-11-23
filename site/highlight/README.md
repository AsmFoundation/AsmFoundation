# Code coloring

Every code block on the site is colored at build time, so the pages ship colored HTML and no highlighting
script. `site/build.cs` runs [highlight.js](https://highlightjs.org/) inside
[Jint](https://github.com/sebastienros/jint), a JavaScript interpreter for .NET, with no access to .NET or the
disk: the scripts receive code and return HTML.

| File | What it is |
|---|---|
| `vendor/highlight.min.js` | highlight.js 11.12.0, the official prebuilt "common" bundle: 36 languages, among them bash, C#, C++, Go, Java, JavaScript, JSON, Python, Rust and XML |
| `vendor/languages/x86asm.min.js` | highlight.js 11.12.0's Intel x86 grammar (NASM syntax), not in the common bundle |
| `vendor/LICENSE` | highlight.js's licence, BSD-3-Clause |
| `asmfoundation.js` | the foundation's own grammars: `nasm`, `cli`, `claude` |

## The foundation's grammars

- **`nasm`**: x86asm, plus what NASM and AsmSdk add. That means the directives x86asm lacks (`%ifdef`,
  `%ifndef`, `%macro` ...) and the macros of AsmSdk's `asmsdk.inc` (`FUNCTION`, `ENDFUNCTION`, `RETURN`,
  `ARG1` to `ARG6`).
- **`cli`**: one command line, colored as typed after a prompt: the program, its options, and numbers and
  versions. Bash's grammar leaves a line like `docker run --rm -p 50051:50051 ...` nearly uncolored. In a
  Get started block, every line that starts with `$ ` is colored this way.
- **`claude`**: a Claude Code slash command, then its repository or plugin arguments.

Zig, one of the benchmarks project's languages, has no highlight.js grammar. It needs one here before the
site shows Zig code: a block in an unknown language stops the build.

## Where the colors come from

The token classes are highlight.js's (`hljs-keyword`, `hljs-string` ...). `site/assets/site.css` colors
them with the palette of AsmSdk's documentation site, so code reads the same on both. Every color passes
WCAG AA (4.5:1) on the code panel, `#f7f9f9`.

## Updating highlight.js

1. Download the same three files of the new version from the official prebuilt release,
   `https://cdn.jsdelivr.net/gh/highlightjs/cdn-release@<version>/`: `build/highlight.min.js`,
   `build/languages/x86asm.min.js` and `LICENSE`.
2. Build the site and compare the colored pages.
3. Update the version and the fingerprints below.

The fingerprints of the files in use (SHA-256, as downloaded on 2026-11-23):

```text
8ab71eb09c51f501e5e25157d9cff100e46cc29bcbfc744d0b746d451fca7f53  vendor/highlight.min.js
364f595481fbb9c75fcb0907c534f904aa9cbf5ec27b36f70addf4d54c0fcfcf  vendor/languages/x86asm.min.js
5f289f36595e0ef6c53d9f4b4e51d7cc1efc5e2b3ba6130a875d177c54789eaf  vendor/LICENSE
```
