# AsmFoundation

The foundation's own repository. It holds the source of the website at
**[asmfoundation.github.io](https://asmfoundation.github.io/)**: the home page, the projects' datasheets, the
evidence, the map, the docs hub, the getting-started session and the foundation's history. It will also hold
the benchmarks project (see [Planned](#planned)).

AsmFoundation builds **FOSAS, Free Open Source Assembly Software**: the services clouds run on, hand-written
in x86-64 assembly and released under Apache-2.0, with every speed claim linked to the run that measured it.

## The repositories

| Repository | What it is | State |
|---|---|---|
| [KvAsm](https://github.com/AsmFoundation/KvAsm) | A gRPC key-value server in assembly | released 0.3.0 |
| [AsmSdk](https://github.com/AsmFoundation/AsmSdk) | Assembly projects on MSBuild and NuGet | released 0.3.0 |
| [GrpcAsm](https://github.com/AsmFoundation/GrpcAsm) | A gRPC server and client in assembly | in development |
| [HttpAsm](https://github.com/AsmFoundation/HttpAsm) | HTTP/1.1, /2 and /3 from the RFCs | in development |
| [AsmClaudeMarketplace](https://github.com/AsmFoundation/AsmClaudeMarketplace) | Claude Code plugins for this ecosystem | asmsdk plugin 0.3.0 |
| [.github](https://github.com/AsmFoundation/.github) | The organization's profile, contributing guide and security policy | |
| AsmFoundation | This repository: the website, and later the benchmarks | |
| [asmfoundation.github.io](https://github.com/AsmFoundation/asmfoundation.github.io) | The built website. Generated: never edit it | |

Each project with documentation of its own publishes it at `asmfoundation.github.io/<Repository>/`
(AsmSdk does today). GitHub serves those addresses from the project's repository, so the foundation's site
must never use a top-level folder named after a repository.

## The website

### Build it

You need the .NET 10 SDK. From the repository's root:

```sh
dotnet run site/build.cs                  # builds into _site/
dotnet run site/build.cs -- --offline     # without network: the About log lists only the milestones
dotnet run site/build.cs -- --out <dir>   # somewhere else (the folder is deleted and recreated)
```

Pages link to each other relatively, so `_site/index.html` opens from disk. A static file server shows it
exactly as GitHub does, including the 404 page, which uses root-relative links.

The About log lists every commit of the organization's public repositories, read from the GitHub API at
build time. Unauthenticated, the API allows 60 requests an hour, enough for a few builds; set
`ASMF_GITHUB_TOKEN` to a token to raise that. The variable has its own name on purpose: a stale `GH_TOKEN`
in a shell must not break a build.

### Change it

| To change | Edit |
|---|---|
| A figure on a datasheet | `site/data/projects.json` |
| A chart on the Evidence page | `site/data/evidence.json` |
| A room on the Map | `site/data/services.json` |
| The Docs table of contents | `site/data/docs.json` |
| A milestone in the About log | `site/data/milestones.json` |
| A section of Get started | `site/get-started/*.md`, one file per section |
| A page's text | `site/pages/*.html` |
| The shell around every page | `site/layout.html` |
| Styles and scripts | `site/assets/` |

The generator, `site/build.cs`, documents every format it reads. Its rules, which a build enforces:

- **A page** starts with its front matter in an HTML comment (`title`, `path`, `nav`, ...). A marker
  `<!-- @name -->` in its body places a generated part: `projects`, `evidence`, `docs`, `map`,
  `get-started` or `history`.
- **A Get started section** is a Markdown file with a `title` (and optionally a `group`) in its front matter.
  Each `### Step` starts a step: its code goes on the left, everything else becomes the note on the right.
  In code, `$ ` marks a command, and a block marked `output` is output. The menu at the top right is built
  from the sections and steps, so adding a file is all a new section needs.
- **A milestone** that names a repository and a commit replaces that commit's row in the log; the build
  fails when no commit matches, so a typo cannot drop one silently.

The owner's design rules for the site:

- **One core everywhere.** Every performance figure gives its one-core number next to any other.
- **Every figure names its source**, and the Evidence page shows where assembly loses as well as where it
  wins.
- **White paper, ink type.** The logo's mint and teal are ink, bars and small blocks, never a background.
  Archivo, expanded, for headings; Martian Mono only for code and data.
- **The home page is about assembly language itself**: the x86-64 opcode table becomes the seal, by one of
  three animations, a different one on every load.

### Publish it

[`.github/workflows/site.yml`](.github/workflows/site.yml) builds the site on every push to `main`, by hand,
and every night (the history changes when any repository does). It pushes the result to
`AsmFoundation/asmfoundation.github.io` with a deploy key that can write to that repository and nothing
else: its private half is this repository's `PAGES_DEPLOY_KEY` secret. GitHub Pages serves that
repository's `main` branch.

## Planned

**The benchmarks project**: the same workloads written in other languages, each at its best and fairest,
measured against the assembly, with every result on the Evidence page. It lives here, as a folder of this
repository, not as a repository of its own. Its workloads, languages and rules are still to be decided.

## License

[Apache-2.0](LICENSE), like every AsmFoundation project. The fonts in `site/assets/fonts/` are Archivo and
Martian Mono, under the SIL Open Font License 1.1 (their licence files sit next to them).
