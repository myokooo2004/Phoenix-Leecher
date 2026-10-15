# Phoenix Leecher

Windows download manager for pCloud and MediaFire share links. Paste a page URL,
get a download list. No browser extensions, no accounts.

## Features

- **Link discovery** — paste any page URL; `*.pcloud.link` and `mediafire.com` links
  are extracted automatically.
- **Blogspot crawl** — `Scan` walks older posts via the Blogger feed API until
  50 share-links are found or posts are exhausted. `Stop` halts gracefully;
  `Resume` continues from the persisted feed position, even after app restart.
- **Dedup** — entries are keyed by the host-resolved inside name (never the URL slug),
  normalized (NFC, case-insensitive, Zawgyi→Unicode). Same name on both hosts →
  one entry, pCloud primary, MediaFire kept as automatic fallback.
- **Resilient downloads** — HTTP Range resume (`.part` files), automatic re-resolve
  of expired direct URLs, pCloud→MediaFire failover mid-download.
- **Folders as single entries** — a shared folder downloads into
  `Downloads\PhoenixLeecher\<folder>\`.
- Password-protected links unlock inline; passwords are session-only, never stored.
- Light/dark themes. System font, no bundled assets.

## Resource profile

Single connection per file (no multi-segment), async I/O, max 3 concurrent file
downloads (configurable), 32 KB stream buffers, throttled UI updates.
Typical: <150 MB RAM, low single-digit % CPU.

## Build

Requires .NET 8 SDK. CI builds on `windows-latest`:

```
dotnet publish src/PhoenixLeecher/PhoenixLeecher.csproj -c Release -r win-x64 \
  --self-contained true -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

Pushing a `v*` tag runs `.github/workflows/build.yml`, which compiles the app,
builds a Windows installer with Inno Setup, and attaches
`PhoenixLeecher-Setup-<version>.exe` to the GitHub Release. The installer is
unsigned; expect a Windows SmartScreen "Unknown publisher" prompt.

## Notes

- pCloud's API intermittently returns empty responses; all API calls use
  6-attempt exponential backoff.
- MediaFire folder APIs (`get_info`, `get_content`) work anonymously; file
  downloads follow the `/file/{quickkey}` 302 redirect, which supports ranges.
