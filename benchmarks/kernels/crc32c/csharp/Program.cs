using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Text;

namespace AsmFoundation.Benchmarks.Crc32C;

/// <summary>
/// The C# entry of the CRC-32C kernel, in two variants, timed with the kernel's protocol (the
/// kernel's README): <c>--variant loop</c>, the loop a C# developer writes first, and
/// <c>--variant best</c>, C# at its best, the assembly entry's own algorithm in intrinsics.
/// </summary>
/// <remarks>
/// <para>
/// The protocol: N threads (one per core the driver gives the process) each checksum their own
/// copy of the input; after a warm-up they wait at a barrier, the main thread opens the window,
/// sleeps through it and raises the stop flag, and each thread counts its calls and times its
/// own window. Each thread checks its checksum before and after; a wrong one fails the run.
/// </para>
/// <para>
/// "Bytes allocated per call" comes from <see cref="GC.GetAllocatedBytesForCurrentThread"/>
/// around each thread's window: what the checksum itself allocates on the managed heap.
/// </para>
/// </remarks>
internal static class Program
{
    /// <summary>Which of the two C# checksums a run times.</summary>
    private enum Variant
    {
        /// <summary><see cref="BitOperations.Crc32C(uint, ulong)"/> over 8-byte words: one dependency chain.</summary>
        Loop,

        /// <summary>The 3-way kernel: three <c>crc32</c> chains per block, merged with <c>pclmulqdq</c>.</summary>
        Best,
    }

    /// <summary>A thread's result is written once into this; its value only defeats dead-code elimination.</summary>
    private static long s_sink;

    /// <summary>Checks the CPU and the check value, then runs every cell of the command line.</summary>
    /// <param name="args">The protocol's options and <c>--variant loop|best</c>.</param>
    /// <returns>0 when every cell ran; 1 when a check failed (nothing is printed as a result then); 2 for a bad command line.</returns>
    private static int Main(string[] args)
    {
        if (!Options.TryParse(args, out Options? options, out string? usage))
        {
            Console.Error.WriteLine(usage);
            return 2;
        }

        if (!(Sse42.X64.IsSupported && Pclmulqdq.IsSupported))
        {
            Console.Error.WriteLine("this entry needs SSE4.2 and PCLMULQDQ");
            return 1;
        }

        // CRC-32C's standard check value, for both variants, before anything is timed.
        byte[] check = Encoding.ASCII.GetBytes("123456789");
        if (Checksum(Variant.Loop, check) != 0xE3069283 || Checksum(Variant.Best, check) != 0xE3069283)
        {
            Console.Error.WriteLine("check value of 123456789 is wrong");
            return 1;
        }

        string kernel = options.Variant == Variant.Loop
            ? "BitOperations.Crc32C over 8-byte words, one chain"
            : "3 crc32 chains per block merged with pclmulqdq, Sse42.X64 and Pclmulqdq intrinsics";
        Console.WriteLine(
            $"{{\"type\":\"header\",\"entry\":\"csharp\",\"variant\":\"{options.Variant.ToString().ToLowerInvariant()}\"," +
            $"\"kernel\":\"{kernel}\",\"runtime\":\"{System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}\"," +
            $"\"threads\":{options.Threads},\"affinity\":\"{Affinity()}\"}}");

        for (int i = 0; i < options.Sizes.Length; i++)
        {
            int size = options.Sizes[i];
            string path = Path.Combine(options.Inputs, $"{size}.bin");
            byte[] input = File.ReadAllBytes(path);
            if (input.Length != size)
            {
                Console.Error.WriteLine($"{path} holds {input.Length} bytes, not {size}");
                return 1;
            }

            if (RunCell(options.Variant, input, options.Expect[i], options.Threads, options.Window[i], options.Warmup[i]) is not { } cell)
            {
                return 1;
            }

            var line = new StringBuilder($"{{\"type\":\"cell\",\"size\":{size},\"threads\":{options.Threads},\"streams\":[");
            for (int t = 0; t < cell.Streams.Length; t++)
            {
                line.Append(t > 0 ? "," : "")
                    .Append(CultureInfo.InvariantCulture, $"{{\"calls\":{cell.Streams[t].Calls},\"seconds\":{cell.Streams[t].Seconds:F6}}}");
            }

            // The mask as of this cell, after its warm-up: the driver sets it right after starting
            // the process, so the header's reading can predate it, but a cell's never does.
            Console.WriteLine(line.Append(CultureInfo.InvariantCulture, $"],\"allocatedBytes\":{cell.AllocatedBytes},\"affinity\":\"{Affinity()}\"}}").ToString());
        }

        return 0;
    }

    /// <summary>The process's affinity mask in hex, so the driver can check it confined the process as asked.</summary>
    /// <returns>For example "0x154", or "unknown" where .NET cannot read it (it reads it on Windows and Linux).</returns>
    private static string Affinity()
    {
        if (!(OperatingSystem.IsWindows() || OperatingSystem.IsLinux()))
        {
            return "unknown";
        }

        // A fresh Process object: ProcessorAffinity is read when asked, and the driver changes it
        // after the process has started.
        using Process self = Process.GetCurrentProcess();
        return $"0x{(long)self.ProcessorAffinity:x}";
    }

    /// <summary>Times one cell: <paramref name="threads"/> threads checksum their own copies until the window closes.</summary>
    /// <param name="variant">Which checksum.</param>
    /// <param name="input">The input; each thread copies it.</param>
    /// <param name="expected">Its CRC-32C.</param>
    /// <param name="threads">How many threads.</param>
    /// <param name="window">The timed window.</param>
    /// <param name="warmup">Each thread's warm-up before the barrier.</param>
    /// <returns>The threads' counts and windows and the bytes they allocated, or null after writing the problem to stderr.</returns>
    /// <remarks>
    /// A thread that fails its check still takes part in the barrier, so nobody waits for it; the
    /// failure is reported after the window, and the cell is not printed.
    /// </remarks>
    private static CellResult? RunCell(Variant variant, byte[] input, uint expected, int threads, TimeSpan window, TimeSpan warmup)
    {
        using var barrier = new Barrier(threads + 1);
        var stop = new StopFlag();
        var streams = new StreamResult[threads];
        var allocated = new long[threads];
        var failures = new string?[threads];
        int batch = Math.Max(1, 65536 / Math.Max(1, input.Length));
        var workers = new Thread[threads];
        for (int t = 0; t < threads; t++)
        {
            int index = t;
            workers[t] = new Thread(() =>
            {
                // The thread's own copy, written by the thread itself.
                byte[] own = (byte[])input.Clone();
                if (Checksum(variant, own) != expected)
                {
                    failures[index] = $"checksum 0x{Checksum(variant, own):x8}, expected 0x{expected:x8}";
                    barrier.SignalAndWait();
                    return;
                }

                // Warm-up in two halves with a pause between, which hands the core to tiered
                // compilation's background compiler when the process is confined to one core.
                // The warm-up reaches the optimized, profile-guided code only because the driver
                // runs this entry with DOTNET_TC_CallCountingDelayMs=0: with .NET's default
                // promotion delay, a process confined to one core still ran the 64-byte checksum
                // unoptimized after 3 s of warm-up (9 million calls a second, against 242 million
                // after 5 s or with the delay off; measured 2026-11-26, see the kernel's README).
                var clock = Stopwatch.StartNew();
                while (clock.Elapsed < warmup / 2)
                {
                    Batch(variant, own, batch);
                }

                Thread.Sleep(100);
                clock.Restart();
                while (clock.Elapsed < warmup / 2)
                {
                    Batch(variant, own, batch);
                }

                barrier.SignalAndWait();
                long before = GC.GetAllocatedBytesForCurrentThread();
                long start = Stopwatch.GetTimestamp();
                long calls = 0;
                uint sink = 0;
                // The flag is read once per batch: microseconds at 64 bytes, one call from 64 KiB up.
                while (!stop.IsSet)
                {
                    sink ^= Batch(variant, own, batch);
                    calls += batch;
                }

                long end = Stopwatch.GetTimestamp();
                allocated[index] = GC.GetAllocatedBytesForCurrentThread() - before;
                Interlocked.Add(ref s_sink, sink);
                if (Checksum(variant, own) != expected)
                {
                    failures[index] = "checksum after the window is wrong";
                    return;
                }

                streams[index] = new StreamResult(calls, (end - start) / (double)Stopwatch.Frequency);
            })
            {
                IsBackground = true,
                Name = $"crc32c {index}",
            };
            workers[t].Start();
        }

        barrier.SignalAndWait();
        Thread.Sleep(window);
        stop.Set();
        foreach (Thread worker in workers)
        {
            worker.Join();
        }

        if (failures.FirstOrDefault(f => f is not null) is { } failure)
        {
            Console.Error.WriteLine($"{input.Length} bytes on {threads} threads: {failure}");
            return null;
        }

        return new CellResult(streams, allocated.Sum());
    }

    /// <summary>One checksum of either variant.</summary>
    /// <param name="variant">Which.</param>
    /// <param name="data">The bytes.</param>
    /// <returns>The CRC-32C.</returns>
    private static uint Checksum(Variant variant, ReadOnlySpan<byte> data) =>
        variant == Variant.Loop ? LoopCrc32C(data) : BestCrc32C(data);

    /// <summary>Runs <paramref name="count"/> checksums of the chosen variant back to back.</summary>
    /// <param name="variant">Which.</param>
    /// <param name="input">The bytes.</param>
    /// <param name="count">How many checksums.</param>
    /// <returns>The XOR of the checksums.</returns>
    /// <remarks>
    /// One branch per batch, then a direct loop: a delegate per call would add an indirect call to
    /// a few-nanosecond checksum.
    /// </remarks>
    private static uint Batch(Variant variant, byte[] input, int count) =>
        variant == Variant.Loop ? LoopBatch(input, count) : BestBatch(input, count);

    /// <summary><see cref="LoopCrc32C"/> <paramref name="count"/> times on the same input.</summary>
    /// <param name="input">The bytes.</param>
    /// <param name="count">How many checksums.</param>
    /// <returns>The XOR of the checksums.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static uint LoopBatch(byte[] input, int count)
    {
        uint acc = 0;
        for (int i = 0; i < count; i++)
        {
            acc ^= LoopCrc32C(input);
        }

        return acc;
    }

    /// <summary><see cref="BestCrc32C"/> <paramref name="count"/> times on the same input.</summary>
    /// <param name="input">The bytes.</param>
    /// <param name="count">How many checksums.</param>
    /// <returns>The XOR of the checksums.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static uint BestBatch(byte[] input, int count)
    {
        uint acc = 0;
        for (int i = 0; i < count; i++)
        {
            acc ^= BestCrc32C(input);
        }

        return acc;
    }

    /// <summary>The loop a C# developer writes first: <see cref="BitOperations.Crc32C(uint, ulong)"/> over 8-byte words, then bytes.</summary>
    /// <param name="data">The bytes.</param>
    /// <returns>The CRC-32C.</returns>
    /// <remarks>
    /// The same code as AsmSdk's sample (<c>Crc32CBenchmarks.ManagedCrc32C</c>). The JIT zero-extends
    /// the <c>uint</c> register before every 64-bit <c>crc32</c>, inside the dependency chain:
    /// 4 cycles per 8 bytes instead of 3.
    /// </remarks>
    private static uint LoopCrc32C(ReadOnlySpan<byte> data)
    {
        uint crc = ~0u;
        while (data.Length >= 8)
        {
            crc = BitOperations.Crc32C(crc, BinaryPrimitives.ReadUInt64LittleEndian(data));
            data = data[8..];
        }

        foreach (byte b in data)
        {
            crc = BitOperations.Crc32C(crc, b);
        }

        return ~crc;
    }

    /// <summary>The merge constants of 512-byte lanes: x^(8*2*512-33) in the low quadword, x^(8*512-33) in the high one.</summary>
    private static readonly Vector128<ulong> s_k512 = Vector128.Create(XPow(8 * 2 * 512 - 33), XPow(8 * 512 - 33));

    /// <summary>The merge constants of 64-byte lanes.</summary>
    private static readonly Vector128<ulong> s_k64 = Vector128.Create(XPow(8 * 2 * 64 - 33), XPow(8 * 64 - 33));

    /// <summary>
    /// C# at its best: the assembly's 3-way kernel. 1,536-byte blocks, then 192-byte blocks, each
    /// as three <c>crc32</c> chains merged with one carry-less multiply per lane; then 8 bytes and
    /// single bytes at a time.
    /// </summary>
    /// <param name="data">The bytes.</param>
    /// <returns>The CRC-32C.</returns>
    /// <remarks>
    /// <see cref="Sse42.X64.Crc32(ulong, ulong)"/> keeps the register in a <c>ulong</c>, so no
    /// zero-extension sits in the chain; reads are unaligned 8-byte loads through a pinned pointer.
    /// </remarks>
    private static unsafe uint BestCrc32C(ReadOnlySpan<byte> data)
    {
        fixed (byte* start = data)
        {
            byte* p = start;
            nuint len = (nuint)data.Length;
            ulong crc = 0xFFFFFFFF;
            ThreeWay(ref crc, ref p, ref len, 512, s_k512);
            ThreeWay(ref crc, ref p, ref len, 64, s_k64);
            for (; len >= 8; len -= 8, p += 8)
            {
                crc = Sse42.X64.Crc32(crc, Unsafe.ReadUnaligned<ulong>(p));
            }

            uint c = (uint)crc;
            for (; len > 0; len--, p++)
            {
                c = Sse42.Crc32(c, *p);
            }

            return ~c;
        }
    }

    /// <summary>Runs every whole block of <c>3 * lane</c> bytes left, advancing <paramref name="p"/> and reducing <paramref name="len"/>.</summary>
    /// <param name="crc">The raw CRC register, continued.</param>
    /// <param name="p">The data; advanced past the blocks.</param>
    /// <param name="len">Bytes left; reduced by the blocks.</param>
    /// <param name="lane">Bytes per lane (a multiple of 8, at least 16); a constant at each call, so the JIT folds it.</param>
    /// <param name="k">The lane's merge constants: two lanes' shift low, one lane's high.</param>
    /// <remarks>
    /// The block's CRC is shift(c0, 2 lanes) ^ shift(c1, 1 lane) ^ c2, and
    /// <c>crc32_u64(0, clmul(c, x^(8n-33))) = shift(c, n)</c>; both products are folded into lane
    /// 2's last 8 bytes so one <c>crc32</c> finishes the block (the assembly's derivation).
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void ThreeWay(ref ulong crc, ref byte* p, ref nuint len, nuint lane, Vector128<ulong> k)
    {
        while (len >= 3 * lane)
        {
            ulong c0 = crc, c1 = 0, c2 = 0;
            // All but each lane's last 8 bytes: three chains that do not wait for each other.
            for (nuint off = 0; off < lane - 8; off += 8)
            {
                c0 = Sse42.X64.Crc32(c0, Unsafe.ReadUnaligned<ulong>(p + off));
                c1 = Sse42.X64.Crc32(c1, Unsafe.ReadUnaligned<ulong>(p + lane + off));
                c2 = Sse42.X64.Crc32(c2, Unsafe.ReadUnaligned<ulong>(p + 2 * lane + off));
            }

            c0 = Sse42.X64.Crc32(c0, Unsafe.ReadUnaligned<ulong>(p + lane - 8));
            c1 = Sse42.X64.Crc32(c1, Unsafe.ReadUnaligned<ulong>(p + 2 * lane - 8));
            Vector128<ulong> a = Pclmulqdq.CarrylessMultiply(Vector128.CreateScalar(c0), k, 0x00);
            Vector128<ulong> b = Pclmulqdq.CarrylessMultiply(Vector128.CreateScalar(c1), k, 0x10);
            ulong folded = (a ^ b).ToScalar() ^ Unsafe.ReadUnaligned<ulong>(p + 3 * lane - 8);
            crc = Sse42.X64.Crc32(c2, folded);
            p += 3 * lane;
            len -= 3 * lane;
        }
    }

    /// <summary>x^n mod P, bit-reflected (bit 31 holds x^0): n steps of the bitwise CRC, as the assembly's X_POW.</summary>
    /// <param name="n">The exponent.</param>
    /// <returns>The polynomial, zero-extended.</returns>
    private static ulong XPow(int n)
    {
        uint r = 0x80000000;
        for (int i = 0; i < n; i++)
        {
            // Multiply by x: shift right, and fold the polynomial back in when x^31 falls off.
            r = (r >> 1) ^ (0x82F63B78u & (uint)-(int)(r & 1));
        }

        return r;
    }

    /// <summary>One thread's count and window.</summary>
    /// <param name="Calls">Checksums inside the window.</param>
    /// <param name="Seconds">From the barrier's release to the end of the thread's last call.</param>
    private readonly record struct StreamResult(long Calls, double Seconds);

    /// <summary>A cell: its threads and what they allocated together.</summary>
    /// <param name="Streams">Each thread's count and window.</param>
    /// <param name="AllocatedBytes">Managed bytes the threads allocated inside their windows.</param>
    private sealed record CellResult(StreamResult[] Streams, long AllocatedBytes);

    /// <summary>The stop flag the main thread raises when the window closes.</summary>
    private sealed class StopFlag
    {
        /// <summary>0 until <see cref="Set"/>; read with a volatile load between batches.</summary>
        private int _value;

        /// <summary>True once the window has closed.</summary>
        public bool IsSet => Volatile.Read(ref _value) != 0;

        /// <summary>Closes the window.</summary>
        public void Set() => Volatile.Write(ref _value, 1);
    }

    /// <summary>The protocol's command line.</summary>
    /// <param name="Threads">Worker threads.</param>
    /// <param name="Inputs">The folder holding each size's input.</param>
    /// <param name="Sizes">The sizes, in order.</param>
    /// <param name="Expect">Each input's CRC-32C.</param>
    /// <param name="Window">Each size's window.</param>
    /// <param name="Warmup">Each size's warm-up.</param>
    /// <param name="Variant">The checksum to time.</param>
    private sealed record Options(int Threads, string Inputs, int[] Sizes, uint[] Expect, TimeSpan[] Window, TimeSpan[] Warmup, Variant Variant)
    {
        /// <summary>Parses the protocol's options and <c>--variant</c>.</summary>
        /// <param name="args">The command line.</param>
        /// <param name="options">The options, when valid.</param>
        /// <param name="usage">What is wrong, when not.</param>
        /// <returns>True when the command line is valid.</returns>
        public static bool TryParse(
            string[] args,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Options? options,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? usage)
        {
            options = null;
            usage = null;
            var values = new Dictionary<string, string>();
            for (int i = 0; i + 1 < args.Length; i += 2)
            {
                values[args[i]] = args[i + 1];
            }

            try
            {
                int[] sizes = values["--sizes"].Split(',').Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
                uint[] expect = values["--expect"].Split(',').Select(s => uint.Parse(s.Replace("0x", ""), NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();
                TimeSpan[] window = values["--window"].Split(',').Select(s => TimeSpan.FromSeconds(double.Parse(s, CultureInfo.InvariantCulture))).ToArray();
                TimeSpan[] warmup = values["--warmup"].Split(',').Select(s => TimeSpan.FromSeconds(double.Parse(s, CultureInfo.InvariantCulture))).ToArray();
                Variant variant = values["--variant"] switch
                {
                    "loop" => Variant.Loop,
                    "best" => Variant.Best,
                    string other => throw new FormatException($"--variant {other}: loop or best"),
                };
                if (expect.Length != sizes.Length || window.Length != sizes.Length || warmup.Length != sizes.Length)
                {
                    throw new FormatException("--sizes, --expect, --window and --warmup need one item per size");
                }

                options = new Options(int.Parse(values["--threads"], CultureInfo.InvariantCulture), values["--inputs"], sizes, expect, window, warmup, variant);
                return options.Threads > 0 ? true : throw new FormatException("--threads must be above 0");
            }
            catch (Exception e) when (e is KeyNotFoundException or FormatException or OverflowException)
            {
                usage = $"usage: --threads N --inputs DIR --sizes A,B --expect X,Y --window S,S --warmup S,S --variant loop|best ({e.Message})";
                return false;
            }
        }
    }
}
