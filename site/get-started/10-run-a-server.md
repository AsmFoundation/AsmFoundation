---
title: Run a server
---

KvAsm, a gRPC key-value server in x86-64 assembly, released as a container image and a .NET client.

### Start the server

```sh
$ docker run --rm -p 50051:50051 ghcr.io/asmfoundation/kvasm:0.3.0
```

**KvAsm** runs on linux/amd64 and listens on port 50051. To build it yourself instead, `docker build --target runtime -t kvasm:0.3.0 .` in a clone runs every server test suite first; the image exists only if all of them pass.

### Add the client

```sh
$ dotnet add package KvAsm.Client
```

The .NET client, for .NET 8 or 10. Any language with gRPC tooling can use [kvasm.proto](https://github.com/AsmFoundation/KvAsm/blob/main/proto/kvasm/v1/kvasm.proto) and [the protocol's description](https://github.com/AsmFoundation/KvAsm/blob/main/docs/PROTOCOL.md) instead.

### Put a key and read it back

```csharp title="Program.cs"
using KvAsm.Client;

using var kv = new KvAsmClient(new Uri("http://localhost:50051"));
bool created = await kv.PutAsync("user:42", "Ada");   // true: new key
string? name = await kv.GetStringAsync("user:42");    // "Ada"
bool deleted = await kv.DeleteAsync("user:42");       // true
```

Large values stream with `UploadAsync` and `DownloadAsync`, up to 64 MiB by default, in chunks checked by CRC-32C at both ends.
