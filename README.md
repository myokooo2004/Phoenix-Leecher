# Phoenix Leecher

Windows download manager for pCloud + MediaFire share links (V1).

## What it does

1. Paste a web page URL → pCloud / MediaFire links are auto-found immediately.
2. **Scan** crawls older Blogspot posts (Blogger feed API) until **50 share-links**
   found or posts exhausted. **Stop** halts gracefully; **Resume** continues from the
   saved feed position — scan state is persisted to disk and survives app restart.
3. Each folder = **one entry** (inside-name from the host API, never the URL slug).
   Same inside-name on both hosts → one entry, **pCloud primary + MediaFire backup**.
   If pCloud breaks, download auto-falls-back to MediaFire.
4. Per-item Download / Pause / Resume, Download All, Pause All / Resume All.
5. Dead/expired direct links are auto re-resolved; downloads resume via HTTP Range.
6. Password-protected links: inline unlock, session-only (never stored).
7. Myanmar filenames: UTF-8 everywhere, heuristic Zawgyi→Unicode for display/save/dedup.
8. Light mode default + dark mode toggle. System font (Segoe UI), no bundled fonts.

## Resource budgets

- **CPU**: single connection per file (no multi-segment), async I/O, max 3 concurrent
  file downloads (changeable in Options), UI updates throttled to 4/sec.
- **RAM**: stream-to-disk (32 KB buffer, never whole files in RAM), metadata-only
  entry list, DataGrid virtualization. Target < 150 MB steady state.

## Build

CI (`build.yml`) publishes a single-file `PhoenixLeecher.exe` on `windows-latest`
on tag push (`v*`) or manual dispatch, and attaches it to a GitHub Release.

> Unsigned exe → Windows SmartScreen "Unknown publisher" warning is expected.

## Verified live (2026-10-15, before implementation)

- pCloud `showpublink` → folder/file metadata; `getpublinkdownload` → direct URL (Range 206).
- MediaFire `folder/get_info` + `folder/get_content` (anonymous) → name/files/quickkeys.
- MediaFire `/file/{quickkey}/x` → 302 → direct URL (Range 206).
- Blogger feed API: 7057 posts, `start-index` pagination works.
- Prototype scan: 50 links / 25 posts → 29 entries, 9 deduped pairs — all passed.
- pCloud API intermittently returns empty replies (~24%) → 6-attempt exponential
  backoff is mandatory (verified working).
