# Lemon Rind - Local AI Agent for Lemonade

### "All the zest, none of the cloud."

A local-first AI assistant desktop app (WPF, .NET 10, VB.NET) backed by a locally-running
[Lemonade Server](https://lemonade-server.ai/) - everything runs on your own machine, including the model itself.

This was built as a learning exercise and proof of concept - a way to explore agentic AI app design (tool calling, memory, context management, RAG) hands-on, not a polished commercial product.

## Screenshots

**Main chat window** - streaming replies, thinking panel, per-turn/session stats, context usage bar
![Overview](<screenshots/Overview.png>)

**Settings - Lemonade server**
![Settings](<screenshots/Settings.png>)

**Settings - Persona**
![Persona](<screenshots/Persona.png>)

**Settings - Web search** - swappable engine, API keys
![Web search](<screenshots/Web Search.png>)

**Settings - Modules** - enable/disable each capability independently
![Modules](<screenshots/Modules.png>)

**Settings - Memories** - review, edit, and delete what's been remembered about you
![Memories](<screenshots/Memories.png>)

**Settings - Knowledge Bases (RAG)** - ingest files, folders, websites, or pasted text
![Knowledge Bases](<screenshots/Knowledge Base (RAG).png>)

**Settings - MCP servers** - paste a server's own config JSON, or add one manually
![MCP servers](<screenshots/MCP.png>)

**Settings - Scheduler** - run a saved prompt through the assistant on a cron schedule
![Scheduler](<screenshots/Scheduler.png>)

**Settings - Image generation**
![Image generation](<screenshots/Image Generation.png>)

**System prompt viewer** - exactly what's currently sitting at the front of the model's context
![System prompt viewer](<screenshots/System Prompt.png>)

**Turn context viewer** - the volatile per-turn content that would be added if you hit Send right now
![Turn context viewer](<screenshots/Turn Context.png>)

**File write approval** - Deny / Allow once / Allow for this session, before any file is touched
![File write confirmation](<screenshots/File Write Confirmation dialog.png>)

**Live Lemonade log viewer**
![Log viewer](<screenshots/Log Viewer.png>)

**Model details** - capabilities, context window, size, backend, and real launch arguments
![Model details](<screenshots/Model Details.png>)

## Requirements

- Windows, .NET 10 runtime
- A running [Lemonade Server](https://lemonade-server.ai/) instance (local or on your network), with at least one chat model downloaded
- Optional, per module you want to use: a web search/read engine (self-hosted [SearXNG](https://docs.searxng.org/), or an API key for Jina, Tavily, or Firecrawl), an MCP server (e.g. Postmark for email)

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
- Full-text search across all chats
- Auto-tagging (e.g. `image`, `scheduled`) so a session's origin is visible at a glance
- Export any chat to Markdown, with thinking/tool use/images all preserved

## Data & security

- Everything lives under one portable data folder (database, generated/attached images, workspace files, settings) - copy it anywhere and it travels with you
- File system and Coder access is sandboxed to explicitly configured root folders, with an approval dialog before any write
- Web reader's direct-fetch path (the default, when no external search engine is configured) has real SSRF protection, tested against a genuine local-network bypass attempt during development
- Content fetched from the web is sanitized before it reaches the model - invisible/lookalike characters are stripped so a malicious page can't hide instructions in plain sight

## Where things live

- `LemonRind\` - the actual application source
- `data\appsettings.json` (inside the app's portable data folder) - Lemonade URL, SearXNG URL, API keys for search providers, module toggles, and every other runtime setting; almost all of it is also editable live from the Settings screen without a restart

## AI-assisted development

Not vibe coded — vibe assisted. AI helped with ideas, inspiration, and pointers, but code was reviewed, understood, tested, and validated by a human.
