// The benchmarks project's driver (a .NET file-based app): builds a kernel's entries, checks them,
// and times them against each other in paired rounds on one core and on several.
//
//   dotnet run benchmarks/run.cs -- crc32c [--rounds 12] [--cores 1,4] [--cpus 2,4,6,8] [--guards 3,5,7,9]
//        [--entries asm,csharp-loop,csharp-best,rust,go,javascript,python] [--go <go.exe>] [--out <folder>] [--quick]
//
// What it does, in order (the kernel's README has the protocol every entry follows):
//   1. builds every entry: the assembly library with AsmFoundation.Sdk (dotnet build), the Rust
//      programs with cargo, the C# entry with dotnet, the Go entry with go (JavaScript and Python
//      run from source);
//   2. writes the inputs (splitmix64 bytes seeded with the size) and computes each one's CRC-32C
//      with its own reference, independent of every entry, checked against the standard check value;
//   3. runs every entry once with short windows: a wrong checksum stops everything before timing;
//   4. holds the other hyperthread of every timed core with the guard (tools/guard: tpause waits), so
//      no other process's thread shares a timed core, and samples the timed cores' clock in the background;
//   5. runs the rounds: every entry on every core count once per round, the order of core counts
//      alternating and the order of entries rotating from round to round. Each run is one process,
//      confined to N performance cores (its affinity mask), at High priority, with Windows' power
//      throttling (EcoQoS) off; Go also gets GOMAXPROCS=N;
//   6. writes raw.jsonl (every line every entry printed, with its round), summary.md and
//      summary.json (medians over the rounds, spreads, ratios to the assembly paired by round,
//      scaling from one core, bytes allocated per call) and environment.json.
//
// Windows only: it confines processes with affinity masks and turns EcoQoS off through kernel32.

#:property AllowUnsafeBlocks=true
#:property TreatWarningsAsErrors=true
// AOT stays off: the driver serializes anonymous objects with reflection and is never published.
#:property PublishAot=false

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

return Driver.Run(args);

/// <summary>The benchmarks project's driver; see the file's opening comment for what it does.</summary>
internal static class Driver
{
    /// <summary>The benchmarks folder (this file's own folder), from which every path is resolved.</summary>
    internal static readonly string Root = Path.GetFullPath(AppContext.GetData("EntryPointFileDirectoryPath") as string
        ?? Path.Combine(Directory.GetCurrentDirectory(), "benchmarks"));

    /// <summary>Runs the driver; see the file's opening comment.</summary>
    /// <param name="args">The kernel's name, then the options.</param>
    /// <returns>0 when the run completed; 1 when a build, a check or a run failed; 2 for a bad command line.</returns>
    public static int Run(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("the driver confines processes with Windows affinity masks and EcoQoS: run it on Windows");
            return 2;
        }

        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (FormatException e)
        {
            Console.Error.WriteLine(e.Message);
            return 2;
        }

        try
        {
            return new Session(options).Run();
        }
        catch (DriverException e)
        {
            Console.Error.WriteLine($"stopped: {e.Message}");
            return 1;
        }
    }
}

/// <summary>A failure that stops the run: a build that failed, a wrong checksum, a run that did not complete.</summary>
/// <param name="message">What failed, for stderr.</param>
internal sealed class DriverException(string message) : Exception(message);

/// <summary>The driver's command line.</summary>
/// <param name="Kernel">The kernel to run; only "crc32c" exists so far.</param>
/// <param name="Rounds">Paired rounds.</param>
/// <param name="Cores">The core counts, each a process confined to that many performance cores.</param>
/// <param name="Cpus">The logical CPUs a run may use, the first N for N cores: each a performance core's first hyperthread.</param>
/// <param name="Guards">The same cores' other hyperthreads, held by guard threads for the whole run.</param>
/// <param name="Entries">The entries to time, by name.</param>
/// <param name="GoExe">The go executable, or null to look for it on PATH.</param>
/// <param name="Out">The results folder.</param>
/// <param name="Quick">A smoke run: one round with short windows, written to a scratch folder.</param>
internal sealed record Options(string Kernel, int Rounds, int[] Cores, int[] Cpus, int[] Guards, string[] Entries, string? GoExe, string Out, bool Quick)
{
    /// <summary>Every entry the CRC-32C kernel has, in the order the summary lists them.</summary>
    public static readonly string[] AllEntries = ["asm", "csharp-loop", "csharp-best", "rust", "go", "javascript", "python"];

    /// <summary>Parses the command line.</summary>
    /// <param name="args">The kernel's name, then the options.</param>
    /// <returns>The options, with defaults for what is not given.</returns>
    /// <exception cref="FormatException">An unknown kernel, entry or option, or a malformed value.</exception>
    public static Options Parse(string[] args)
    {
        if (args.Length == 0 || args[0] != "crc32c")
        {
            throw new FormatException("usage: dotnet run benchmarks/run.cs -- crc32c [--rounds N] [--cores 1,4] [--cpus 2,4,6,8] [--guards 3,5,7,9] [--entries ...] [--go <go.exe>] [--out <folder>] [--quick]");
        }

        int rounds = 12;
        int[] cores = [1, 4], cpus = [2, 4, 6, 8], guards = [3, 5, 7, 9];
        string[] entries = AllEntries;
        string? go = Environment.GetEnvironmentVariable("ASMF_BENCH_GO"), output = null;
        bool quick = false;
        for (int i = 1; i < args.Length; i++)
        {
            // Every option takes a value except --quick.
            string Value() => i + 1 < args.Length ? args[++i] : throw new FormatException($"{args[i]} needs a value");
            int[] Ints() => Value().Split(',').Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
            switch (args[i])
            {
                case "--rounds": rounds = int.Parse(Value(), CultureInfo.InvariantCulture); break;
                case "--cores": cores = Ints(); break;
                case "--cpus": cpus = Ints(); break;
                case "--guards": guards = Ints(); break;
                case "--entries": entries = Value().Split(','); break;
                case "--go": go = Value(); break;
                case "--out": output = Value(); break;
                case "--quick": quick = true; break;
                default: throw new FormatException($"unknown option {args[i]}");
            }
        }

        if (entries.FirstOrDefault(e => !AllEntries.Contains(e)) is { } unknown)
        {
            throw new FormatException($"unknown entry {unknown}; the entries are {string.Join(", ", AllEntries)}");
        }

        if (cores.Max() > cpus.Length || rounds < 1)
        {
            throw new FormatException("--cpus must list at least as many CPUs as the largest core count, and --rounds must be above 0");
        }

        string date = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        output ??= quick
            ? Path.Combine(Path.GetTempPath(), "asmfoundation-benchmarks", "quick")
            : Path.Combine(Path.GetFullPath(AppContext.GetData("EntryPointFileDirectoryPath") as string ?? "benchmarks"), "results", $"{date}-{Machine.ShortName}-crc32c");
        return new Options("crc32c", quick ? 1 : rounds, cores, cpus, guards, entries, go, Path.GetFullPath(output), quick);
    }
}

/// <summary>One run of the driver: the builds, the checks, the rounds and the summary.</summary>
/// <param name="options">The command line.</param>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class Session(Options options)
{
    /// <summary>The input sizes: a short message, a large buffer in cache, and data that must come from RAM.</summary>
    private static readonly int[] Sizes = [64, 1 << 20, 64 << 20];

    /// <summary>Each size's timed window, in seconds: long enough for many calls, and at least one even for the slowest entry at 64 MiB.</summary>
    private static readonly double[] Windows = [0.5, 0.5, 2.0];

    /// <summary>
    /// Each size's warm-up, in seconds. The first is long enough for every JIT to reach its optimized code on one
    /// core (measured for .NET: 2 s with <see cref="DotnetTiering"/>); the JIT's work carries over to the later
    /// sizes, which run in the same process.
    /// </summary>
    private static readonly double[] Warmups = [2.0, 0.6, 1.0];

    /// <summary>
    /// The C# entry's runtime setting: tiered compilation without its promotion delay. With .NET's default delay,
    /// a process confined to one core still ran the 64-byte checksum unoptimized after 3 s of warm-up (9 million calls
    /// a second); with the delay off it reaches, within 2 s, the same profile-guided code a 5-second warm-up reaches
    /// (242 million). Turning tiering or dynamic PGO off instead would have cost C# about 30% (160 to 169 million).
    /// </summary>
    private static readonly (string Name, string Value)[] DotnetTiering = [("DOTNET_TC_CallCountingDelayMs", "0")];

    /// <summary>Where builds, inputs and other bulky files go: outside the repository.</summary>
    private readonly string _work = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "asmfoundation-benchmarks", "work", "crc32c");

    /// <summary>The kernel's folder in the repository.</summary>
    private readonly string _kernel = Path.Combine(Path.GetFullPath(AppContext.GetData("EntryPointFileDirectoryPath") as string ?? "benchmarks"), "kernels", "crc32c");

    /// <summary>Every tool's version, for environment.json and the summary.</summary>
    private readonly Dictionary<string, string> _versions = [];

    /// <summary>The expected CRC-32C of each input, from the driver's own reference.</summary>
    private uint[] _expected = [];

    /// <summary>The built guard (tools/guard), set by <see cref="Build"/>.</summary>
    private string _guardExe = "";

    /// <summary>Runs the whole session.</summary>
    /// <returns>0 when it completed.</returns>
    /// <exception cref="DriverException">A build, a check or a run failed.</exception>
    public int Run()
    {
        Directory.CreateDirectory(options.Out);
        Directory.CreateDirectory(_work);
        Console.WriteLine($"results: {options.Out}");
        Native.DisableThrottling(Process.GetCurrentProcess().Handle);
        Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;

        Dictionary<string, EntryCommand> entries = Build();
        WriteInputs();
        Check(entries);

        using var guards = GuardProcess.Start(_guardExe, options.Guards);
        using var clock = ClockSampler.Start(options.Cpus, Path.Combine(options.Out, "clock.csv"));
        var cells = new List<CellRecord>();
        using var raw = new StreamWriter(Path.Combine(options.Out, "raw.jsonl"), append: false, new UTF8Encoding(false));
        var started = DateTimeOffset.UtcNow;
        for (int round = 1; round <= options.Rounds; round++)
        {
            var clockRound = Stopwatch.StartNew();
            // Alternate the core counts and rotate the entries, so no entry always runs first,
            // last, or right after the same neighbour.
            int[] coreOrder = round % 2 == 1 ? options.Cores : options.Cores.Reverse().ToArray();
            string[] entryOrder = options.Entries.Skip((round - 1) % options.Entries.Length).Concat(options.Entries.Take((round - 1) % options.Entries.Length)).ToArray();
            foreach (int cores in coreOrder)
            {
                foreach (string name in entryOrder)
                {
                    cells.AddRange(RunOnce(entries[name], cores, round, Windows, Warmups, raw));
                }
            }

            raw.Flush();
            Console.WriteLine($"round {round} of {options.Rounds} done in {clockRound.Elapsed.TotalSeconds:F0} s");
        }

        var summary = new Summary(cells, options, _versions, started, DateTimeOffset.UtcNow);
        File.WriteAllText(Path.Combine(options.Out, "summary.md"), summary.Markdown(), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(options.Out, "summary.json"), summary.Json(), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(options.Out, "environment.json"), JsonSerializer.Serialize(new
        {
            machine = Machine.CpuName,
            os = RuntimeInformation.OSDescription,
            versions = _versions,
            cpus = options.Cpus,
            guards = options.Guards,
            sizes = Sizes,
            windows = Windows,
            warmups = Warmups,
            rounds = options.Rounds,
            started,
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(summary.Markdown());
        return 0;
    }

    /// <summary>Builds every entry and returns how to start each.</summary>
    /// <returns>Each entry's command, by name.</returns>
    /// <exception cref="DriverException">A tool is missing or a build failed.</exception>
    private Dictionary<string, EntryCommand> Build()
    {
        string cargo = Tools.Find("cargo"), node = Tools.Find("node"), python = Tools.Find("python");
        string go = options.GoExe ?? Tools.Find("go");
        _versions["dotnet"] = Tools.Output("dotnet", "--version", Driver.Root);
        _versions["cargo"] = Tools.Output(cargo, "--version", Driver.Root);
        _versions["rustc"] = Tools.Output(Tools.Find("rustc"), "--version", Driver.Root);
        _versions["go"] = Tools.Output(go, "version", Driver.Root);
        _versions["node"] = Tools.Output(node, "--version", Driver.Root);
        _versions["python"] = Tools.Output(python, "--version", Driver.Root);

        Console.WriteLine("building the assembly library (AsmFoundation.Sdk), the C# entry, the Rust programs and the Go entry");
        Tools.Run("dotnet", ["build", "-c", "Release", "-nodeReuse:false"], Path.Combine(_kernel, "asm", "lib"));
        string library = Path.Combine(_kernel, "asm", "lib", "bin", "Release", "freestanding", "win-x64", "crc32c.dll");
        Tools.Run("dotnet", ["build", "-c", "Release", "-nodeReuse:false"], Path.Combine(_kernel, "csharp"));
        string csharp = Path.Combine(_kernel, "csharp", "bin", "Release", "net10.0", "crc32c-csharp.exe");
        // Each crate builds from its own folder, so its .cargo/config.toml applies, into a target
        // folder outside the repository.
        Tools.Run(cargo, ["build", "--release"], Path.Combine(_kernel, "asm", "harness"), ("CARGO_TARGET_DIR", Path.Combine(_work, "cargo", "asm")));
        Tools.Run(cargo, ["build", "--release"], Path.Combine(_kernel, "rust"), ("CARGO_TARGET_DIR", Path.Combine(_work, "cargo", "rust")));
        // The guard that holds the timed cores' other hyperthreads (tools/guard).
        Tools.Run(cargo, ["build", "--release"], Path.Combine(Driver.Root, "tools", "guard"), ("CARGO_TARGET_DIR", Path.Combine(_work, "cargo", "guard")));
        _guardExe = Path.Combine(_work, "cargo", "guard", "release", "guard.exe");
        string goOut = Path.Combine(_work, "go", "crc32c-go.exe");
        Tools.Run(go, ["build", "-trimpath", "-o", goOut, "."], Path.Combine(_kernel, "go"), ("GOTOOLCHAIN", "local"));

        foreach (string built in new[] { library, csharp, goOut, _guardExe })
        {
            if (!File.Exists(built))
            {
                throw new DriverException($"the build did not produce {built}");
            }
        }

        string asmHarness = Path.Combine(_work, "cargo", "asm", "release", "crc32c-asm-harness.exe");
        string rust = Path.Combine(_work, "cargo", "rust", "release", "crc32c-rust.exe");
        return new Dictionary<string, EntryCommand>
        {
            ["asm"] = new("asm", "Assembly (AsmSdk)", asmHarness, ["--library", library], []),
            ["csharp-loop"] = new("csharp-loop", "C#, simple loop", csharp, ["--variant", "loop"], DotnetTiering),
            ["csharp-best"] = new("csharp-best", "C#, at its best", csharp, ["--variant", "best"], DotnetTiering),
            ["rust"] = new("rust", "Rust", rust, [], []),
            ["go"] = new("go", "Go", goOut, [], []),
            ["javascript"] = new("javascript", "JavaScript (Node.js)", node, [Path.Combine(_kernel, "javascript", "crc32c.mjs")], []),
            ["python"] = new("python", "Python", python, ["-I", Path.Combine(_kernel, "python", "crc32c.py")], []),
        };
    }

    /// <summary>Writes every input and computes its expected checksum with the driver's own reference.</summary>
    /// <exception cref="DriverException">The reference fails the standard check value.</exception>
    private void WriteInputs()
    {
        if (Reference.Crc32C("123456789"u8) != 0xE3069283)
        {
            throw new DriverException("the driver's reference CRC-32C fails the check value");
        }

        Directory.CreateDirectory(Path.Combine(_work, "inputs"));
        _expected = new uint[Sizes.Length];
        for (int i = 0; i < Sizes.Length; i++)
        {
            byte[] data = Reference.Input(Sizes[i]);
            File.WriteAllBytes(Path.Combine(_work, "inputs", $"{Sizes[i]}.bin"), data);
            _expected[i] = Reference.Crc32C(data);
            Console.WriteLine($"input {Sizes[i]} bytes: CRC-32C 0x{_expected[i]:x8}");
        }
    }

    /// <summary>Runs every entry once on one core with short windows: every checksum must be right before anything is timed.</summary>
    /// <param name="entries">The built entries.</param>
    /// <exception cref="DriverException">An entry failed or printed a malformed result.</exception>
    private void Check(Dictionary<string, EntryCommand> entries)
    {
        foreach (string name in options.Entries)
        {
            RunOnce(entries[name], 1, 0, Sizes.Select(_ => 0.05).ToArray(), Sizes.Select(_ => 0.0).ToArray(), raw: null);
            Console.WriteLine($"checked {name}");
        }
    }

    /// <summary>Runs one entry once, confined to <paramref name="cores"/> performance cores, and returns its cells.</summary>
    /// <param name="entry">How to start the entry.</param>
    /// <param name="cores">How many cores (and threads).</param>
    /// <param name="round">The round, recorded with the cells (0 for the check).</param>
    /// <param name="windows">Each size's window.</param>
    /// <param name="warmups">Each size's warm-up.</param>
    /// <param name="raw">Where every printed line goes, with the round; null for the check.</param>
    /// <returns>One cell per size.</returns>
    /// <exception cref="DriverException">The process failed, timed out, printed a malformed line, ran on other cores than asked, or reported other thread counts.</exception>
    private IEnumerable<CellRecord> RunOnce(EntryCommand entry, int cores, int round, double[] windows, double[] warmups, StreamWriter? raw)
    {
        string Join(IEnumerable<double> values) => string.Join(",", values.Select(v => v.ToString(CultureInfo.InvariantCulture)));
        var arguments = new List<string>(entry.Prefix)
        {
            "--threads", cores.ToString(CultureInfo.InvariantCulture),
            "--inputs", Path.Combine(_work, "inputs"),
            "--sizes", string.Join(",", Sizes),
            "--expect", string.Join(",", _expected.Select(e => $"0x{e:x8}")),
            "--window", Join(windows),
            "--warmup", Join(warmups),
        };
        long mask = options.Cpus.Take(cores).Aggregate(0L, (m, cpu) => m | (1L << cpu));
        var start = new ProcessStartInfo(entry.Exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(entry.Exe) ?? Driver.Root,
        };
        foreach (string a in arguments)
        {
            start.ArgumentList.Add(a);
        }

        // Go sizes its scheduler from the CPUs it sees at start, before the mask applies.
        start.Environment["GOMAXPROCS"] = cores.ToString(CultureInfo.InvariantCulture);
        foreach ((string name, string value) in entry.Environment)
        {
            start.Environment[name] = value;
        }
        using var process = Process.Start(start) ?? throw new DriverException($"could not start {entry.Exe}");
        // Confine it at once: threads already running move to the allowed CPUs, and everything the
        // entry times runs after its warm-up, long after this.
        process.ProcessorAffinity = (IntPtr)mask;
        process.PriorityClass = ProcessPriorityClass.High;
        Native.DisableThrottling(process.Handle);
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromMinutes(15)))
        {
            process.Kill(entireProcessTree: true);
            throw new DriverException($"{entry.Name} on {cores} cores did not finish in 15 minutes");
        }

        string output = stdout.Result;
        if (process.ExitCode != 0)
        {
            throw new DriverException($"{entry.Name} on {cores} cores exited {process.ExitCode}: {stderr.Result.Trim()}");
        }

        string expectedMask = $"0x{mask:x}";
        var cells = new List<CellRecord>();
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            JsonNode node = JsonNode.Parse(line) ?? throw new DriverException($"{entry.Name}: empty line");
            raw?.WriteLine(new JsonObject { ["round"] = round, ["entry"] = entry.Name, ["cores"] = cores, ["line"] = node.DeepClone() }.ToJsonString());
            if ((string?)node["type"] != "cell")
            {
                continue;
            }

            int size = (int)node["size"]!;
            var streams = node["streams"]!.AsArray().Select(s => ((long)s!["calls"]!, (double)s["seconds"]!)).ToArray();
            if ((int)node["threads"]! != cores || streams.Length != cores || streams.Any(s => s.Item1 <= 0 || s.Item2 <= 0))
            {
                throw new DriverException($"{entry.Name} on {cores} cores, {size} bytes: {streams.Length} threads reported, or one did nothing");
            }

            // The confinement must have held where the entry can see it: the process's mask, or
            // each Python worker's.
            var masks = new List<string>();
            if (node["affinity"] is { } a)
            {
                masks.Add((string)a!);
            }

            if (node["workerAffinity"] is JsonArray workers)
            {
                masks.AddRange(workers.Select(w => (string)w!));
            }

            if (masks.FirstOrDefault(m => m != "unknown" && m != expectedMask) is { } wrong)
            {
                throw new DriverException($"{entry.Name} on {cores} cores ran with affinity {wrong}, not {expectedMask}");
            }

            long? allocated = node["allocatedBytes"] is JsonValue v ? (long)v.GetValue<double>() : null;
            cells.Add(new CellRecord(entry.Name, entry.Label, round, cores, size, streams.Sum(s => s.Item1 / s.Item2), streams.Sum(s => s.Item1), allocated));
        }

        if (cells.Count != Sizes.Length)
        {
            throw new DriverException($"{entry.Name} on {cores} cores printed {cells.Count} cells, not {Sizes.Length}");
        }

        return cells;
    }
}

/// <summary>How to start an entry.</summary>
/// <param name="Name">Its name on the command line and in the results.</param>
/// <param name="Label">Its name in the summary.</param>
/// <param name="Exe">The executable: the entry itself, or its runtime (node, python).</param>
/// <param name="Prefix">Arguments before the protocol's: a script, a variant, a library.</param>
/// <param name="Environment">Environment variables of the entry's own, such as the C# entry's tiering setting.</param>
internal sealed record EntryCommand(string Name, string Label, string Exe, string[] Prefix, (string Name, string Value)[] Environment);

/// <summary>One timed cell of one round.</summary>
/// <param name="Entry">The entry's name.</param>
/// <param name="Label">The entry's name in the summary.</param>
/// <param name="Round">The round (0 for the check).</param>
/// <param name="Cores">The cores it was confined to.</param>
/// <param name="Size">The input size.</param>
/// <param name="CallsPerSecond">All threads together: each thread's calls over its own window, summed.</param>
/// <param name="Calls">All threads' calls.</param>
/// <param name="AllocatedBytes">Bytes allocated across the window, or null where the runtime does not count them.</param>
internal sealed record CellRecord(string Entry, string Label, int Round, int Cores, int Size, double CallsPerSecond, long Calls, long? AllocatedBytes);

/// <summary>The medians, spreads, paired ratios and scaling of a session, as Markdown and JSON.</summary>
/// <param name="cells">Every timed cell.</param>
/// <param name="options">The session's options.</param>
/// <param name="versions">The tools' versions.</param>
/// <param name="started">When the rounds began.</param>
/// <param name="finished">When they ended.</param>
internal sealed class Summary(List<CellRecord> cells, Options options, Dictionary<string, string> versions, DateTimeOffset started, DateTimeOffset finished)
{
    /// <summary>The cells of one entry, size and core count, by round.</summary>
    /// <param name="entry">The entry.</param>
    /// <param name="size">The size.</param>
    /// <param name="cores">The core count.</param>
    /// <returns>The cells, ordered by round.</returns>
    private CellRecord[] Of(string entry, int size, int cores) =>
        cells.Where(c => c.Entry == entry && c.Size == size && c.Cores == cores).OrderBy(c => c.Round).ToArray();

    /// <summary>The median of some values (the mean of the middle two for an even count).</summary>
    /// <param name="values">At least one value.</param>
    /// <returns>The median.</returns>
    private static double Median(IEnumerable<double> values)
    {
        double[] s = values.Order().ToArray();
        return s.Length % 2 == 1 ? s[s.Length / 2] : (s[s.Length / 2 - 1] + s[s.Length / 2]) / 2;
    }

    /// <summary>One row of the summary: an entry at one size on one core count.</summary>
    /// <param name="entry">The entry.</param>
    /// <param name="size">The size.</param>
    /// <param name="cores">The core count.</param>
    /// <returns>The row's figures, or null when the entry has no such cells.</returns>
    private Row? RowOf(string entry, int size, int cores)
    {
        CellRecord[] own = Of(entry, size, cores);
        if (own.Length == 0)
        {
            return null;
        }

        // Paired with the assembly in the same round, on the same core count and size.
        CellRecord[] asm = Of("asm", size, cores);
        double[] ratios = own.Join(asm, c => c.Round, a => a.Round, (c, a) => c.CallsPerSecond / a.CallsPerSecond).ToArray();
        CellRecord[] one = Of(entry, size, 1);
        double[] scaling = cores == 1 ? [] : own.Join(one, c => c.Round, o => o.Round, (c, o) => c.CallsPerSecond / o.CallsPerSecond).ToArray();
        double[] allocated = own.Where(c => c.AllocatedBytes is not null).Select(c => (double)c.AllocatedBytes!.Value / c.Calls).ToArray();
        return new Row(
            entry,
            own[0].Label,
            size,
            cores,
            Median(own.Select(c => c.CallsPerSecond)),
            own.Min(c => c.CallsPerSecond),
            own.Max(c => c.CallsPerSecond),
            ratios.Length > 0 ? Median(ratios) : null,
            scaling.Length > 0 ? Median(scaling) : null,
            allocated.Length > 0 ? Median(allocated) : null,
            own.Length);
    }

    /// <summary>Every row, entry by entry in the options' order.</summary>
    /// <returns>The rows.</returns>
    private IEnumerable<Row> Rows() =>
        from size in cells.Select(c => c.Size).Distinct().Order()
        from cores in options.Cores
        from entry in options.Entries
        let row = RowOf(entry, size, cores)
        where row is not null
        select row;

    /// <summary>The summary as Markdown tables.</summary>
    /// <returns>The Markdown.</returns>
    public string Markdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"# CRC-32C, {options.Rounds} paired rounds, {started:yyyy-MM-dd HH:mm} to {finished:HH:mm} UTC");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"{Machine.CpuName}; cores = processes confined to logical CPUs {string.Join(",", options.Cpus)}, the first N for N cores, siblings {string.Join(",", options.Guards)} guarded.");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Tools: {string.Join("; ", versions.Select(v => $"{v.Key} {v.Value}"))}.");
        sb.AppendLine();
        sb.AppendLine("Medians over the rounds. Throughput is all cores together; \"per call\" is one call's time on its core while the others run; \"vs asm\" is the median of the per-round ratios to the assembly (above 1: faster than the assembly); \"x1 core\" the median of the per-round ratios to the same entry on one core; allocated is bytes per call (n/a: the runtime keeps no count).");
        foreach (var group in Rows().GroupBy(r => r.Size))
        {
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"## {Format.Size(group.Key)}");
            sb.AppendLine();
            sb.AppendLine("| Entry | Cores | Calls/s | GB/s | Per call | Lowest to highest round, calls/s | vs asm | x1 core | Allocated per call |");
            sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|");
            foreach (Row r in group)
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"| {r.Label} | {r.Cores} | {Format.Rate(r.MedianRate)} | {Format.Num(r.MedianRate * r.Size / 1e9)} | {Format.Time(r.Cores * 1e9 / r.MedianRate)} | " +
                    $"{Format.Rate(r.MinRate)} to {Format.Rate(r.MaxRate)} | {(r.VsAsm is { } v ? $"{v:F3}x" : "")} | {(r.Scaling is { } s ? $"{s:F2}x" : "")} | " +
                    $"{(r.AllocatedPerCall is { } a ? Format.Num(a) + " B" : "n/a")} |");
            }
        }

        return sb.ToString();
    }

    /// <summary>The summary as JSON, for the website's charts.</summary>
    /// <returns>The JSON.</returns>
    public string Json() => JsonSerializer.Serialize(new
    {
        kernel = options.Kernel,
        machine = Machine.CpuName,
        started,
        finished,
        rounds = options.Rounds,
        cpus = options.Cpus,
        versions,
        rows = Rows().Select(r => new
        {
            entry = r.Entry,
            label = r.Label,
            size = r.Size,
            cores = r.Cores,
            callsPerSecond = r.MedianRate,
            gigabytesPerSecond = r.MedianRate * r.Size / 1e9,
            nanosecondsPerCallPerCore = r.Cores * 1e9 / r.MedianRate,
            lowestCallsPerSecond = r.MinRate,
            highestCallsPerSecond = r.MaxRate,
            versusAsm = r.VsAsm,
            scalingFromOneCore = r.Scaling,
            allocatedBytesPerCall = r.AllocatedPerCall,
            rounds = r.Rounds,
        }),
    }, new JsonSerializerOptions { WriteIndented = true });

    /// <summary>One summary row.</summary>
    /// <param name="Entry">The entry's name.</param>
    /// <param name="Label">Its label.</param>
    /// <param name="Size">The size.</param>
    /// <param name="Cores">The core count.</param>
    /// <param name="MedianRate">Median calls per second over the rounds, all cores.</param>
    /// <param name="MinRate">The lowest round.</param>
    /// <param name="MaxRate">The highest round.</param>
    /// <param name="VsAsm">Median per-round ratio to the assembly, or null without assembly cells.</param>
    /// <param name="Scaling">Median per-round ratio to one core, or null on one core.</param>
    /// <param name="AllocatedPerCall">Median bytes allocated per call, or null where not counted.</param>
    /// <param name="Rounds">How many rounds the row has.</param>
    private sealed record Row(string Entry, string Label, int Size, int Cores, double MedianRate, double MinRate, double MaxRate, double? VsAsm, double? Scaling, double? AllocatedPerCall, int Rounds);
}

/// <summary>Number formats of the summary: three significant figures, ASCII units.</summary>
internal static class Format
{
    /// <summary>Three significant figures.</summary>
    /// <param name="v">A non-negative number.</param>
    /// <returns>The text.</returns>
    public static string Num(double v) => v switch
    {
        >= 100 => v.ToString("F0", CultureInfo.InvariantCulture),
        >= 10 => v.ToString("F1", CultureInfo.InvariantCulture),
        >= 1 => v.ToString("F2", CultureInfo.InvariantCulture),
        _ => v.ToString("G3", CultureInfo.InvariantCulture),
    };

    /// <summary>A rate with k, M or G.</summary>
    /// <param name="v">Calls per second.</param>
    /// <returns>The text.</returns>
    public static string Rate(double v) => v >= 1e9 ? Num(v / 1e9) + "G" : v >= 1e6 ? Num(v / 1e6) + "M" : v >= 1e3 ? Num(v / 1e3) + "k" : Num(v);

    /// <summary>A time in ns, us, ms or s.</summary>
    /// <param name="ns">Nanoseconds.</param>
    /// <returns>The text.</returns>
    public static string Time(double ns) => ns >= 1e9 ? Num(ns / 1e9) + " s" : ns >= 1e6 ? Num(ns / 1e6) + " ms" : ns >= 1e3 ? Num(ns / 1e3) + " us" : Num(ns) + " ns";

    /// <summary>A size as 64 B, 1 MiB, 64 MiB.</summary>
    /// <param name="size">Bytes.</param>
    /// <returns>The text.</returns>
    public static string Size(int size) => size >= 1 << 20 ? $"{size >> 20} MiB" : size >= 1 << 10 ? $"{size >> 10} KiB" : $"{size} B";
}

/// <summary>The kernel's inputs and an independent CRC-32C to check every entry against.</summary>
internal static class Reference
{
    /// <summary>The byte-at-a-time table of CRC-32C (reflected polynomial 0x82F63B78).</summary>
    private static readonly uint[] Table = Enumerable.Range(0, 256).Select(i =>
    {
        uint c = (uint)i;
        for (int k = 0; k < 8; k++)
        {
            c = (c >> 1) ^ (0x82F63B78u & (uint)-(int)(c & 1));
        }

        return c;
    }).ToArray();

    /// <summary>CRC-32C byte at a time: slow, simple, and shares no code with any entry.</summary>
    /// <param name="data">The bytes.</param>
    /// <returns>The checksum.</returns>
    public static uint Crc32C(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return ~crc;
    }

    /// <summary>
    /// The input of a size: splitmix64 seeded with the size, each 64-bit output little-endian, cut
    /// to the size. Any language can regenerate it; the kernel's README gives its first bytes.
    /// </summary>
    /// <param name="size">The size in bytes.</param>
    /// <returns>The bytes.</returns>
    public static byte[] Input(int size)
    {
        byte[] data = new byte[(size + 7) & ~7];
        ulong state = (ulong)size;
        for (int i = 0; i < data.Length; i += 8)
        {
            state += 0x9E3779B97F4A7C15;
            ulong z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EB;
            BitConverter.TryWriteBytes(data.AsSpan(i, 8), z ^ (z >> 31));
        }

        return data.AsSpan(0, size).ToArray();
    }
}

/// <summary>External tools: finding them, their versions, and running builds.</summary>
internal static class Tools
{
    /// <summary>Finds an executable on PATH.</summary>
    /// <param name="name">Its name without extension.</param>
    /// <returns>Its full path.</returns>
    /// <exception cref="DriverException">It is not on PATH.</exception>
    public static string Find(string name)
    {
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(dir, name + ".exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DriverException($"{name} is not on PATH");
    }

    /// <summary>Runs a tool and returns its first line of output (its version).</summary>
    /// <param name="exe">The tool.</param>
    /// <param name="argument">The one argument, such as --version.</param>
    /// <param name="cwd">Where to run it.</param>
    /// <returns>The first line, trimmed.</returns>
    /// <exception cref="DriverException">It cannot be started.</exception>
    public static string Output(string exe, string argument, string cwd)
    {
        var start = new ProcessStartInfo(exe, argument) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = cwd };
        using var p = Process.Start(start) ?? throw new DriverException($"could not start {exe}");
        string text = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
    }

    /// <summary>Runs a build step and stops the session when it fails.</summary>
    /// <param name="exe">The tool.</param>
    /// <param name="arguments">Its arguments.</param>
    /// <param name="cwd">Where to run it.</param>
    /// <param name="environment">Extra environment variables.</param>
    /// <exception cref="DriverException">The step exited with an error; its output is in the message.</exception>
    public static void Run(string exe, string[] arguments, string cwd, params (string Name, string Value)[] environment)
    {
        var start = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = cwd };
        foreach (string a in arguments)
        {
            start.ArgumentList.Add(a);
        }

        foreach ((string name, string value) in environment)
        {
            start.Environment[name] = value;
        }

        using var p = Process.Start(start) ?? throw new DriverException($"could not start {exe}");
        Task<string> err = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            throw new DriverException($"{Path.GetFileName(exe)} {string.Join(" ", arguments)} in {cwd} failed:\n{output}\n{err.Result}");
        }
    }
}

/// <summary>The machine's name, for the results folder and the summary.</summary>
internal static class Machine
{
    /// <summary>The CPU's marketing name from the registry.</summary>
    public static readonly string CpuName = OperatingSystem.IsWindows()
        ? (Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0")?.GetValue("ProcessorNameString") as string ?? "unknown CPU").Trim()
        : "unknown CPU";

    /// <summary>A short machine name for folder names: the CPU's model, such as i9-13900K.</summary>
    public static readonly string ShortName = CpuName.Split(' ').LastOrDefault(w => w.Contains('-')) ?? "machine";
}

/// <summary>
/// The guard process (<c>tools/guard</c>): one thread pinned to each of the timed cores' other hyperthreads, waiting in
/// <c>tpause</c> for the whole session, so that no other process's thread runs on a timed core while the core stays its
/// timed thread's. A guard spinning on <c>pause</c> instead (.NET's SpinWait, as this driver first did) took up to a
/// third of a timed thread's speed in the trials of 2026-11-26 (the benchmarks README has them).
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class GuardProcess : IDisposable
{
    /// <summary>The guard; it ends when its standard input closes.</summary>
    private readonly Process _process;

    /// <summary>Wraps a started guard.</summary>
    /// <param name="process">The guard process.</param>
    private GuardProcess(Process process) => _process = process;

    /// <summary>Starts the guard on the given CPUs, at High priority with power throttling off.</summary>
    /// <param name="exe">The built guard.</param>
    /// <param name="cpus">The logical CPUs to hold.</param>
    /// <returns>The running guard.</returns>
    /// <exception cref="DriverException">It could not start, or it exited at once (a CPU it cannot pin to).</exception>
    public static GuardProcess Start(string exe, int[] cpus)
    {
        var start = new ProcessStartInfo(exe, string.Join(",", cpus))
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
        };
        Process process = Process.Start(start) ?? throw new DriverException($"could not start {exe}");
        process.PriorityClass = ProcessPriorityClass.High;
        Native.DisableThrottling(process.Handle);
        // Its first line says which instruction it waits with; an exit here means a bad CPU list.
        string? first = process.StandardError.ReadLine();
        if (process.WaitForExit(200))
        {
            throw new DriverException($"the guard exited at once: {first} {process.StandardError.ReadToEnd()}");
        }

        Console.WriteLine(first);
        return new GuardProcess(process);
    }

    /// <summary>Ends the guard by closing its standard input, as the guard expects.</summary>
    public void Dispose()
    {
        _process.StandardInput.Close();
        if (!_process.WaitForExit(5000))
        {
            _process.Kill();
        }

        _process.Dispose();
    }
}

/// <summary>Samples the timed cores' clock every 5 seconds with Windows' typeperf, into a CSV file.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class ClockSampler : IDisposable
{
    /// <summary>The typeperf process, stopped on dispose.</summary>
    private readonly Process? _process;

    /// <summary>Wraps a started sampler.</summary>
    /// <param name="process">typeperf, or null when it could not start (the session goes on without clock samples).</param>
    private ClockSampler(Process? process) => _process = process;

    /// <summary>Starts typeperf on the given CPUs' nominal frequency and "% Processor Performance".</summary>
    /// <param name="cpus">The logical CPUs (processor group 0).</param>
    /// <param name="csv">The output file.</param>
    /// <returns>The sampler.</returns>
    public static ClockSampler Start(int[] cpus, string csv)
    {
        var start = new ProcessStartInfo("typeperf") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (int cpu in cpus)
        {
            start.ArgumentList.Add($@"\Processor Information(0,{cpu})\Processor Frequency");
            start.ArgumentList.Add($@"\Processor Information(0,{cpu})\% Processor Performance");
        }

        foreach (string a in new[] { "-si", "5", "-f", "CSV", "-o", csv, "-y" })
        {
            start.ArgumentList.Add(a);
        }

        try
        {
            return new ClockSampler(Process.Start(start));
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new ClockSampler(null);
        }
    }

    /// <summary>Stops typeperf.</summary>
    public void Dispose()
    {
        if (_process is { HasExited: false })
        {
            _process.Kill();
        }

        _process?.Dispose();
    }
}

/// <summary>The kernel32 calls the driver needs.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class Native
{
    /// <summary>Turns power throttling (EcoQoS) off for a process, which otherwise runs on efficiency cores when it has no window.</summary>
    /// <param name="process">The process handle.</param>
    public static unsafe void DisableThrottling(IntPtr process)
    {
        // PROCESS_POWER_THROTTLING_STATE v1: control the execution-speed knob (1) and set it to
        // 0, never throttle. ProcessPowerThrottling is information class 4.
        uint* state = stackalloc uint[] { 1, 1, 0 };
        SetProcessInformation(process, 4, state, 12);
    }

    /// <summary>kernel32's SetProcessInformation.</summary>
    /// <param name="process">The process.</param>
    /// <param name="informationClass">What to set.</param>
    /// <param name="information">The setting.</param>
    /// <param name="size">Its size in bytes.</param>
    /// <returns>Nonzero on success.</returns>
    [DllImport("kernel32.dll")]
    private static extern unsafe int SetProcessInformation(IntPtr process, int informationClass, void* information, uint size);
}
