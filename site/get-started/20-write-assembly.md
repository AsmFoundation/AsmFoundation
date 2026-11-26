---
title: Write assembly in .NET
---

AsmSdk builds `.asmproj` projects with `dotnet build`, for Windows, Linux and macOS on x64 and ARM64, and for WebAssembly. You need the .NET SDK and nothing else.

### Name the SDK

```json title="global.json"
{ "msbuild-sdks": { "AsmFoundation.Sdk": "0.4.0" } }
```

**global.json** names the SDK. MSBuild fetches it from nuget.org, with the assembler and linker for your machine.

### Write the project

```xml title="Hello/Hello.asmproj"
<Project Sdk="AsmFoundation.Sdk">
  <PropertyGroup>
    <AssemblyName>hello</AssemblyName>
    <RuntimeIdentifiers>win-x64;linux-x64</RuntimeIdentifiers>
  </PropertyGroup>
</Project>
```

The project file reads like a `.csproj`, and its properties are .NET's own. Every `.asm` file next to it is a source. Add `osx-arm64` or `linux-arm64` and a `.s` file with the ARM64 code to build those too.

### Write the code

```nasm title="Hello/hello.asm"
PROTO int32_t hello_add(int32_t a, int32_t b)
FUNCTION hello_add, 2
    lea     eax, [ARG1 + ARG2]  ; rcx + rdx on Windows, rdi + rsi on Linux
    RETURN
ENDFUNCTION
```

`FUNCTION`, `ARG1` and `RETURN` come from the SDK's `asmsdk.inc`, which it puts in front of every source. They hide the difference between the Windows and Linux calling conventions, so one source builds for both. `PROTO` is the function's C prototype.

### Build both platforms

```sh
$ dotnet build Hello/Hello.asmproj
```

```output
Hello/bin/Debug/freestanding/win-x64/hello.dll
Hello/bin/Debug/freestanding/linux-x64/libhello.so
```

One build, one library per platform, and `hello.h` for C. A C# project that references it calls `Hello.Hello.HelloAdd(40, 2)`, which the SDK writes from the `PROTO` line; [the getting started guide](https://asmfoundation.github.io/AsmSdk/guide/getting-started.html) adds ARM64, a program and the C# caller.
