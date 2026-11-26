# Link-preview cards

What X, LinkedIn, Slack, Discord, WhatsApp and the rest draw when someone shares a link to the foundation.
Nothing here is published or read by the build: each PNG is rendered by hand and committed, and the GitHub
ones are uploaded in GitHub's settings. The owner chose every design on 2026-11-26.

| File | What it is | Where it goes |
|---|---|---|
| `card.html` | The website's card, "The listing": the logo, "Hand-written x86-64 assembly." and KvAsm's AES-NI key hash (`server/src/aeshash.asm`, lines 64-76, unchanged), colored by the site's own highlighter | Rendered to `../assets/card.png`, which every page names in `og:image` (`../layout.html`) |
| `repo.html` | The repositories' social previews, one family: the logo, the repository's name, one line, the address | Rendered to `repos/<Repository>.png`, uploaded in each repository's Settings > General > Social preview |
| `avatar.png` | The organization's avatar, made crop-safe | Uploaded in the organization's Settings > General > Profile picture |

Each HTML file's header holds the exact headless Chrome command that renders it. Pass
`--allow-file-access-from-files`: without it Chrome refuses the `@font-face` files from a `file://` page and
the card silently falls back to system fonts.

## How X draws a link

Measured on 2026-11-26 from X's public embed data for the owner's reply (post 2125807823246209028) and the
pages' own tags, except the cache, which is X's documented behavior and was not measured here.

- **A large card** (`twitter:card` = `summary_large_image`) is a 1.91:1 frame. X center-crops whatever image
  the page names to that frame, and lays the page's `og:title` over the bottom-left corner in a dark pill
  (up to about 70% of the width for a long title, in the last ~70 px of a 1200x630 card). Nothing that must
  be read may sit there.
- **The organization page** (github.com/AsmFoundation) cannot carry a card of its own: GitHub names the
  avatar, at `?s=280`, as its image. X keeps its own 280x280 copy and stretches its middle 52.4% (the
  1.91:1 band) to the card's width, so that link is always blurry. `avatar.png` is the owner's logo
  (`logo-transparent.png`, outside this repository) scaled so its content fills 44% of the square's height,
  centered on white: the whole A and wordmark sit inside the band, at the price of a smaller mark everywhere
  on GitHub. GitHub accepts it at 1254x1254 and serves it scaled.
- **X caches a link's card** for about a week, keyed by the exact address (X's documentation; not measured).
  A changed image can therefore take that long to reach posts of an address X has already read.
- **A reply to someone else's post cannot be edited** on X, Premium or not: the owner's reply carries
  `edit_control.is_edit_eligible: false` inside its one-hour window. To change such a reply's card, post it
  again.
