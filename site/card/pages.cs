#!/usr/bin/env dotnet
#:property TargetFramework=net10.0
#:property Nullable=enable
#:property ImplicitUsings=enable
#:property PublishAot=false
#:property InvariantGlobalization=true

// pages.cs - renders the website's per-page link-preview cards, site/assets/cards/<page>.png, from a built site.
//
// Each card is the page's name over the block the page leads with (Benchmarks its first two charts, Architecture
// its drawing...), captured from the built page itself, so the picture is the page's own pixels and data. The
// owner picked a card for each of six pages on 2026-11-26, rendered by hand rather than in CI: run this after a
// change to what a page opens with (a new figure, a project's new state, a new chart), then commit the PNGs.
//
// Usage, from the repository root, after building the site:
//   dotnet run site/build.cs
//   dotnet run site/card/pages.cs                          reads _site/, finds Chrome
//   dotnet run site/card/pages.cs -- --site <dir> --chrome <path to chrome or chromium>
// It starts its own headless Chrome (a throw-away profile; never the user's browser), drives it over the DevTools
// protocol, captures each page's block at 2x, renders site/card/page.html around it at 1200x630 px, and writes the
// six cards only after all of them rendered. 1200x630 at 1x, not 2x: the platforms draw a card about 600 px wide,
// so 1x stays sharp on high-density screens, and a text-heavy card stays under WhatsApp's ~300 KB (at 2x four of
// the six were 350-430 KB). site/build.cs reads each card's size from its PNG header for og:image:width/height.
// Exit status: 0 written; 1 Chrome, a page or a selector failed (no card is written then); 2 wrong arguments.
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

try
{
    var options = CardOptions.Parse(args);
    var written = await PageCards.RenderAsync(options);
    Console.WriteLine($"Wrote {written} cards into site/assets/cards/");
    return 0;
}
catch (ArgumentException e)
{
    Console.Error.WriteLine($"error: {e.Message}\nusage: dotnet run site/card/pages.cs [-- --site <dir>] [--chrome <path>]");
    return 2;
}
catch (CardException e)
{
    Console.Error.WriteLine($"error: {e.Message}");
    return 1;
}

/// <summary>A failure while rendering the cards: Chrome missing or unresponsive, a page that is not in the built
/// site, a selector that matches nothing. Reported as one line; no card has been written when it is thrown.</summary>
/// <param name="message">What failed, and where possible which page and the fix.</param>
sealed class CardException(string message) : Exception(message);

/// <summary>What one run renders from, and with which browser.</summary>
/// <param name="Site">The built site (the build's output folder).</param>
/// <param name="Chrome">The Chrome or Chromium executable.</param>
sealed record CardOptions(string Site, string Chrome)
{
    /// <summary>Reads the command line, defaulting to <c>_site/</c> and the first Chrome found.</summary>
    /// <param name="args">The arguments after <c>--</c>.</param>
    /// <returns>The options, with both paths checked.</returns>
    /// <exception cref="ArgumentException">An unknown argument or a missing value.</exception>
    /// <exception cref="CardException">The site folder, the card template or Chrome cannot be found.</exception>
    public static CardOptions Parse(string[] args)
    {
        string site = "_site", chrome = "";
        for (var i = 0; i < args.Length; i++)
        {
            string Value() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
            switch (args[i])
            {
                case "--site": site = Value(); break;
                case "--chrome": chrome = Value(); break;
                default: throw new ArgumentException($"unknown argument {args[i]}.");
            }
        }
        site = Path.GetFullPath(site);
        if (!File.Exists(Path.Combine(site, "index.html")))
            throw new CardException($"{site} holds no built site: run dotnet run site/build.cs first, from the repository root.");
        if (!File.Exists(PageCards.Template))
            throw new CardException($"{PageCards.Template} is missing: run this from the repository root.");
        return new CardOptions(site, chrome.Length > 0 ? chrome : FindChrome());
    }

    /// <summary>Finds a Chrome to drive: the CHROME variable, then the usual install places of each OS.</summary>
    /// <returns>The executable's path, or a bare name the OS resolves on PATH (Linux).</returns>
    /// <exception cref="CardException">No Chrome on Windows or macOS at its usual places, and no CHROME variable.</exception>
    static string FindChrome()
    {
        var fromEnv = Environment.GetEnvironmentVariable("CHROME");
        if (!string.IsNullOrEmpty(fromEnv)) return fromEnv;
        string[] candidates = OperatingSystem.IsWindows()
            ? [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Google\Chrome\Application\chrome.exe"),
               Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Google\Chrome\Application\chrome.exe"),
               Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\Application\chrome.exe")]
            : OperatingSystem.IsMacOS()
                ? ["/Applications/Google Chrome.app/Contents/MacOS/Google Chrome", "/Applications/Chromium.app/Contents/MacOS/Chromium"]
                : ["/usr/bin/google-chrome", "/usr/bin/chromium", "/usr/bin/chromium-browser"];
        return candidates.FirstOrDefault(File.Exists)
            ?? throw new CardException("no Chrome found: pass --chrome <path>, or set CHROME.");
    }
}

/// <summary>One page's card: where the page is, and which block of it the card shows.</summary>
/// <param name="Name">The card's file name, without .png; also how errors name the page.</param>
/// <param name="Path">The page's folder in the built site ("benchmarks/").</param>
/// <param name="Selector">The elements whose box, together, is the block: their union, descendants included, so a
/// label that overflows its chart (the four-core chart's "10M") is not cut off.</param>
/// <param name="MaxHeight">The most of the block the card shows, in CSS pixels from its top: a page's opening is
/// what matters, and a long block (About's whole log) would shrink to unreadable.</param>
sealed record PageCard(string Name, string Path, string Selector, int MaxHeight);

/// <summary>Renders every page's card with one headless Chrome.</summary>
static class PageCards
{
    /// <summary>The card template, relative to the repository root (the working directory).</summary>
    public const string Template = "site/card/page.html";

    /// <summary>Where the cards go; each page's front matter names its file (card: cards/&lt;name&gt;.png).</summary>
    const string OutputDirectory = "site/assets/cards";

    /// <summary>The six pages and their blocks, as the owner approved them in the drafts of 2026-11-26.</summary>
    static readonly PageCard[] Cards =
    [
        new("projects", "projects/", ".sheets > article:nth-of-type(-n+2)", 400),
        new("docs", "docs/", ".toc", 430),
        new("benchmarks", "benchmarks/", ".charts > section:nth-of-type(-n+2)", 300),
        new("architecture", "architecture/", ".key, .elev-wrap", 520),
        new("get-started", "get-started/", ".gs-sec:first-of-type", 330),
        new("about", "about/", ".log-col, .facts", 470),
    ];

    /// <summary>The block's box in document coordinates, and the page's name from its h1. A function expression:
    /// the caller appends its arguments, the selector and the height cap.</summary>
    const string BlockScript = """
        (sel, maxH) => {
          const els = [...document.querySelectorAll(sel)];
          if (!els.length) return null;
          let l = Infinity, t = Infinity, r = -Infinity, b = -Infinity;
          for (const el of els) for (const e of [el, ...el.querySelectorAll('*')]) {
            const q = e.getBoundingClientRect();
            if (q.width === 0 && q.height === 0) continue;   // closed <details>, display:none
            l = Math.min(l, q.left); t = Math.min(t, q.top); r = Math.max(r, q.right); b = Math.max(b, q.bottom);
          }
          const h1 = document.querySelector('h1');
          return { x: Math.floor(l + scrollX), y: Math.floor(t + scrollY), w: Math.ceil(r - l), h: Math.ceil(Math.min(b - t, maxH)),
                   title: h1 ? h1.textContent.trim() : '' };
        }
        """;

    /// <summary>Renders all the cards, then writes them.</summary>
    /// <param name="o">The built site and the Chrome to use.</param>
    /// <returns>How many cards were written.</returns>
    /// <exception cref="CardException">Chrome would not start or answer, a page is missing, or a selector matched
    /// nothing. Cards are kept in memory until every one rendered, so a failure leaves the committed cards as they
    /// were.</exception>
    public static async Task<int> RenderAsync(CardOptions o)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var work = Directory.CreateTempSubdirectory("asmf-cards-");
        HeadlessChrome? chrome = null;
        try
        {
            // inside the try, so a start that times out is reported like any other failure
            chrome = await HeadlessChrome.StartAsync(o.Chrome, timeout.Token);
            var cdp = chrome.Page;
            var rendered = new List<(string File, byte[] Png)>();
            foreach (var card in Cards)
            {
                var page = Path.Combine(o.Site, card.Path, "index.html");
                if (!File.Exists(page)) throw new CardException($"{card.Name}: {page} is not in the built site.");

                // The page, laid out as a desktop reader sees it, at 2x so the card stays sharp when scaled down.
                await cdp.SendAsync("Emulation.setDeviceMetricsOverride", new { width = 1280, height = 900, deviceScaleFactor = 2, mobile = false }, timeout.Token);
                await LoadAsync(cdp, new Uri(page).AbsoluteUri, timeout.Token);
                var box = await cdp.EvaluateAsync($"({BlockScript})({JsonSerializer.Serialize(card.Selector)}, {card.MaxHeight})", timeout.Token);
                if (box.ValueKind != JsonValueKind.Object)
                    throw new CardException($"{card.Name}: {card.Selector} matches nothing on {card.Path}: the page changed; update the selector in pages.cs.");
                var block = await cdp.ScreenshotAsync(box.GetProperty("x").GetDouble(), box.GetProperty("y").GetDouble(),
                    box.GetProperty("w").GetDouble(), box.GetProperty("h").GetDouble(), timeout.Token);
                var blockFile = Path.Combine(work.FullName, card.Name + ".png");
                await File.WriteAllBytesAsync(blockFile, block, timeout.Token);

                // The card: the template around the block, at the 1200x630 the platforms draw (1x: see the header).
                var title = box.GetProperty("title").GetString();
                if (string.IsNullOrEmpty(title)) throw new CardException($"{card.Name}: {card.Path} has no h1 to name the card.");
                await cdp.SendAsync("Emulation.setDeviceMetricsOverride", new { width = 1200, height = 630, deviceScaleFactor = 1, mobile = false }, timeout.Token);
                var url = new Uri(Path.GetFullPath(Template)).AbsoluteUri
                    + "?t=" + Uri.EscapeDataString(title) + "&img=" + Uri.EscapeDataString(new Uri(blockFile).AbsoluteUri);
                await LoadAsync(cdp, url, timeout.Token);
                // the block is a separate file: wait until it decoded, or the card would render without it
                await cdp.EvaluateAsync("document.getElementById('i').decode().then(() => true)", timeout.Token);
                rendered.Add((card.Name + ".png", await cdp.ScreenshotAsync(0, 0, 1200, 630, timeout.Token)));
                Console.WriteLine($"  {card.Name}: \"{title}\", block {box.GetProperty("w")}x{box.GetProperty("h")} css px");
            }

            // only now, with every card rendered, replace the committed ones
            Directory.CreateDirectory(OutputDirectory);
            foreach (var (file, png) in rendered) await File.WriteAllBytesAsync(Path.Combine(OutputDirectory, file), png, timeout.Token);
            return rendered.Count;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new CardException("Chrome did not finish within 3 minutes.");
        }
        finally
        {
            if (chrome is not null) await chrome.DisposeAsync();
            work.Delete(recursive: true);
        }
    }

    /// <summary>Navigates and waits until the page and its fonts are ready and two frames have been drawn.</summary>
    /// <param name="cdp">The page's DevTools connection.</param>
    /// <param name="url">The address to load.</param>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns>A task that completes when the page is ready to capture.</returns>
    /// <exception cref="CardException">The navigation failed (a file that is not there).</exception>
    /// <remarks>Fonts load late from file:// addresses; capturing before <c>document.fonts.ready</c> catches the
    /// fallback font, and before two animation frames a layout that is not painted yet.</remarks>
    static async Task LoadAsync(Cdp cdp, string url, CancellationToken ct)
    {
        // Mark the current window first: right after a navigation the previous document can still answer
        // readyState "complete", and a capture would then take the old page. A new document has a new window.
        await cdp.EvaluateAsync("window.__asmfCardOld = true", ct);
        var nav = await cdp.SendAsync("Page.navigate", new { url }, ct);
        if (nav.TryGetProperty("errorText", out var error)) throw new CardException($"{url}: {error.GetString()}");
        while (true)
        {
            try
            {
                if ((await cdp.EvaluateAsync("!window.__asmfCardOld && document.readyState === 'complete'", ct)).GetBoolean()) break;
            }
            catch (CardException)
            {
                // the old document's context was destroyed mid-call while the new one replaced it: ask again
            }
            await Task.Delay(50, ct);
        }
        await cdp.EvaluateAsync("document.fonts.ready.then(() => true)", ct);
        await cdp.EvaluateAsync("new Promise(r => requestAnimationFrame(() => requestAnimationFrame(() => r(true))))", ct);
    }
}

/// <summary>A headless Chrome with a throw-away profile, and the DevTools connection to its page.</summary>
sealed class HeadlessChrome : IAsyncDisposable
{
    readonly Process _process;
    readonly DirectoryInfo _profile;

    /// <summary>The DevTools connection to Chrome's page.</summary>
    public Cdp Page { get; }

    HeadlessChrome(Process process, DirectoryInfo profile, Cdp page) => (_process, _profile, Page) = (process, profile, page);

    /// <summary>Starts Chrome and connects to its first page.</summary>
    /// <param name="executable">The Chrome or Chromium executable.</param>
    /// <param name="ct">Cancels the start.</param>
    /// <returns>The running browser.</returns>
    /// <exception cref="CardException">Chrome did not start, or did not open its DevTools port within 20 seconds.</exception>
    /// <remarks>
    /// The port is 0, so Chrome picks a free one and writes it to DevToolsActivePort in the profile: no clash with
    /// another Chrome being debugged on this machine. --allow-file-access-from-files lets the built pages and the
    /// template load their fonts from file:// addresses, which Chrome otherwise refuses.
    /// </remarks>
    public static async Task<HeadlessChrome> StartAsync(string executable, CancellationToken ct)
    {
        var profile = Directory.CreateTempSubdirectory("asmf-cards-chrome-");
        var psi = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true,
            ArgumentList = { "--headless=new", "--remote-debugging-port=0", $"--user-data-dir={profile.FullName}",
                             "--allow-file-access-from-files", "--hide-scrollbars", "--no-first-run", "--no-default-browser-check",
                             "--window-size=1280,900", "about:blank" },
        };
        Process process;
        try { process = Process.Start(psi) ?? throw new CardException($"{executable} did not start."); }
        catch (System.ComponentModel.Win32Exception e) { profile.Delete(true); throw new CardException($"{executable}: {e.Message}"); }
        // Chrome logs to both streams; read them so a full pipe can never block it
        process.ErrorDataReceived += (_, _) => { };
        process.OutputDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        try
        {
            var portFile = Path.Combine(profile.FullName, "DevToolsActivePort");
            var deadline = DateTime.UtcNow.AddSeconds(20);
            string? port = null;
            while (port is null)
            {
                if (DateTime.UtcNow > deadline || process.HasExited) throw new CardException($"{executable} did not open its DevTools port.");
                try { if (File.Exists(portFile)) port = File.ReadLines(portFile).FirstOrDefault(); }
                catch (IOException) { /* Chrome is still writing it */ }
                if (port is null) await Task.Delay(100, ct);
            }
            using var http = new HttpClient();
            using var list = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json/list", ct));
            var target = list.RootElement.EnumerateArray().First(t => t.GetProperty("type").GetString() == "page");
            var page = await Cdp.ConnectAsync(new Uri(target.GetProperty("webSocketDebuggerUrl").GetString()!), ct);
            await page.SendAsync("Page.enable", null, ct);
            return new HeadlessChrome(process, profile, page);
        }
        catch
        {
            // a half-started Chrome must not outlive the run, nor leave its profile behind
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            process.WaitForExit(5000);
            try { profile.Delete(true); } catch (IOException) { }
            throw;
        }
    }

    /// <summary>Closes the connection, ends Chrome and deletes its profile.</summary>
    /// <returns>A task that completes when Chrome has exited.</returns>
    /// <remarks>Chrome holds files in the profile until it exits, so the profile is deleted after the wait; a file
    /// still locked then stays in the temp folder rather than failing a run whose cards were written.</remarks>
    public async ValueTask DisposeAsync()
    {
        await Page.DisposeAsync();
        // HasExited and Kill throw InvalidOperationException when the process is already gone: nothing to end then
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        using (var wait = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            try { await _process.WaitForExitAsync(wait.Token); } catch (OperationCanceledException) { }
        }
        try { _profile.Delete(true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        _process.Dispose();
    }
}

/// <summary>A minimal DevTools-protocol client: one command at a time, answers matched by id, events skipped.</summary>
sealed class Cdp : IAsyncDisposable
{
    readonly ClientWebSocket _ws = new();
    int _id;

    Cdp() { }

    /// <summary>Connects to a target's DevTools websocket.</summary>
    /// <param name="url">The target's webSocketDebuggerUrl.</param>
    /// <param name="ct">Cancels the connection.</param>
    /// <returns>The connected client.</returns>
    public static async Task<Cdp> ConnectAsync(Uri url, CancellationToken ct)
    {
        var cdp = new Cdp();
        cdp._ws.Options.KeepAliveInterval = TimeSpan.Zero;
        await cdp._ws.ConnectAsync(url, ct);
        return cdp;
    }

    /// <summary>Sends a command and waits for its answer.</summary>
    /// <param name="method">The DevTools method, e.g. Page.navigate.</param>
    /// <param name="parameters">Its parameters, serialized as JSON; null for none.</param>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns>The answer's result object.</returns>
    /// <exception cref="CardException">Chrome answered with an error.</exception>
    public async Task<JsonElement> SendAsync(string method, object? parameters, CancellationToken ct)
    {
        var id = ++_id;
        var json = JsonSerializer.Serialize(new { id, method, @params = parameters ?? new { } });
        await _ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, endOfMessage: true, ct);
        while (true)
        {
            using var doc = JsonDocument.Parse(await ReceiveAsync(ct));
            var root = doc.RootElement;
            // events carry no id; an answer with another id belongs to nothing we wait for
            if (!root.TryGetProperty("id", out var answered) || answered.GetInt32() != id) continue;
            if (root.TryGetProperty("error", out var error)) throw new CardException($"{method}: {error}");
            return root.GetProperty("result").Clone();
        }
    }

    /// <summary>Evaluates an expression in the page, awaiting it if it is a promise.</summary>
    /// <param name="expression">The JavaScript expression.</param>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns>The value, as JSON.</returns>
    /// <exception cref="CardException">The expression threw.</exception>
    public async Task<JsonElement> EvaluateAsync(string expression, CancellationToken ct)
    {
        var result = await SendAsync("Runtime.evaluate", new { expression, returnByValue = true, awaitPromise = true }, ct);
        if (result.TryGetProperty("exceptionDetails", out var ex)) throw new CardException($"the page's script failed: {ex}");
        return result.GetProperty("result").TryGetProperty("value", out var value) ? value.Clone() : default;
    }

    /// <summary>Captures a rectangle of the page as PNG, at the page's device scale factor.</summary>
    /// <param name="x">Left edge, document CSS pixels.</param>
    /// <param name="y">Top edge, document CSS pixels.</param>
    /// <param name="width">Width, CSS pixels.</param>
    /// <param name="height">Height, CSS pixels.</param>
    /// <param name="ct">Cancels the capture.</param>
    /// <returns>The PNG's bytes.</returns>
    /// <remarks>captureBeyondViewport, so a block below the first screen is captured without scrolling.</remarks>
    public async Task<byte[]> ScreenshotAsync(double x, double y, double width, double height, CancellationToken ct)
    {
        var shot = await SendAsync("Page.captureScreenshot", new
        {
            format = "png", captureBeyondViewport = true,
            clip = new { x, y, width, height, scale = 1 },
        }, ct);
        return Convert.FromBase64String(shot.GetProperty("data").GetString()!);
    }

    /// <summary>Reads one whole message: a screenshot arrives in many frames.</summary>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The message's text.</returns>
    /// <exception cref="CardException">Chrome closed the connection.</exception>
    async Task<string> ReceiveAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var r = await _ws.ReceiveAsync(buffer, ct);
            if (r.MessageType == WebSocketMessageType.Close) throw new CardException("Chrome closed the DevTools connection.");
            message.Write(buffer, 0, r.Count);
            if (r.EndOfMessage) return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
        }
    }

    /// <summary>Closes the websocket, if it is still open.</summary>
    /// <returns>A task that completes when closed.</returns>
    public async ValueTask DisposeAsync()
    {
        if (_ws.State == WebSocketState.Open)
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); }
            catch (WebSocketException) { /* Chrome may already be gone */ }
        }
        _ws.Dispose();
    }
}
