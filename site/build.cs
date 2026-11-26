#!/usr/bin/env dotnet
#:property TargetFramework=net10.0
#:property Nullable=enable
#:property ImplicitUsings=enable
#:property PublishAot=false
#:property InvariantGlobalization=true
#:package Markdig@1.6.0
#:package Jint@4.18.1

// The AsmFoundation website generator: renders site/ into a folder of static files for GitHub Pages.
//
// Usage, from the repository root:
//   dotnet run site/build.cs                    build into _site/
//   dotnet run site/build.cs -- --out <dir>     build into <dir> (deleted and recreated)
//   dotnet run site/build.cs -- --offline       skip the GitHub API: the About log lists only the milestones
//
// Inputs, all under site/:
//   layout.html        the shell every page shares: head, navigation, footer. {{placeholders}} are filled per page.
//   pages/*.html       one page each. A leading <!-- key: value --> block is its front matter; every
//                      <!-- @part --> marker in its body is replaced by a generated part (see Parts).
//   data/*.json        the facts the generated parts are drawn from: one place to change a figure.
//   get-started/*.md   one section of the Get started page each, in file-name order (see Parts.GetStarted).
//   highlight/         highlight.js and the foundation's own grammars, run at build time to color every code
//                      block (see Highlighter); nothing in it is published.
//   assets/            copied as is. assets/card.png is the link-preview image every page names in og:image.
//   root/              copied as is to the site's root, not under assets/: favicon.ico and apple-touch-icon.png,
//                      which unfurlers and iPhones request at the root whatever the pages link. A name the build
//                      also writes (index.html, sitemap.xml ...) stops the build.
//   card/              the source of assets/card.png (card.html, with the command that renders it), of the
//                      repositories' social previews (repo.html, rendered into repos/) and the organization's
//                      crop-safe avatar (avatar.png). Not read by the build and not published: the PNGs are
//                      rendered by hand and committed, and the GitHub ones are uploaded in GitHub's settings.
// The About log also lists every commit and tag of the organization's public repositories, read from
// the GitHub API at build time. ASMF_GITHUB_TOKEN, when set, raises the API's rate limit; CI passes its
// GITHUB_TOKEN there. A dedicated variable on purpose: a stale GH_TOKEN in a shell must not break builds.

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jint;
using Jint.Native;
using Jint.Runtime;
using Markdig;
using Markdig.Extensions.Yaml;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

return await Site.RunAsync(args);

/// <summary>
/// A problem in the site's sources (missing front matter, a malformed data file, an unknown
/// <c>&lt;!-- @part --&gt;</c> marker) or in reading its history from GitHub. The generator prints
/// the message and exits with code 1. Nothing is half-written: every page is rendered in memory
/// before the output folder is touched.
/// </summary>
/// <param name="message">What is wrong and, where possible, the file and the fix.</param>
/// <param name="inner">The exception that caused it, when there is one.</param>
sealed class BuildException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>What one run of the generator builds, and from where.</summary>
/// <param name="SiteDirectory">The <c>site/</c> folder: layout.html, pages/, data/, get-started/, assets/ and root/.</param>
/// <param name="OutputDirectory">Where the finished site goes. It is deleted and recreated on every build, so
/// <see cref="Parse"/> refuses any folder that contains the sources.</param>
/// <param name="Offline">True to skip the GitHub API; the About log then lists only the curated milestones.
/// Use it without network, never for a published build.</param>
sealed record BuildOptions(string SiteDirectory, string OutputDirectory, bool Offline)
{
    /// <summary>Reads the command line and locates the sources from the script's own folder.</summary>
    /// <param name="args">The arguments after <c>--</c> on the <c>dotnet run</c> command line.</param>
    /// <returns>The options, with absolute paths.</returns>
    /// <exception cref="BuildException">The site folder has no layout.html, an argument is unknown, <c>--out</c> has no
    /// folder, or the output folder is the site folder or contains it (the build would delete its own sources).</exception>
    public static BuildOptions Parse(string[] args)
    {
        // A file-based app knows its source folder; fall back to ./site for hosts that do not say.
        var site = Path.GetFullPath(AppContext.GetData("EntryPointFileDirectoryPath") as string
                                    ?? Path.Combine(Directory.GetCurrentDirectory(), "site"));
        if (!File.Exists(Path.Combine(site, "layout.html")))
            throw new BuildException($"{site} has no layout.html: run `dotnet run site/build.cs` from the repository root.");
        var output = Path.Combine(Path.GetDirectoryName(site)!, "_site");
        var offline = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out" when i + 1 < args.Length: output = Path.GetFullPath(args[++i]); break;
                case "--out": throw new BuildException("--out needs a folder.");
                case "--offline": offline = true; break;
                default: throw new BuildException($"Unknown argument '{args[i]}'. The options are --out <dir> and --offline.");
            }
        }
        // The output folder is deleted before writing: refuse one that would take the sources with it.
        var outDir = Path.TrimEndingDirectorySeparator(output);
        var siteDir = Path.TrimEndingDirectorySeparator(site);
        if (string.Equals(siteDir, outDir, StringComparison.OrdinalIgnoreCase)
            || siteDir.StartsWith(outDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new BuildException($"--out {output} contains the site's sources; give the output a folder of its own.");
        return new BuildOptions(site, output, offline);
    }
}

/// <summary>The generator's entry point and the steps of one build.</summary>
static class Site
{
    /// <summary>The GitHub organization whose projects, history and links the site describes.</summary>
    public const string Org = "AsmFoundation";

    /// <summary>The site's public address, for the sitemap and canonical links. Pages link to each other
    /// relatively, so a build also works from a file:// folder or a preview host.</summary>
    public const string BaseUrl = "https://asmfoundation.github.io/";

    /// <summary>Builds the site and reports the outcome on the console.</summary>
    /// <param name="args">The command-line arguments (see <see cref="BuildOptions.Parse"/>).</param>
    /// <returns>The process exit code: 0 when the site was written, 1 when a <see cref="BuildException"/> stopped it.</returns>
    /// <remarks>Only <see cref="BuildException"/> is reported as a clean error; anything else is a bug in
    /// the generator and keeps its stack trace.</remarks>
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var options = BuildOptions.Parse(args);
            var pages = await BuildAsync(options);
            Console.WriteLine($"Built {pages} pages into {options.OutputDirectory}{(options.Offline ? " (offline: no commit history)" : "")}");
            return 0;
        }
        catch (BuildException e)
        {
            Console.Error.WriteLine($"error: {e.Message}");
            return 1;
        }
    }

    /// <summary>Renders every page in memory, then replaces the output folder with the result, the assets and the
    /// root files.</summary>
    /// <param name="o">What to build and where.</param>
    /// <returns>The number of HTML pages written.</returns>
    /// <exception cref="BuildException">Any source problem, a GitHub API failure when not offline, a missing
    /// <c>site/root</c>, or a root file with the name of a generated one.</exception>
    /// <remarks>The output folder is deleted only after every page rendered, so a failed build leaves the previous
    /// output in place.</remarks>
    public static async Task<int> BuildAsync(BuildOptions o)
    {
        var data = new DataSet(Path.Combine(o.SiteDirectory, "data"));
        var history = o.Offline ? null : await GitHubHistory.FetchAsync(CancellationToken.None);
        var parts = new Parts(o, data, history);
        var layout = File.ReadAllText(Path.Combine(o.SiteDirectory, "layout.html"));

        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);  // output-relative path -> content
        foreach (var path in Directory.GetFiles(Path.Combine(o.SiteDirectory, "pages"), "*.html").Order(StringComparer.Ordinal))
        {
            var page = PageSource.Load(path);
            if (!files.TryAdd(page.OutputPath, Layout.Render(layout, page, parts, Path.Combine(o.SiteDirectory, "assets"))))
                throw new BuildException($"{page.Name}: another page already writes {page.OutputPath}.");
        }
        files["sitemap.xml"] = Sitemap(files.Keys);
        files[".nojekyll"] = "";   // GitHub Pages serves the files as they are, without running Jekyll
        // the Architecture drawing as an image of its own: the GitHub organization page embeds it from this address
        files["architecture/drawing.svg"] = parts.ArchitectureDrawing();

        // Files that only work at the site's root: unfurlers (Slack among them) and iPhones ask for /favicon.ico
        // and /apple-touch-icon.png there, whatever the pages' <link> tags say. Checked here, before the output
        // folder is deleted, so a missing folder or a clash with a generated file keeps the previous output.
        var root = Path.Combine(o.SiteDirectory, "root");
        if (!Directory.Exists(root)) throw new BuildException($"{root} does not exist: it holds favicon.ico and apple-touch-icon.png.");
        var rootFiles = Directory.GetFiles(root);
        foreach (var file in rootFiles)
            if (files.ContainsKey(Path.GetFileName(file)))
                throw new BuildException($"site/root/{Path.GetFileName(file)}: the build already writes a file of that name.");

        if (Directory.Exists(o.OutputDirectory)) Directory.Delete(o.OutputDirectory, recursive: true);
        foreach (var (rel, content) in files)
        {
            var target = Path.Combine(o.OutputDirectory, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        CopyDirectory(Path.Combine(o.SiteDirectory, "assets"), Path.Combine(o.OutputDirectory, "assets"));
        foreach (var file in rootFiles) File.Copy(file, Path.Combine(o.OutputDirectory, Path.GetFileName(file)), overwrite: false);
        return files.Keys.Count(k => k.EndsWith(".html", StringComparison.Ordinal));
    }

    /// <summary>The sitemap search engines read: every page except the 404 page, at its public address.</summary>
    /// <param name="paths">The output-relative paths of everything written.</param>
    /// <returns>The sitemap XML.</returns>
    static string Sitemap(IEnumerable<string> paths)
    {
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">\n");
        foreach (var p in paths.Where(p => p.EndsWith("index.html", StringComparison.Ordinal)))
            sb.Append("  <url><loc>").Append(BaseUrl).Append(p[..^"index.html".Length]).Append("</loc></url>\n");
        return sb.Append("</urlset>\n").ToString();
    }

    /// <summary>Copies a folder tree file by file, creating folders as needed.</summary>
    /// <param name="from">The source folder; it must exist.</param>
    /// <param name="to">The target folder.</param>
    /// <exception cref="BuildException">The source folder does not exist.</exception>
    static void CopyDirectory(string from, string to)
    {
        if (!Directory.Exists(from)) throw new BuildException($"{from} does not exist.");
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}

/// <summary>One file of site/pages: its front matter and its body.</summary>
/// <param name="Name">The file name, for error messages.</param>
/// <param name="Meta">The front matter, by key: <c>title</c> (required), <c>title-full</c>, <c>description</c>,
/// <c>path</c> (required: "" for the home page, "projects/" for a folder page, "404.html" for a file),
/// <c>nav</c> (which navigation item is current), <c>scripts</c> (asset scripts, space-separated),
/// <c>body</c> (the body's class), <c>root: absolute</c> (for pages served at any depth, like 404.html), and
/// <c>card</c> with <c>card-alt</c> (the page's own link-preview card under assets/, and what it shows; without
/// them the page shows the site's card).</param>
/// <param name="Body">The HTML after the front matter.</param>
sealed record PageSource(string Name, IReadOnlyDictionary<string, string> Meta, string Body)
{
    /// <summary>The page's own title, shown in the browser tab as "Title - AsmFoundation".</summary>
    /// <exception cref="BuildException">The front matter has no <c>title</c>.</exception>
    public string Title => Meta.TryGetValue("title", out var t) && t.Length > 0 ? t : throw new BuildException($"{Name}: the front matter has no title.");

    /// <summary>Where the page is written, relative to the output folder: a folder path gets index.html.</summary>
    /// <exception cref="BuildException">The front matter has no <c>path</c>, or the path leaves the site.</exception>
    public string OutputPath
    {
        get
        {
            if (!Meta.TryGetValue("path", out var p)) throw new BuildException($"{Name}: the front matter has no path (use \"path:\" with nothing after it for the home page).");
            if (p.StartsWith('/') || p.Contains("..", StringComparison.Ordinal)) throw new BuildException($"{Name}: path \"{p}\" must be relative to the site's root.");
            return p.Length == 0 ? "index.html" : p.EndsWith('/') ? p + "index.html" : p;
        }
    }

    /// <summary>The prefix that reaches the site's root from this page: "" at the top, "../" one folder down,
    /// or "/" for a page served at any depth.</summary>
    /// <remarks>Relative links keep a build browsable from disk; the 404 page cannot use them, because GitHub
    /// serves it for every missing address, at whatever depth that address has.</remarks>
    public string Root => Meta.TryGetValue("root", out var r) && r == "absolute"
        ? "/"
        : string.Concat(Enumerable.Repeat("../", OutputPath.Count(c => c == '/')));

    /// <summary>Reads a page file: a leading <c>&lt;!-- --&gt;</c> comment of <c>key: value</c> lines, then the body.</summary>
    /// <param name="file">The page's path.</param>
    /// <returns>The parsed page.</returns>
    /// <exception cref="BuildException">The file does not start with the front-matter comment, the comment is not
    /// closed, or a line in it is not <c>key: value</c>.</exception>
    public static PageSource Load(string file)
    {
        var name = "pages/" + Path.GetFileName(file);
        var text = File.ReadAllText(file);
        if (!text.StartsWith("<!--", StringComparison.Ordinal)) throw new BuildException($"{name}: must start with its front matter in a <!-- --> comment.");
        var end = text.IndexOf("-->", StringComparison.Ordinal);
        if (end < 0) throw new BuildException($"{name}: the front-matter comment is not closed.");
        var meta = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text[4..end].Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var colon = line.IndexOf(':');
            if (colon <= 0) throw new BuildException($"{name}: front-matter line \"{line}\" is not key: value.");
            meta[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        return new PageSource(name, meta, text[(end + 3)..].TrimStart('\r', '\n'));
    }
}

/// <summary>Fills the shared layout for one page.</summary>
static class Layout
{
    /// <summary>The navigation, in order. Hrefs without a scheme are site pages, resolved against the page's root.</summary>
    static readonly (string Text, string Href)[] NavItems =
    [
        ("Projects", "projects/"), ("Docs", "docs/"), ("Benchmarks", "benchmarks/"), ("Architecture", "architecture/"),
        ("Get started", "get-started/"), ("About", "about/"), ("GitHub", "https://github.com/AsmFoundation"),
    ];

    /// <summary>Matches a generated-part marker, <c>&lt;!-- @name --&gt;</c>.</summary>
    static readonly Regex Marker = new(@"<!--\s*@([a-z-]+)\s*-->", RegexOptions.Compiled);

    /// <summary>Matches a placeholder that nothing filled, which is a typo in a page or the layout.</summary>
    static readonly Regex Leftover = new(@"\{\{[a-z-]+\}\}", RegexOptions.Compiled);

    /// <summary>The site's link-preview card, under assets/: every page that names none of its own shows it.</summary>
    const string DefaultCard = "card.png";

    /// <summary>What the site's card shows, for readers who cannot see it.</summary>
    const string DefaultCardAlt = "AsmFoundation: hand-written x86-64 assembly, the services clouds run on, free under Apache-2.0. Beside it, KvAsm's AES-NI key hash in NASM: ten AES rounds.";

    /// <summary>Renders one page: its generated parts, then the layout around it, then the root prefix everywhere.</summary>
    /// <param name="layout">The text of layout.html.</param>
    /// <param name="page">The page.</param>
    /// <param name="parts">The generated parts the page's markers name.</param>
    /// <param name="assets">The site's assets folder, where the page's link-preview card must be.</param>
    /// <returns>The finished HTML document.</returns>
    /// <exception cref="BuildException">A marker names no part, a part fails, a placeholder is left unfilled, or
    /// the page's card is missing, outside assets/, or not a PNG.</exception>
    /// <remarks><c>{{root}}</c> is replaced last, so page bodies and generated parts can use it in their own links.</remarks>
    public static string Render(string layout, PageSource page, Parts parts, string assets)
    {
        var body = Marker.Replace(page.Body, m => parts.Render(m.Groups[1].Value, page));
        // The page's link-preview card (front matter "card", a PNG under assets/), else the site's. Its size comes
        // from the file, so og:image:width and og:image:height cannot drift from what pages.cs rendered.
        var card = page.Meta.GetValueOrDefault("card") ?? DefaultCard;
        var cardAlt = page.Meta.GetValueOrDefault("card-alt") ?? (card == DefaultCard ? DefaultCardAlt : "");
        if (cardAlt.Length == 0) throw new BuildException($"{page.Name}: card {card} needs a card-alt line saying what it shows.");
        var (cardWidth, cardHeight) = PngSize(assets, card, page.Name);
        var title = page.Meta.TryGetValue("title-full", out var full) ? full : $"{page.Title} - {Site.Org}";
        var scripts = string.Concat((page.Meta.GetValueOrDefault("scripts") ?? "")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => $"<script src=\"{{{{root}}}}assets/{H.E(s)}\"></script>\n"));
        // only folder pages have a public address of their own; the 404 page is served in place of missing ones
        var canonical = page.OutputPath.EndsWith("index.html", StringComparison.Ordinal) ? Site.BaseUrl + page.OutputPath[..^"index.html".Length] : "";
        var html = layout
            .Replace("{{title}}", H.E(title))
            .Replace("{{description}}", H.E(page.Meta.GetValueOrDefault("description") ?? ""))
            .Replace("{{canonical}}", canonical.Length == 0 ? "" : $"<link rel=\"canonical\" href=\"{canonical}\">")
            // og:url names the same address as the canonical link, so a share of /projects/ and one of
            // /projects/index.html count as one page; the 404 page, which has no address, gets none
            .Replace("{{og-url}}", canonical.Length == 0 ? "" : $"<meta property=\"og:url\" content=\"{canonical}\">")
            // link-preview crawlers resolve nothing relative: the card's og:image needs the absolute address
            .Replace("{{base}}", Site.BaseUrl)
            .Replace("{{card}}", H.E(card))
            .Replace("{{card-width}}", cardWidth.ToString(CultureInfo.InvariantCulture))
            .Replace("{{card-height}}", cardHeight.ToString(CultureInfo.InvariantCulture))
            .Replace("{{card-alt}}", H.E(cardAlt))
            .Replace("{{body}}", H.E(page.Meta.GetValueOrDefault("body") ?? ""))
            .Replace("{{nav}}", Nav(page))
            .Replace("{{content}}", body)
            .Replace("{{scripts}}", scripts)
            .Replace("{{root}}", page.Root);
        var left = Leftover.Match(html);
        if (left.Success) throw new BuildException($"{page.Name}: nothing fills the placeholder {left.Value}.");
        return html;
    }

    /// <summary>Reads a link-preview card's pixel size from its PNG header (the IHDR chunk).</summary>
    /// <param name="assets">The site's assets folder.</param>
    /// <param name="card">The card, relative to assets/ ("cards/benchmarks.png").</param>
    /// <param name="pageName">The page that names it, for the error message.</param>
    /// <returns>The width and height in pixels.</returns>
    /// <exception cref="BuildException">The card leaves assets/, is missing, or is not a PNG: the page would
    /// promise crawlers an image they cannot show.</exception>
    static (int Width, int Height) PngSize(string assets, string card, string pageName)
    {
        if (card.StartsWith('/') || card.Contains("..", StringComparison.Ordinal) || !card.EndsWith(".png", StringComparison.Ordinal))
            throw new BuildException($"{pageName}: card {card} must be a .png under site/assets/.");
        var path = Path.Combine(assets, card);
        if (!File.Exists(path)) throw new BuildException($"{pageName}: card {card} is not in site/assets/ (render it with dotnet run site/card/pages.cs).");
        Span<byte> head = stackalloc byte[24];
        using (var f = File.OpenRead(path))
            if (f.ReadAtLeast(head, 24, throwOnEndOfStream: false) < 24) throw new BuildException($"{pageName}: card {card} is not a PNG.");
        // the 8-byte signature, the IHDR chunk's length and type, then its width and height, big-endian
        if (head[0] != 0x89 || head[1] != (byte)'P' || head[2] != (byte)'N' || head[3] != (byte)'G')
            throw new BuildException($"{pageName}: card {card} is not a PNG.");
        return (System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(head[16..]),
                System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(head[20..]));
    }

    /// <summary>The navigation links, with the page's own item marked current.</summary>
    /// <param name="page">The page being rendered; its <c>nav</c> front matter names the current item.</param>
    /// <returns>The links' HTML.</returns>
    static string Nav(PageSource page)
    {
        var current = page.Meta.GetValueOrDefault("nav");
        return string.Concat(NavItems.Select(n =>
        {
            var cur = n.Text == current;
            var href = n.Href.Contains("://", StringComparison.Ordinal) ? n.Href : "{{root}}" + n.Href;
            return $"<a class=\"l{(cur ? " cur" : "")}\" href=\"{href}\"{(cur ? " aria-current=\"page\"" : "")}>{H.E(n.Text)}</a>";
        }));
    }
}

/// <summary>Small helpers for writing HTML and SVG.</summary>
static class H
{
    /// <summary>Encodes text for element content or a double-quoted attribute value.</summary>
    /// <param name="s">The text; null is treated as empty.</param>
    /// <returns>The encoded text.</returns>
    public static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    /// <summary>A URL-fragment id from a title: lower-case ASCII letters and digits, joined by single hyphens.</summary>
    /// <param name="s">The title.</param>
    /// <returns>The id; "section" when the title has no letters or digits.</returns>
    /// <remarks>Two titles that differ only in punctuation get the same id; callers that need unique ids check.</remarks>
    public static string Slug(string s)
    {
        var slug = Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return slug.Length == 0 ? "section" : slug;
    }

    /// <summary>Formats a coordinate or a percentage for HTML and SVG: invariant culture, at most two decimals.</summary>
    /// <param name="v">The number.</param>
    /// <returns>The number's text.</returns>
    public static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}

/// <summary>The JSON files of site/data, each parsed once on first use.</summary>
/// <param name="directory">The data folder.</param>
sealed class DataSet(string directory)
{
    readonly Dictionary<string, JsonElement> cache = new(StringComparer.Ordinal);

    /// <summary>The root element of <c>data/&lt;name&gt;.json</c>.</summary>
    /// <param name="name">The file name without its extension.</param>
    /// <returns>The parsed document's root, detached from its document.</returns>
    /// <exception cref="BuildException">The file does not exist or is not valid JSON (comments and trailing commas are allowed).</exception>
    public JsonElement this[string name]
    {
        get
        {
            if (cache.TryGetValue(name, out var root)) return root;
            var path = Path.Combine(directory, name + ".json");
            if (!File.Exists(path)) throw new BuildException($"data/{name}.json does not exist.");
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                return cache[name] = doc.RootElement.Clone();
            }
            catch (JsonException e)
            {
                throw new BuildException($"data/{name}.json: {e.Message}", e);
            }
        }
    }
}

/// <summary>Typed reads of JSON data, each failing with the place and the property it expected.</summary>
static class J
{
    /// <summary>A required string property.</summary>
    /// <param name="e">The object.</param>
    /// <param name="name">The property.</param>
    /// <param name="where">The file and item, for the error message.</param>
    /// <returns>The string.</returns>
    /// <exception cref="BuildException">The property is missing or not a string.</exception>
    public static string Str(this JsonElement e, string name, string where) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()! : throw new BuildException($"{where}: \"{name}\" must be a string.");

    /// <summary>An optional string property.</summary>
    /// <param name="e">The object.</param>
    /// <param name="name">The property.</param>
    /// <returns>The string, or null when the property is missing or not a string.</returns>
    public static string? OptStr(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>A required number property.</summary>
    /// <param name="e">The object.</param>
    /// <param name="name">The property.</param>
    /// <param name="where">The file and item, for the error message.</param>
    /// <returns>The number.</returns>
    /// <exception cref="BuildException">The property is missing or not a number.</exception>
    public static double Num(this JsonElement e, string name, string where) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble() : throw new BuildException($"{where}: \"{name}\" must be a number.");

    /// <summary>An optional boolean property that defaults to false.</summary>
    /// <param name="e">The object.</param>
    /// <param name="name">The property.</param>
    /// <returns>True only when the property is the JSON literal true.</returns>
    public static bool Flag(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>A required array property.</summary>
    /// <param name="e">The object.</param>
    /// <param name="name">The property.</param>
    /// <param name="where">The file and item, for the error message.</param>
    /// <returns>The array's items.</returns>
    /// <exception cref="BuildException">The property is missing or not an array.</exception>
    public static IEnumerable<JsonElement> Arr(this JsonElement e, string name, string where) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray() : throw new BuildException($"{where}: \"{name}\" must be an array.");

    /// <summary>The strings of a required array property.</summary>
    /// <param name="e">The object.</param>
    /// <param name="name">The property.</param>
    /// <param name="where">The file and item, for the error message.</param>
    /// <returns>The strings, in order.</returns>
    /// <exception cref="BuildException">The property is missing, not an array, or holds something other than strings.</exception>
    public static IEnumerable<string> Strings(this JsonElement e, string name, string where) =>
        e.Arr(name, where).Select(v => v.ValueKind == JsonValueKind.String ? v.GetString()! : throw new BuildException($"{where}: every item of \"{name}\" must be a string."));
}

/// <summary>The generated parts a page can place with a <c>&lt;!-- @name --&gt;</c> marker.</summary>
/// <param name="o">The build's options (for the get-started folder).</param>
/// <param name="data">The site's data files.</param>
/// <param name="history">The organization's commit history, or null when building offline.</param>
sealed class Parts(BuildOptions o, DataSet data, OrgHistory? history)
{
    /// <summary>Colors code; created on first use, so a page without code never starts the JavaScript engine.</summary>
    readonly Lazy<Highlighter> highlighter = new(() => new Highlighter(Path.Combine(o.SiteDirectory, "highlight")));

    /// <summary>A project's, service's or room's state, as the data files spell it, and its words.</summary>
    static readonly Dictionary<string, string> Statuses = new(StringComparer.Ordinal)
    {
        ["rel"] = "Released", ["dev"] = "In development", ["des"] = "In design", ["none"] = "Not started",
    };

    /// <summary>Renders the part a marker names.</summary>
    /// <param name="name">The marker's name: projects, benchmarks, docs, architecture, get-started or history.</param>
    /// <param name="page">The page holding the marker, for error messages.</param>
    /// <returns>The part's HTML.</returns>
    /// <exception cref="BuildException">No part has that name, or the part's data is malformed.</exception>
    public string Render(string name, PageSource page) => name switch
    {
        "projects" => Projects(),
        "benchmarks" => Benchmarks(),
        "docs" => Docs(),
        "architecture" => Architecture(),
        "get-started" => GetStarted(),
        "history" => Log(),
        _ => throw new BuildException($"{page.Name}: no generated part is called @{name}."),
    };

    /// <summary>Checks a status code from a data file.</summary>
    /// <param name="status">The code.</param>
    /// <param name="where">The file and item, for the error message.</param>
    /// <param name="allowed">The codes valid in this place.</param>
    /// <returns>The code, unchanged.</returns>
    /// <exception cref="BuildException">The code is not one of <paramref name="allowed"/>.</exception>
    static string Status(string status, string where, params string[] allowed) =>
        allowed.Contains(status) ? status : throw new BuildException($"{where}: status \"{status}\" must be one of {string.Join(", ", allowed)}.");

    /// <summary>The key of status colours, shared by the Architecture page and any page that marks states.</summary>
    /// <returns>The key's HTML.</returns>
    static string Key() =>
        "<div class=\"key\" aria-label=\"Key\">" +
        string.Concat(Statuses.Select(s => $"<span><i class=\"k {s.Key}\"></i>{s.Value}</span>")) + "</div>";

    // ------------------------------------------------------------------------------------------ Projects

    /// <summary>The Projects page: one datasheet per project in data/projects.json, in file order.</summary>
    /// <returns>The datasheets' HTML.</returns>
    /// <exception cref="BuildException">A project lacks a required field or has an unknown status.</exception>
    /// <remarks>Every performance figure in a sheet carries its one-core number: the owner's rule, "1 core everywhere".
    /// The data file holds the figures, so the rule is kept there.</remarks>
    string Projects()
    {
        var sb = new StringBuilder("<div class=\"sheets\">");
        foreach (var p in data["projects"].Arr("projects", "projects.json"))
        {
            var name = p.Str("name", "projects.json");
            var where = $"projects.json: {name}";
            var status = Status(p.Str("status", where), where, "rel", "dev", "des");
            sb.Append($"<article class=\"sheet {status}\" id=\"{H.Slug(name)}\"><header><h2>{H.E(name)}</h2>")
              .Append($"<span class=\"stamp\">{H.E(p.Str("stamp", where))}<small>{H.E(p.Str("date", where))}</small></span></header>")
              .Append($"<p class=\"sub\">{H.E(p.Str("summary", where))}</p><div class=\"cols\"><div><h3>Features</h3><ul>");
            foreach (var f in p.Strings("features", where)) sb.Append($"<li>{H.E(f)}</li>");
            sb.Append($"</ul></div><div><h3>{H.E(p.Str("factsTitle", where))}</h3><table class=\"spec\">");
            foreach (var row in p.Arr("facts", where))
            {
                var cells = row.ValueKind == JsonValueKind.Array ? row.EnumerateArray().Select(c => c.GetString() ?? "").ToArray() : [];
                if (cells.Length != 2) throw new BuildException($"{where}: every fact is [\"label\", \"value\"].");
                sb.Append($"<tr><td>{H.E(cells[0])}</td><td>{H.E(cells[1])}</td></tr>");
            }
            sb.Append($"</table></div></div><div class=\"order\"><span>{H.E(p.Str("getLabel", where))}</span>");
            // each line is colored on its own, in the project's getLanguage (cli, json, claude ...); "text" stays plain
            var getLanguage = p.OptStr("getLanguage") ?? "text";
            foreach (var line in p.Strings("get", where))
                sb.Append($"<code>{(getLanguage == "text" ? H.E(line) : highlighter.Value.Html(line, getLanguage, where))}</code>");
            sb.Append("</div><p class=\"links\">");
            foreach (var link in p.Arr("links", where))
                sb.Append($"<a href=\"{H.E(link.Str("url", where))}\">{H.E(link.Str("text", where))}</a>");
            sb.Append("</p></article>");
        }
        return sb.Append("</div>").ToString();
    }

    // ------------------------------------------------------------------------------------------ Benchmarks

    /// <summary>The Benchmarks page: one bar chart per entry of data/benchmarks.json, with a table view and its source.</summary>
    /// <returns>The charts' HTML.</returns>
    /// <exception cref="BuildException">A chart lacks a field, or a value lies outside 0..max (the bar would overflow its track).</exception>
    /// <remarks>Bars are plain HTML, so the charts read without JavaScript; site.js only adds tooltips. Our bar and the
    /// rows flagged <c>show</c> carry their value; every other value is in the tooltip and the table, so no chart
    /// prints a number on every bar.</remarks>
    string Benchmarks()
    {
        var sb = new StringBuilder("<div class=\"charts\">");
        foreach (var c in data["benchmarks"].Arr("charts", "benchmarks.json"))
        {
            var title = c.Str("title", "benchmarks.json");
            var where = $"benchmarks.json: {title}";
            var max = c.Num("max", where);
            if (max <= 0) throw new BuildException($"{where}: max must be above zero.");
            sb.Append($"<section class=\"chart\" id=\"{H.Slug(title)}\"><h2>{H.E(title)}</h2><p class=\"q\">{H.E(c.Str("question", where))}</p><div class=\"bars\">");
            var table = new StringBuilder();
            foreach (var r in c.Arr("rows", where))
            {
                var label = r.Str("label", where);
                var value = r.Num("value", where);
                var text = r.Str("text", where);
                var ours = r.Flag("ours");
                if (value < 0 || value > max) throw new BuildException($"{where}: {label}'s value {value} lies outside 0..{max}.");
                var pct = H.N(100 * value / max);
                var val = ours || r.Flag("show") ? $"<span class=\"val\" style=\"left:calc({pct}% + 8px)\">{H.E(text)}</span>" : "";
                sb.Append($"<div class=\"lab{(ours ? " hl" : "")}\">{H.E(label)}</div><div class=\"track\" data-tip=\"{H.E(label)}: {H.E(text)}\">")
                  .Append($"<div class=\"bar{(ours ? " hl" : "")}\" style=\"width:{pct}%\"></div>{val}</div>");
                table.Append($"<tr><td>{H.E(label)}</td><td>{H.E(text)}</td></tr>");
            }
            sb.Append("</div><div class=\"axis\" aria-hidden=\"true\"><div></div><div>");
            foreach (var t in c.Arr("ticks", where))
            {
                if (t.ValueKind != JsonValueKind.Array || t.GetArrayLength() != 2) throw new BuildException($"{where}: every tick is [value, \"label\"].");
                sb.Append($"<span style=\"left:{H.N(100 * t[0].GetDouble() / max)}%\">{H.E(t[1].GetString())}</span>");
            }
            sb.Append("</div></div>");
            if (c.OptStr("note") is { } note) sb.Append($"<p class=\"note\">{H.E(note)}</p>");
            sb.Append($"<p class=\"src\">Source: <a href=\"{H.E(c.Str("url", where))}\">{H.E(c.Str("source", where))}</a></p>")
              .Append($"<details><summary>Show as a table</summary><table>{table}</table></details></section>");
        }
        return sb.Append("</div>").ToString();
    }

    // ------------------------------------------------------------------------------------------ Docs

    /// <summary>The Docs page: a table of contents with one section per project, from data/docs.json.</summary>
    /// <returns>The table of contents' HTML.</returns>
    /// <exception cref="BuildException">A book or entry lacks a field.</exception>
    string Docs()
    {
        var sb = new StringBuilder("<div class=\"toc\">");
        foreach (var b in data["docs"].Arr("books", "docs.json"))
        {
            var name = b.Str("name", "docs.json");
            var where = $"docs.json: {name}";
            var home = b.Str("home", where);
            sb.Append($"<section class=\"book\" id=\"{H.Slug(name)}\"><h2>{H.E(name)} <small><a href=\"{H.E(b.Str("url", where))}\">{H.E(home)}</a></small></h2><ol>");
            foreach (var e in b.Arr("entries", where))
                sb.Append($"<li><a href=\"{H.E(e.Str("url", where))}\">{H.E(e.Str("title", where))}</a><span>{H.E(e.Str("kind", where))}</span></li>");
            sb.Append("</ol></section>");
        }
        return sb.Append("</div>").ToString();
    }

    // ------------------------------------------------------------------------------------------ Architecture

    /// <summary>The Architecture page: the drawing (<see cref="ArchitectureSvg"/>), the key of status colours, a readout
    /// that names the room under the pointer, and the date the drawing describes.</summary>
    /// <returns>The page part's HTML.</returns>
    /// <exception cref="BuildException">data/architecture.json lacks a field or uses an unknown status.</exception>
    string Architecture() =>
        Key() + $"<div class=\"elev-wrap\">{ArchitectureSvg(standalone: false)}</div>" +
        "<div class=\"readout2\" id=\"ro\" aria-live=\"polite\"><b>Point at a room</b><span>Each one says what is there today.</span></div>" +
        $"<p class=\"drawn\">Drawn on {H.E(data["architecture"].Str("updated", "architecture.json"))}. A dashed outline marks a service nobody has started.</p>";

    /// <summary>The same drawing as a file of its own, which the build writes to <c>architecture/drawing.svg</c>. The GitHub
    /// organization page (AsmFoundation/.github, <c>profile/README.md</c>) shows that file above its project table, so the
    /// page and the website draw from one source: data/architecture.json.</summary>
    /// <returns>A complete SVG document: its font embedded, its own key, on a white card.</returns>
    /// <exception cref="BuildException">data/architecture.json lacks a field or uses an unknown status, or the Archivo font
    /// file is missing from site/assets/fonts.</exception>
    /// <remarks>GitHub shows an SVG as an image, and an image cannot load fonts or pages' styles: hence the embedded font
    /// and the key drawn inside. The white card keeps the ink lines legible on GitHub's dark theme.</remarks>
    public string ArchitectureDrawing() => ArchitectureSvg(standalone: true);

    /// <summary>The foundation drawn as a building section, from data/architecture.json. The services are the rooms on top,
    /// eight to a floor; the protocols, the build and the machine code are the floors beneath.</summary>
    /// <param name="standalone">False for the website's page: the drawing sits in the page's HTML, uses the page's fonts, and
    /// each room is focusable with its description in <c>data-d</c> ("Name: text"), which site.js shows in the readout.
    /// True for <see cref="ArchitectureDrawing"/>: a self-contained document for use as an image elsewhere.</param>
    /// <returns>The drawing's SVG markup.</returns>
    /// <exception cref="BuildException">The data file lacks a field or uses an unknown status, or (standalone) the Archivo
    /// font file is missing.</exception>
    /// <remarks>The drawing grows a floor for every eight services. Both forms come from this one method on purpose: a
    /// change to the data changes the website and the GitHub page alike.</remarks>
    string ArchitectureSvg(bool standalone)
    {
        const double x0 = 178, x1 = 1200, roomH = 90, levelH = 92, slabH = 62, top = 10;
        const int perFloor = 8;
        var m = data["architecture"];
        const string where = "architecture.json";
        var services = m.Arr("services", where).ToList();
        var floors = (services.Count + perFloor - 1) / perFloor;
        var protoY = top + floors * roomH;
        var buildY = protoY + levelH;
        var ground = buildY + levelH;
        var height = ground + slabH + 34;
        var mid = (x0 + x1) / 2;
        var svg = new StringBuilder();
        if (standalone)
        {
            // a margin for the card and, above the drawing, a band for the key
            const double pad = 24, keyBand = 40;
            var font = Path.Combine(o.SiteDirectory, "assets", "fonts", "archivo-latin.woff2");
            if (!File.Exists(font)) throw new BuildException($"{font} is missing: the standalone drawing embeds it.");
            svg.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"{H.N(-pad)} {H.N(-pad - keyBand)} {H.N(1210 + 2 * pad)} {H.N(height + 2 * pad + keyBand)}\" width=\"{H.N(1210 + 2 * pad)}\" height=\"{H.N(height + 2 * pad + keyBand)}\" role=\"img\" aria-labelledby=\"t\">")
               .Append($"<title id=\"t\">{H.E(m.Str("summary", where))}</title>")
               .Append($"<style>@font-face{{font-family:\"Archivo\";src:url(data:font/woff2;base64,{Convert.ToBase64String(File.ReadAllBytes(font))}) format(\"woff2\");font-weight:100 900;font-stretch:62% 125%}}</style>")
               .Append($"<rect x=\"{H.N(-pad)}\" y=\"{H.N(-pad - keyBand)}\" width=\"{H.N(1210 + 2 * pad)}\" height=\"{H.N(height + 2 * pad + keyBand)}\" rx=\"24\" fill=\"#fff\"/>");
        }
        else
        {
            svg.Append($"<svg class=\"elev\" viewBox=\"0 0 1210 {H.N(height)}\" role=\"img\" aria-label=\"{H.E(m.Str("summary", where))}\">");
        }
        svg.Append("<defs><pattern id=\"hatch\" width=\"9\" height=\"9\" patternUnits=\"userSpaceOnUse\" patternTransform=\"rotate(45)\"><line x1=\"0\" y1=\"0\" x2=\"0\" y2=\"9\" stroke=\"#062f2e\" stroke-width=\"1.3\" opacity=\".55\"/></pattern>")
           .Append("<pattern id=\"earth\" width=\"14\" height=\"14\" patternUnits=\"userSpaceOnUse\" patternTransform=\"rotate(-45)\"><line x1=\"0\" y1=\"0\" x2=\"0\" y2=\"14\" stroke=\"#b9c2c7\" stroke-width=\"1\"/></pattern></defs>")
           .Append("<g font-family=\"Archivo, sans-serif\">");
        if (standalone)
        {
            // the key the page shows in HTML, drawn into the image: an image cannot show the page around it
            var keys = new[] { ("Released", "fill=\"#062f2e\""), ("In development", "fill=\"#06706d\""), ("In design", "fill=\"#d99a3c\""), ("Not started", "fill=\"#fff\" stroke=\"#c3cbd0\" stroke-dasharray=\"3 2\"") };
            var kx = 0.0;
            foreach (var (label, paint) in keys)
            {
                svg.Append($"<rect x=\"{H.N(kx)}\" y=\"-28\" width=\"24\" height=\"8\" {paint}/><text x=\"{H.N(kx + 32)}\" y=\"-19\" font-size=\"14\" fill=\"#5b656b\">{label}</text>");
                kx += 32 + label.Length * 7.6 + 26;   // Archivo at 14 px averages under 7.6 px a character
            }
        }
        var width = (x1 - x0) / perFloor;
        for (var i = 0; i < services.Count; i++)
        {
            // the first eight services sit on the lowest floor, next to the protocols; later ones stack upward
            var floor = floors - 1 - i / perFloor;
            var s = services[i];
            var name = s.Str("name", where);
            var status = Status(s.OptStr("status") ?? "none", $"{where}: {name}", "rel", "dev", "des", "none");
            var project = s.OptStr("project");
            var desc = status == "none" ? $"{name}: not started." : $"{name}: {project}. {s.Str("text", $"{where}: {name}")}";
            svg.Append(Room(x0 + (i % perFloor) * width, top + floor * roomH, width, roomH, name, status, project ?? "", false, desc, !standalone));
        }
        var protocols = m.Arr("protocols", where).ToList();
        var pw = (x1 - x0) / Math.Max(1, protocols.Count);
        for (var i = 0; i < protocols.Count; i++)
        {
            var p = protocols[i];
            var name = p.Str("name", where);
            svg.Append(Room(x0 + i * pw, protoY, pw, levelH, name, Status(p.Str("status", where), $"{where}: {name}", "rel", "dev", "des"), p.Str("sub", where), true, $"{name}: {p.Str("text", where)}", !standalone));
        }
        if (!m.TryGetProperty("build", out var b)) throw new BuildException($"{where}: \"build\" must be an object.");
        var buildName = b.Str("name", where);
        svg.Append(Room(x0, buildY, x1 - x0, levelH, buildName, Status(b.Str("status", where), $"{where}: {buildName}", "rel", "dev", "des"), b.Str("sub", where), true, $"{buildName}: {b.Str("text", where)}", !standalone));
        svg.Append($"<rect x=\"0\" y=\"{H.N(ground)}\" width=\"1210\" height=\"{H.N(slabH + 34)}\" fill=\"url(#earth)\"/><line x1=\"0\" y1=\"{H.N(ground)}\" x2=\"1210\" y2=\"{H.N(ground)}\" stroke=\"#062f2e\" stroke-width=\"2\"/>")
           .Append($"<rect x=\"{H.N(x0 - 14)}\" y=\"{H.N(ground)}\" width=\"{H.N(x1 - x0 + 28)}\" height=\"{H.N(slabH)}\" fill=\"#fff\"/>")
           .Append($"<rect x=\"{H.N(x0 - 14)}\" y=\"{H.N(ground)}\" width=\"{H.N(x1 - x0 + 28)}\" height=\"{H.N(slabH)}\" fill=\"url(#hatch)\" stroke=\"#062f2e\" stroke-width=\"2\"/>")
           .Append($"<rect x=\"{H.N(mid - 250)}\" y=\"{H.N(ground + 18)}\" width=\"500\" height=\"26\" fill=\"#fff\"/>")
           .Append($"<text x=\"{H.N(mid)}\" y=\"{H.N(ground + 36)}\" text-anchor=\"middle\" font-size=\"15\" font-weight=\"800\" fill=\"#062f2e\" style=\"font-stretch:116%\">{H.E(m.Str("foundation", where))}</text>");
        // a row header per level: the name at the band's middle, a bracket spanning the band
        foreach (var (label, y0, y1) in new[] { ("Services", top, protoY), ("Protocols", protoY, buildY), ("Build", buildY, ground), ("Foundation", ground, ground + slabH) })
            svg.Append($"<text x=\"0\" y=\"{H.N((y0 + y1) / 2 + 5)}\" font-size=\"14\" font-weight=\"700\" fill=\"#062f2e\">{label}</text>")
               .Append($"<path d=\"M{H.N(x0 - 40)} {H.N(y0 + 4)} h10 V{H.N(y1 - 4)} h-10\" fill=\"none\" stroke=\"#b9c2c7\" stroke-width=\"1.5\"/>");
        return svg.Append("</g></svg>").ToString();
    }

    /// <summary>One room of the Architecture page's drawing; its status sets the outline, a small mint block marks a released room.</summary>
    /// <param name="x">The room's left edge.</param>
    /// <param name="y">The room's top edge.</param>
    /// <param name="w">The room's width.</param>
    /// <param name="h">The room's height.</param>
    /// <param name="name">The room's name, drawn at its centre.</param>
    /// <param name="status">rel, dev, des or none.</param>
    /// <param name="sub">A second line under the name, or empty.</param>
    /// <param name="big">True for the wide floors (protocols, build): a larger, expanded name.</param>
    /// <param name="desc">The readout text, "Name: what is there".</param>
    /// <param name="interactive">True on the website's page: the room takes focus and carries <paramref name="desc"/> for the
    /// readout. False in the standalone drawing, which is shown as an image and cannot take focus.</param>
    /// <returns>The room's SVG group.</returns>
    static string Room(double x, double y, double w, double h, string name, string status, string sub, bool big, string desc, bool interactive)
    {
        var stroke = status switch
        {
            "rel" => "stroke=\"#062f2e\" stroke-width=\"2.5\"",
            "dev" => "stroke=\"#06706d\" stroke-width=\"2.5\"",
            "des" => "stroke=\"#d99a3c\" stroke-width=\"2\" stroke-dasharray=\"7 5\"",
            _ => "stroke=\"#c3cbd0\" stroke-width=\"1.2\" stroke-dasharray=\"3 4\"",
        };
        var color = status == "none" ? "#8f9aa0" : "#062f2e";
        var (size, weight) = big ? (22.0, 800) : (14.5, status == "none" ? 600 : 700);
        var cx = x + w / 2;
        var focus = interactive ? $" class=\"room\" tabindex=\"0\" data-d=\"{H.E(desc)}\"" : "";
        var sb = new StringBuilder($"<g{focus}><rect x=\"{H.N(x + 1.5)}\" y=\"{H.N(y + 1.5)}\" width=\"{H.N(w - 3)}\" height=\"{H.N(h - 3)}\" fill=\"#fff\" {stroke}/>");
        if (status == "rel") sb.Append($"<rect x=\"{H.N(x + 10)}\" y=\"{H.N(y + 10)}\" width=\"11\" height=\"11\" fill=\"#3eea8c\"/>");
        sb.Append($"<text x=\"{H.N(cx)}\" y=\"{H.N(y + h / 2 + (sub.Length > 0 ? -6 : 5))}\" text-anchor=\"middle\" font-size=\"{H.N(size)}\" font-weight=\"{weight}\" fill=\"{color}\" style=\"font-stretch:{(big ? "116%" : "100%")}\">{H.E(name)}</text>");
        if (sub.Length > 0)
            sb.Append($"<text x=\"{H.N(cx)}\" y=\"{H.N(y + h / 2 + (big ? 17 : 14))}\" text-anchor=\"middle\" font-size=\"{(big ? 13 : 12)}\" fill=\"#5b656b\">{H.E(sub)}</text>");
        return sb.Append("</g>").ToString();
    }

    // ------------------------------------------------------------------------------------------ Get started

    /// <summary>One file of site/get-started: a section of the annotated session.</summary>
    /// <param name="Id">The section's fragment id, from its title.</param>
    /// <param name="Title">The section's title, from its front matter.</param>
    /// <param name="Group">The menu group it belongs to, or null.</param>
    sealed record GsSection(string Id, string Title, string? Group)
    {
        /// <summary>HTML before the first step: the section's introduction.</summary>
        public StringBuilder Intro { get; } = new();

        /// <summary>The section's steps, in order.</summary>
        public List<GsStep> Steps { get; } = [];
    }

    /// <summary>One <c>###</c> step of a section: its code on the left, its note in the margin.</summary>
    /// <param name="Id">The step's fragment id: the section's id, two hyphens, the step's slug.</param>
    /// <param name="Text">The step's title as plain text, for the menu.</param>
    /// <param name="Html">The step's title as HTML, for its heading.</param>
    sealed record GsStep(string Id, string Text, string Html)
    {
        /// <summary>The step's code blocks.</summary>
        public StringBuilder Code { get; } = new();

        /// <summary>Everything else in the step: the margin note.</summary>
        public StringBuilder Note { get; } = new();
    }

    /// <summary>The Get started page: an annotated session from the Markdown files of site/get-started, with a menu of
    /// every section and step that stays at the top right while the reader scrolls.</summary>
    /// <returns>The session and its menu.</returns>
    /// <exception cref="BuildException">A file lacks a title, uses a heading other than <c>###</c>, has code before its first
    /// step, has no steps, or two sections or steps get the same id.</exception>
    /// <remarks>
    /// The format, chosen so the page can grow by adding files:
    /// <list type="bullet">
    /// <item>front matter: <c>title</c> (required) and <c>group</c> (optional; the menu groups sections once any has one);</item>
    /// <item>text before the first <c>###</c> is the section's introduction;</item>
    /// <item>each <c>### Step</c> starts a step: its fenced code goes left, everything else becomes the note on the right;</item>
    /// <item>in code, a line starting with <c>$ </c> is a command (the prompt is drawn, not copied), and a block whose info
    /// string is <c>output</c> is output (no Copy button); <c>title="file"</c> after the language names the file.</item>
    /// </list>
    /// </remarks>
    string GetStarted()
    {
        var pipeline = new MarkdownPipelineBuilder().UseYamlFrontMatter().Build();
        var sections = new List<GsSection>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(Path.Combine(o.SiteDirectory, "get-started"), "*.md").Order(StringComparer.Ordinal))
        {
            var name = "get-started/" + Path.GetFileName(file);
            var doc = Markdown.Parse(File.ReadAllText(file), pipeline);
            var meta = FrontMatter(doc);
            var title = meta.GetValueOrDefault("title") ?? throw new BuildException($"{name}: the front matter has no title.");
            var section = new GsSection(H.Slug(title), title, meta.GetValueOrDefault("group"));
            if (!ids.Add(section.Id)) throw new BuildException($"{name}: another section already has the id {section.Id}.");
            GsStep? step = null;
            foreach (var block in doc)
            {
                switch (block)
                {
                    case YamlFrontMatterBlock:
                        break;
                    case HeadingBlock { Level: 3 } h:
                        var text = PlainText(h.Inline);
                        step = new GsStep($"{section.Id}--{H.Slug(text)}", text, InlineHtml(h, pipeline));
                        if (!ids.Add(step.Id)) throw new BuildException($"{name}: two steps are called \"{text}\".");
                        section.Steps.Add(step);
                        break;
                    case HeadingBlock:
                        throw new BuildException($"{name}: only ### headings are allowed; the section's title comes from its front matter.");
                    case FencedCodeBlock when step is null:
                        throw new BuildException($"{name}: a code block comes before the first ### step.");
                    case FencedCodeBlock code:
                        step.Code.Append(CodeHtml(code, name));
                        break;
                    default:
                        (step is null ? section.Intro : step.Note).Append(BlockHtml(block, pipeline));
                        break;
                }
            }
            if (section.Steps.Count == 0) throw new BuildException($"{name}: has no ### steps.");
            sections.Add(section);
        }

        var body = new StringBuilder("<div class=\"gs-body\">");
        foreach (var s in sections)
        {
            body.Append($"<section class=\"gs-sec\" id=\"{s.Id}\"><h2>{H.E(s.Title)}</h2>");
            if (s.Intro.Length > 0) body.Append($"<div class=\"gs-intro\">{s.Intro}</div>");
            foreach (var st in s.Steps)
                body.Append($"<div class=\"step\" id=\"{st.Id}\"><h3>{st.Html}</h3><div class=\"c\">{st.Code}</div><div class=\"n\">{st.Note}</div></div>");
            body.Append("</section>");
        }
        body.Append("</div>");

        // the menu: sections with their steps; grouped once any section names a group
        var menu = new StringBuilder("<nav class=\"gs-menu\" aria-label=\"On this page\"><p class=\"gs-menu-t\">On this page</p><ol>");
        var grouped = sections.Any(s => s.Group is not null);
        foreach (var group in sections.GroupBy(s => grouped ? s.Group ?? "More" : ""))
        {
            if (grouped) menu.Append($"<li class=\"grp\"><span>{H.E(group.Key)}</span><ol>");
            foreach (var s in group)
            {
                menu.Append($"<li data-sec=\"{s.Id}\"><a href=\"#{s.Id}\">{H.E(s.Title)}</a><ol>");
                foreach (var st in s.Steps) menu.Append($"<li><a href=\"#{st.Id}\">{H.E(st.Text)}</a></li>");
                menu.Append("</ol></li>");
            }
            if (grouped) menu.Append("</ol></li>");
        }
        menu.Append("</ol></nav>");
        return $"<div class=\"gs\">{body}{menu}</div>";
    }

    /// <summary>Reads a Markdown file's YAML front matter as flat <c>key: value</c> pairs.</summary>
    /// <param name="doc">The parsed file.</param>
    /// <returns>The pairs; empty when the file has no front matter.</returns>
    /// <remarks>Only flat pairs are supported, which is all a section needs; quotes around a value are removed.</remarks>
    static Dictionary<string, string> FrontMatter(MarkdownDocument doc)
    {
        var meta = new Dictionary<string, string>(StringComparer.Ordinal);
        var block = doc.Descendants<YamlFrontMatterBlock>().FirstOrDefault();
        if (block is null) return meta;
        foreach (var raw in block.Lines.ToString().Split('\n'))
        {
            var line = raw.Trim();
            if (line is "---" or "..." || line.Length == 0) continue;
            var colon = line.IndexOf(':');
            if (colon > 0) meta[line[..colon].Trim()] = line[(colon + 1)..].Trim().Trim('"');
        }
        return meta;
    }

    /// <summary>The plain text of a run of inlines: literals and code spans, with emphasis and links unwrapped.</summary>
    /// <param name="inline">The inlines; null gives an empty string.</param>
    /// <returns>The text.</returns>
    static string PlainText(ContainerInline? inline)
    {
        if (inline is null) return "";
        var sb = new StringBuilder();
        foreach (var i in inline)
            sb.Append(i switch { LiteralInline l => l.Content.ToString(), CodeInline c => c.Content, ContainerInline c => PlainText(c), _ => "" });
        return sb.ToString();
    }

    /// <summary>A heading's inlines rendered as HTML, without the heading element around them.</summary>
    /// <param name="h">The heading.</param>
    /// <param name="pipeline">The Markdown pipeline, so extensions render as in the rest of the page.</param>
    /// <returns>The inline HTML.</returns>
    static string InlineHtml(HeadingBlock h, MarkdownPipeline pipeline)
    {
        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        pipeline.Setup(renderer);
        renderer.WriteLeafInline(h);
        writer.Flush();
        return writer.ToString();
    }

    /// <summary>One Markdown block rendered as HTML.</summary>
    /// <param name="block">The block.</param>
    /// <param name="pipeline">The Markdown pipeline.</param>
    /// <returns>The block's HTML.</returns>
    static string BlockHtml(Block block, MarkdownPipeline pipeline)
    {
        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        pipeline.Setup(renderer);
        renderer.Render(block);
        writer.Flush();
        return writer.ToString();
    }

    /// <summary>Matches <c>title="..."</c> in a fenced block's arguments.</summary>
    static readonly Regex CodeTitle = new("title=\"([^\"]*)\"", RegexOptions.Compiled);

    /// <summary>A fenced code block as the session shows it: colored code with a Copy button, or plain output.</summary>
    /// <param name="code">The block. Its info string names its language: any highlight.js language (sh, csharp,
    /// json, xml ...), or the foundation's nasm, cli and claude; <c>text</c> for prose and <c>output</c> for a
    /// program's output, which stay uncolored on purpose.</param>
    /// <param name="file">The file it came from, for error messages.</param>
    /// <returns>The block's HTML, with its file-name caption when it names one.</returns>
    /// <exception cref="BuildException">The block names no language (all code is colored, so an unmarked block is an
    /// error, not plain text), names one no grammar knows, or is an empty output block.</exception>
    /// <remarks>
    /// Lines starting with <c>$ </c> are commands: each is colored on its own as a command line (cli), whatever the
    /// block's language, and the lines between them are output, left plain.
    /// The text a Copy button copies is fixed at build time in <c>data-copy</c>: the commands without their prompts
    /// and without the output between them, or the whole block when it has no prompts.
    /// A <c>text</c> block wraps its lines, for prose such as a prompt to type.
    /// </remarks>
    string CodeHtml(FencedCodeBlock code, string file)
    {
        var lines = code.Lines.ToString().Replace("\r\n", "\n").Split('\n');
        var language = (code.Info ?? "").Trim();
        if (language.Length == 0)
            throw new BuildException($"{file}: a code block names no language; give it one (sh, csharp, json, xml, nasm, cli, claude ...), or text or output for blocks that are not code.");
        var output = language == "output";
        var prose = language == "text";
        if (output && lines.All(string.IsNullOrWhiteSpace)) throw new BuildException($"{file}: an empty output block.");
        var prompts = !output && lines.Any(l => l.StartsWith("$ ", StringComparison.Ordinal));
        var copy = new List<string>();
        string html;
        if (prompts)
        {
            var parts = new List<string>(lines.Length);
            foreach (var line in lines)
            {
                if (!line.StartsWith("$ ", StringComparison.Ordinal)) { parts.Add(H.E(line)); continue; }
                parts.Add("<span class=\"p\" aria-hidden=\"true\">$ </span>" + highlighter.Value.Html(line[2..], "cli", file));
                copy.Add(line[2..]);
            }
            html = string.Join("\n", parts);
        }
        else
        {
            copy.AddRange(lines);
            var text = string.Join("\n", lines);
            // the whole block at once, so a comment or string spanning lines is colored as one
            html = output || prose ? H.E(text) : highlighter.Value.Html(text, language, file);
        }
        var title = CodeTitle.Match(code.Arguments ?? "");
        var caption = title.Success ? $"<div class=\"ct\">{H.E(title.Groups[1].Value)}</div>" : "";
        return output
            ? $"{caption}<pre class=\"out\"><code>{html}</code></pre>"
            : $"{caption}<pre class=\"cmd{(prose ? " wrap" : "")}\" data-copy=\"{H.E(string.Join("\n", copy))}\"><code class=\"hljs\">{html}</code></pre>";
    }

    // ------------------------------------------------------------------------------------------ History

    /// <summary>One row of the About log: a commit, a milestone, or a commit that is a milestone.</summary>
    /// <param name="When">When it happened, for ordering.</param>
    /// <param name="Repo">The repository, or null for an event outside any repository.</param>
    /// <param name="Title">The commit's first line or the milestone's title.</param>
    /// <param name="Text">The milestone's description, or null.</param>
    /// <param name="Sha">The commit's id, or null.</param>
    /// <param name="Url">Where the row links: the commit, or the milestone's page.</param>
    /// <param name="Tags">The tags on the commit.</param>
    /// <param name="Milestone">True for curated milestones and tagged commits.</param>
    sealed record LogRow(DateTimeOffset When, string? Repo, string Title, string? Text, string? Sha, string? Url, IReadOnlyList<string> Tags, bool Milestone);

    /// <summary>The About log: every commit of every public repository, newest first, with the curated milestones of
    /// data/milestones.json and every tagged commit marked as milestones, in a scrolling box with filters.</summary>
    /// <returns>The log's filters, box and count.</returns>
    /// <exception cref="BuildException">A milestone lacks a field, or names a commit the history does not contain
    /// (a typo would otherwise drop it silently).</exception>
    /// <remarks>Offline, the log holds the milestones only and says so.</remarks>
    string Log()
    {
        const string where = "milestones.json";
        var rows = new List<LogRow>();
        var byCommit = new Dictionary<string, JsonElement>(StringComparer.Ordinal);   // "Repo/sha prefix" -> milestone
        foreach (var ms in data["milestones"].Arr("milestones", where))
        {
            var title = ms.Str("title", where);
            var date = DateTimeOffset.ParseExact(ms.Str("date", $"{where}: {title}"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
            var repo = ms.OptStr("repo");
            var commit = ms.OptStr("commit");
            if (history is not null && repo is not null && commit is not null)
            {
                byCommit[$"{repo}/{commit}"] = ms;
                continue;
            }
            // a milestone of its own: placed at the end of its day, above that day's commits
            var url = ms.OptStr("url") ?? (repo is not null && commit is not null ? $"https://github.com/{Site.Org}/{repo}/commit/{commit}" : null);
            rows.Add(new LogRow(date.AddDays(1).AddSeconds(-1), repo, title, ms.Str("text", $"{where}: {title}"), commit, url, [], true));
        }
        if (history is not null)
        {
            foreach (var c in history.Commits)
            {
                var tags = history.TagsBySha.TryGetValue(c.Sha, out var t) ? t : [];
                var ms = byCommit.FirstOrDefault(kv => kv.Key.StartsWith(c.Repo + "/", StringComparison.Ordinal) && c.Sha.StartsWith(kv.Key[(c.Repo.Length + 1)..], StringComparison.Ordinal));
                if (ms.Key is not null)
                {
                    byCommit.Remove(ms.Key);
                    var title = ms.Value.Str("title", where);
                    rows.Add(new LogRow(c.Date, c.Repo, title, ms.Value.Str("text", $"{where}: {title}"), c.Sha, c.Url, tags, true));
                }
                else
                {
                    rows.Add(new LogRow(c.Date, c.Repo, c.Title, null, c.Sha, c.Url, tags, tags.Count > 0));
                }
            }
            if (byCommit.Count > 0)
                throw new BuildException($"{where}: no commit matches {string.Join(", ", byCommit.Keys)}.");
        }
        rows.Sort((a, b) => b.When.CompareTo(a.When));

        var repos = rows.Where(r => r.Repo is not null).Select(r => r.Repo!).Distinct().Order(StringComparer.OrdinalIgnoreCase).ToList();
        var sb = new StringBuilder("<div class=\"log-tools\"><div role=\"group\" aria-label=\"Show\">")
            .Append($"<button type=\"button\" aria-pressed=\"true\" data-show=\"all\">{(history is null ? "Everything" : "Every commit")}</button>")
            .Append("<button type=\"button\" aria-pressed=\"false\" data-show=\"ms\">Milestones</button></div>")
            .Append("<div role=\"group\" aria-label=\"Repository\"><button type=\"button\" aria-pressed=\"true\" data-repo=\"\">All repositories</button>");
        foreach (var r in repos) sb.Append($"<button type=\"button\" aria-pressed=\"false\" data-repo=\"{H.E(r)}\">{H.E(r)}</button>");
        sb.Append("</div></div><div class=\"log-box\" tabindex=\"0\" aria-label=\"The foundation's history, newest first\"><ol class=\"log\">");
        foreach (var r in rows)
        {
            sb.Append($"<li{(r.Milestone ? " class=\"ms\"" : "")} data-repo=\"{H.E(r.Repo ?? "")}\"{(r.Milestone ? " data-ms=\"1\"" : "")}>")
              .Append($"<time datetime=\"{r.When:yyyy-MM-dd}\">{r.When:yyyy-MM-dd}</time><span class=\"repo\">{H.E(r.Repo ?? "")}</span><div class=\"what\">");
            sb.Append(r.Text is null ? $"<p class=\"t\" title=\"{H.E(r.Title)}\">{H.E(r.Title)}" : $"<p class=\"t\"><b>{H.E(r.Title)}</b>");
            foreach (var tag in r.Tags) sb.Append($" <span class=\"tag\">{H.E(tag)}</span>");
            sb.Append("</p>");
            if (r.Text is not null) sb.Append($"<p class=\"d\">{H.E(r.Text)}</p>");
            sb.Append("</div>");
            if (r.Url is not null) sb.Append($"<a class=\"sha\" href=\"{H.E(r.Url)}\">{H.E(r.Sha is null ? "read" : r.Sha[..Math.Min(7, r.Sha.Length)])}</a>");
            sb.Append("</li>");
        }
        sb.Append("</ol></div>");
        var commits = history?.Commits.Count ?? 0;
        var first = rows.Count > 0 ? rows[^1].When.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "";
        sb.Append(history is null
            ? "<p class=\"log-count\">Built offline: the commits are not listed, only the milestones.</p>"
            : $"<p class=\"log-count\">{commits:N0} commits in {repos.Count} repositories since {first}. Dates are UTC, as GitHub reports them. <span id=\"log-shown\"></span></p>");
        return sb.ToString();
    }
}

/// <summary>One commit of one of the organization's repositories, as the About log lists it.</summary>
/// <param name="Repo">The repository's name.</param>
/// <param name="Sha">The full commit id.</param>
/// <param name="Date">The author date: when the change was written, as <c>git log</c> shows it.</param>
/// <param name="Title">The first line of the commit message.</param>
/// <param name="Url">The commit's page on GitHub.</param>
sealed record Commit(string Repo, string Sha, DateTimeOffset Date, string Title, string Url);

/// <summary>Everything the About log needs from GitHub.</summary>
/// <param name="Commits">Every commit of every public repository, newest first.</param>
/// <param name="TagsBySha">Tag names by the full id of the commit each tag points at.</param>
sealed record OrgHistory(IReadOnlyList<Commit> Commits, IReadOnlyDictionary<string, List<string>> TagsBySha);

/// <summary>Reads the organization's public history from the GitHub REST API.</summary>
static class GitHubHistory
{
    /// <summary>Repositories left out of the log: the generated Pages repository holds only deploy commits.</summary>
    static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase) { "asmfoundation.github.io" };

    /// <summary>Reads every commit and tag of the organization's public, non-fork, non-archived repositories.</summary>
    /// <param name="ct">Cancels the requests.</param>
    /// <returns>The history, commits newest first.</returns>
    /// <exception cref="BuildException">The network failed, GitHub refused a request (the message says when the rate limit
    /// resets, and to set ASMF_GITHUB_TOKEN or build with --offline), or a reply was not the JSON expected.</exception>
    /// <remarks>An empty repository answers 409 to a commit listing and is skipped, so a newly created repository does
    /// not break the build.</remarks>
    public static async Task<OrgHistory> FetchAsync(CancellationToken ct)
    {
        using var http = new HttpClient { BaseAddress = new Uri("https://api.github.com/"), Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AsmFoundation-site-build");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        var token = Environment.GetEnvironmentVariable("ASMF_GITHUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(token)) http.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var commits = new List<Commit>();
        var tags = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        try
        {
            foreach (var repo in await PagesAsync(http, $"orgs/{Site.Org}/repos?type=public&per_page=100", ct))
            {
                var name = repo.GetProperty("name").GetString()!;
                if (Skip.Contains(name) || repo.GetProperty("fork").GetBoolean() || repo.GetProperty("archived").GetBoolean()) continue;
                foreach (var c in await PagesAsync(http, $"repos/{Site.Org}/{name}/commits?per_page=100", ct))
                {
                    var commit = c.GetProperty("commit");
                    var message = commit.GetProperty("message").GetString() ?? "";
                    var nl = message.IndexOf('\n');
                    commits.Add(new Commit(name, c.GetProperty("sha").GetString()!, commit.GetProperty("author").GetProperty("date").GetDateTimeOffset(),
                        (nl < 0 ? message : message[..nl]).Trim(), c.GetProperty("html_url").GetString()!));
                }
                foreach (var t in await PagesAsync(http, $"repos/{Site.Org}/{name}/tags?per_page=100", ct))
                {
                    var sha = t.GetProperty("commit").GetProperty("sha").GetString()!;
                    if (!tags.TryGetValue(sha, out var list)) tags[sha] = list = [];
                    list.Add(t.GetProperty("name").GetString()!);
                }
            }
        }
        catch (HttpRequestException e)
        {
            throw new BuildException($"could not reach the GitHub API ({e.Message}); build with --offline to skip the commit history.", e);
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or JsonException)
        {
            throw new BuildException($"the GitHub API answered with JSON this generator does not expect: {e.Message}", e);
        }
        commits.Sort((a, b) => b.Date.CompareTo(a.Date));
        return new OrgHistory(commits, tags);
    }

    /// <summary>Reads every page of a GitHub list endpoint, following the <c>Link: rel="next"</c> header.</summary>
    /// <param name="http">The client, with GitHub's headers set.</param>
    /// <param name="url">The first page's URL, relative to the API's base address.</param>
    /// <param name="ct">Cancels the requests.</param>
    /// <returns>Every item of every page; empty for an empty repository (409).</returns>
    /// <exception cref="BuildException">GitHub answered with an error other than 409.</exception>
    static async Task<List<JsonElement>> PagesAsync(HttpClient http, string url, CancellationToken ct)
    {
        var items = new List<JsonElement>();
        for (string? next = url; next is not null;)
        {
            using var response = await http.GetAsync(next, ct);
            if (response.StatusCode == HttpStatusCode.Conflict) return items;   // "Git Repository is empty"
            if (!response.IsSuccessStatusCode) throw new BuildException(Explain(response, next));
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            foreach (var item in doc.RootElement.EnumerateArray()) items.Add(item.Clone());
            next = NextPage(response);
        }
        return items;
    }

    /// <summary>The URL of the next page, from GitHub's <c>Link</c> header.</summary>
    /// <param name="response">The current page's response.</param>
    /// <returns>The next page's absolute URL, or null on the last page.</returns>
    static string? NextPage(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out var values)) return null;
        var m = Regex.Match(string.Join(",", values), "<([^>]+)>;\\s*rel=\"next\"");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>An error message for a refused request; for an exhausted rate limit it says when it resets and what to do.</summary>
    /// <param name="response">The error response.</param>
    /// <param name="url">The request's URL.</param>
    /// <returns>The message.</returns>
    static string Explain(HttpResponseMessage response, string url)
    {
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
            && response.Headers.TryGetValues("x-ratelimit-remaining", out var left) && left.FirstOrDefault() == "0")
        {
            var reset = response.Headers.TryGetValues("x-ratelimit-reset", out var r) && long.TryParse(r.FirstOrDefault(), out var s)
                ? DateTimeOffset.FromUnixTimeSeconds(s).ToString("HH:mm 'UTC'", CultureInfo.InvariantCulture) : "later";
            return $"GitHub's API rate limit is used up until {reset}. Set ASMF_GITHUB_TOKEN to a token, or build with --offline.";
        }
        return $"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase} for {url}.";
    }
}

/// <summary>
/// Colors code at build time. highlight.js (vendored in site/highlight/vendor, with its x86 grammar) and the
/// foundation's own grammars (site/highlight/asmfoundation.js: nasm, cli, claude) run inside Jint, a JavaScript
/// interpreter for .NET, so the pages ship colored HTML and no highlighting script.
/// </summary>
/// <remarks>
/// The output uses highlight.js's own token classes (hljs-keyword, hljs-string, ...), the ones AsmSdk's documentation
/// site colors, so one palette serves both sites. The engine gets no access to .NET or the disk: the scripts
/// receive code and return HTML, and a console that reports to the build. One engine serves a whole build;
/// it is not thread-safe, and the build is single-threaded.
/// </remarks>
sealed class Highlighter
{
    /// <summary>The scripts, in load order: the language files call <c>hljs.registerLanguage</c> on the core.</summary>
    static readonly string[] Scripts = ["vendor/highlight.min.js", "vendor/languages/x86asm.min.js", "asmfoundation.js"];

    readonly Engine engine;
    readonly JsValue highlight;
    readonly JsValue known;
    readonly List<string> warnings = [];

    /// <summary>Starts the engine and loads highlight.js and the grammars.</summary>
    /// <param name="directory">The site/highlight folder.</param>
    /// <exception cref="BuildException">A script is missing, or fails to load (a bad update of the vendored files).</exception>
    public Highlighter(string directory)
    {
        engine = new Engine(options => options.TimeoutInterval(TimeSpan.FromSeconds(60)));
        // highlight.js reports problems through console.warn and console.error: collect them, so the build can fail
        // on any instead of shipping code colored by a grammar that complained
        engine.SetValue("__warn", new Action<string>(warnings.Add));
        engine.Execute("var console = { log: function () {}, info: function () {}, "
                       + "warn: function () { __warn(Array.prototype.join.call(arguments, ' ')); }, "
                       + "error: function () { __warn(Array.prototype.join.call(arguments, ' ')); } };");
        foreach (var script in Scripts)
        {
            var path = Path.Combine(directory, script);
            if (!File.Exists(path)) throw new BuildException($"site/highlight/{script} does not exist.");
            try
            {
                engine.Execute(File.ReadAllText(path));
            }
            catch (Exception e) when (e is not BuildException)
            {
                throw new BuildException($"site/highlight/{script} failed to load: {e.Message}", e);
            }
        }
        highlight = engine.Evaluate("(function (code, language) { return hljs.highlight(code, { language: language, ignoreIllegals: true }).value; })");
        known = engine.Evaluate("(function (language) { return !!hljs.getLanguage(language); })");
    }

    /// <summary>Colors one piece of code.</summary>
    /// <param name="code">The code, as plain text.</param>
    /// <param name="language">A highlight.js language name or alias (sh, csharp, json, xml ...), or nasm, cli or claude.</param>
    /// <param name="where">The file or data item the code comes from, for error messages.</param>
    /// <returns>HTML: the code, escaped, with its tokens in <c>&lt;span class="hljs-..."&gt;</c>.</returns>
    /// <exception cref="BuildException">No grammar has that name, highlight.js threw, or it warned while coloring.</exception>
    public string Html(string code, string language, string where)
    {
        if (!engine.Invoke(known, language).AsBoolean())
            throw new BuildException($"{where}: no grammar is called \"{language}\". Use a highlight.js language (sh, csharp, json, xml ...), nasm, cli or claude; or text or output for blocks that are not code.");
        string html;
        try
        {
            html = engine.Invoke(highlight, code, language).AsString();
        }
        catch (JavaScriptException e)
        {
            throw new BuildException($"{where}: highlight.js failed on a {language} block: {e.Message}", e);
        }
        if (warnings.Count > 0)
        {
            var said = string.Join("; ", warnings);
            warnings.Clear();
            throw new BuildException($"{where}: highlight.js warned while coloring a {language} block: {said}");
        }
        return html;
    }
}
