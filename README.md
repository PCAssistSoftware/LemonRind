# Lemon Rind - Local AI Agent for Lemonade

### "All the zest, none of the cloud."

A local-first AI assistant backed by a locally-running [Lemonade Server](https://lemonade-server.ai/) - everything runs on your own machine, including the model itself. It comes in **three editions** that share one feature set:

| Edition | Folder | Stack | Runs on |
|---|---|---|---|
| **WPF** (the original) | `LemonRind_WPF\` | VB.NET, WPF, .NET 10 | Windows |
| **Avalonia** | `LemonRind_Avalonia\` | C#, Avalonia 12, .NET 10 | Windows, Linux, macOS (native desktop app) |
| **Blazor** | `LemonRind_Blazor\` | C#, Blazor Server, .NET 10 | Any OS - the app runs as a small web server and you use it in a browser |

This was built as a learning exercise and proof of concept - a way to explore agentic AI app design (tool calling, memory, context management, RAG) hands-on, not a polished commercial product. The WPF app came first; the Avalonia and Blazor editions were then ported from it, stage by stage, to see how the same design carries across UI frameworks and operating systems.

## Requirements

- A running [Lemonade Server](https://lemonade-server.ai/) instance (local or on your network), with at least one chat model downloaded
- **WPF**: Windows and the .NET 10 runtime
- **Avalonia / Blazor**: the .NET 10 SDK to build and run from source (or the runtime to run a published build) on Windows, Linux or macOS
- Optional, per module you want to use: a web search/read engine (self-hosted [SearXNG](https://docs.searxng.org/), or an API key for Jina, Tavily, or Firecrawl), an MCP server (e.g. Postmark for email - needs Node.js for `npx`-based servers)

## The three editions

Everything below under "Core chat experience" to "Data & security" applies to **all three** unless noted. The solution file `LemonRind.slnx` opens all three projects together in Visual Studio; `LemonRind.CrossPlatform.slnx` holds just Avalonia and Blazor, for building on Linux or macOS (the WPF project is Windows-only).

### WPF (`LemonRind_WPF`)

The original and the behavioural reference for the other two. Open `LemonRind.slnx` in Visual Studio, or:

```
dotnet run --project LemonRind_WPF/LemonRind.vbproj
```

### Avalonia (`LemonRind_Avalonia`) - cross-platform desktop

The same app as a native desktop application on Windows, Linux and macOS, from one C# codebase. It additionally has:

- A light/dark theme (follows the system, or choose in Settings -> Interface)
- Resizable side panes (their widths are remembered), vector icons, relative times in the chat list
- A log viewer with severity/text filtering, pause and copy

```
dotnet run --project LemonRind_Avalonia/LemonRindAvalonia.csproj
```

To run it on Linux without installing the SDK there, publish a self-contained build on any machine and copy the folder across (the executable bit does not survive a Windows-to-Linux copy, so `chmod +x` the binary):

```
dotnet publish LemonRind_Avalonia/LemonRindAvalonia.csproj -c Debug -r linux-x64 --self-contained
```

### Blazor (`LemonRind_Blazor`) - runs in a browser

The same app as a Blazor Server web app: start it, then open it in any browser - handy for a Linux box or a headless server. It has the same theme, resizable panes, icons and log-viewer controls as the Avalonia edition (theme and pane widths are remembered per browser), and on a narrow window or phone the chat list and stats panel turn into slide-in drawers.

```
cd LemonRind_Blazor
dotnet run --urls http://localhost:5031
```

then browse to `http://localhost:5031`. **There is no login**, so only bind it to your own machine or a network you trust (see the note in the Blazor project's own notes about adding authentication before exposing it more widely). `dotnet publish -c Debug` writes a ready-to-copy folder (`bin\Debug\publish\`) that needs only the .NET 10 ASP.NET Core runtime on the target machine: run `dotnet LemonRindBlazor.dll --urls ...` from inside it.

> **Build note (Avalonia and Blazor):** they use `SixLabors.ImageSharp` 4.x, which needs a licence file for **Release** builds (Debug builds work and just print a warning). See [Six Labors' licensing](https://sixlabors.com/pricing/) if you want Release builds.

## Core chat experience

- Streaming replies, with a collapsible "thinking" panel for models that return separate reasoning content
- Model selector grouped by capability (Chat / Image / Embedding / Other), read live from Lemonade
- Model details panel - capabilities, context window, size, backend, and the real launch arguments for the loaded model
- Health indicator for whether Lemonade is reachable, with auto-reconnect
- Live Lemonade log viewer, streaming the server's own logs in real time
- Per-turn and per-session stats - tokens in/out, tokens/sec, time-to-first-token, running context-usage bar
- Real tool-call outcomes shown as a genuine success/fail per call, with the actual error on hover if it failed
- Image analysis - attach an image to a vision-capable model
- Date dividers with hover timestamps
- System prompt viewer - see exactly what's currently sitting at the front of the model's context for the open chat
- Turn context viewer - see the volatile per-turn content (time-awareness, relevant memories/knowledge) that would be added if you hit Send right now
- Time-awareness - the model always knows the real current date/time, refreshed every turn

## Persona

Settings -> Persona lets the assistant "learn" who it's talking to - a standing sense of who you are, not just isolated facts. Everything here is optional and costs nothing in prompt size when left blank:

- Identity - a name and personality for the assistant
- About you - a short standing bio, included on every turn unconditionally
- Writing style - tone, verbosity, emoji use, plus free text for anything else

## Modules

Every capability beyond the core chat is a self-contained module, independently enabled/disabled in Settings -> Modules. A disabled module's tools are never offered to the model at all. Currently:

- Web search - searches the web via a swappable engine (SearXNG, Jina, Tavily, or Firecrawl)
- Web reader - fetches and reads a specific web page's text via the same swappable engine, with real SSRF protection when using direct fetch; also exposes a multi-page site crawl (up to 30 pages, following internal links) when a Firecrawl API key is configured
- File system access - reads and writes files in specific sandboxed folders only, with an approval dialog before any write
- Coder - code-aware search/read/edit tools over the same sandboxed folders, with syntax-checked edits
- MCP servers - connects to external MCP servers over stdio and exposes their tools
- Image generation - generates images from a text prompt via Lemonade's configured image model
- Scheduler - runs a saved prompt through the full tool-enabled assistant on a cron schedule
- Knowledge Bases - lets a chat attach ingested files/folders/websites/pasted text so relevant content is folded into replies
- Auto-backup - periodically zips the data folder (including all settings) to a separate folder as a safety net; off by default

## Keeping prompts small

A deliberate design priority throughout:

- A stable, cacheable system prompt - only genuinely stable content lives here, so Lemonade's prefix-based KV-cache can actually reuse work between turns
- Volatile per-turn content (memories, knowledge-base results) is folded into the outgoing message only, never into the stored conversation
- Short-term compaction - once context usage crosses a threshold, older messages get summarized into a running paragraph; nothing is lost from the actual saved transcript
- Long-term memory is capped and similarity-filtered, so old conversations don't bloat every future prompt just by existing - reviewable, editable, and deletable at any time in Settings -> Memories
- Images are stripped from history except the newest occurrence
- A disabled module's tools are never included in the request at all

## Organization

- Folders and tags for the sidebar
- Full-text search across all chats (titles, message text, folder names and tags - Avalonia and Blazor)
- Auto-tagging (e.g. `image`, `scheduled`) so a session's origin is visible at a glance
- Export any chat to Markdown, with thinking/tool use/images all preserved

## Data & security

- Everything lives under one portable data folder (database, generated/attached images, workspace files, settings) - copy it anywhere and it travels with you
- File system and Coder access is sandboxed to explicitly configured root folders, with an approval dialog before any write
- Web reader's direct-fetch path (the default, when no external search engine is configured) has real SSRF protection, tested against a genuine local-network bypass attempt during development
- Content fetched from the web is sanitized before it reaches the model - invisible/lookalike characters are stripped so a malicious page can't hide instructions in plain sight

## Where things live

- `LemonRind_WPF\`, `LemonRind_Avalonia\`, `LemonRind_Blazor\` - the application source, one folder per edition
- `data\appsettings.json` (inside each app's portable data folder, next to the built binaries) - Lemonade URL, SearXNG URL, API keys for search providers, module toggles, and every other runtime setting; almost all of it is also editable live from the Settings screen without a restart. The three editions share the same database schema and settings layout; each keeps its own data folder
- `screenshots\` - screenshots of each edition (see below)

## AI-assisted development

Not vibe coded, but vibe assisted. AI helped with ideas, inspiration, and pointers, but code was reviewed, understood, tested, and validated by a human.

## Screenshots

Each edition has its own page of screenshots:

- **[WPF screenshots](screenshots/WPF.md)** - the original Windows desktop app
- **[Avalonia screenshots](screenshots/Avalonia.md)** - the cross-platform desktop app (light, plus a dark-mode view)
- **[Blazor screenshots](screenshots/Blazor.md)** - the browser edition (light, plus a dark-mode view)

**Avalonia, dark mode**

![Avalonia in dark mode](<screenshots/Avalonia/Overview (dark).png>)

**Blazor, light**

![Blazor in the browser](<screenshots/Blazor/Overview.png>)
