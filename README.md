# Relliga

**Your digital life, together.**

Relliga is a local-first, open-source desktop application for preserving and exploring personal history. Your library stays on your device, with no telemetry or Relliga cloud storage. Manual imports work offline. Optional, explicitly authorized Telegram connections communicate with Telegram through official TDLib while Relliga runs.

## Implemented in 0.5.0

- Provider-independent connection lifecycle, durable checkpoints, transactional synchronization pages, cancellation and bounded retry/backoff. One Telegram account per library is supported.
- An official TDLib JSON C-ABI adapter, persistent encrypted TDLib sessions with Windows DPAPI-protected keys, phone/code/two-step password/email authentication states, initial cloud history paging, live updates, reconciliation on reconnect, pause/resume, manual refresh and explicit disconnection. **Native/live-account verification remains blocked by external prerequisites described below.**
- Telegram Desktop account/single-chat JSON import, shared account/chat/server-message identity, preserved provenance, Conversations browsing/search, universal search/timeline, statistics, exports, controlled deletion and backup/restore integration.

- Import Spotify Extended Streaming History JSON and Google Calendar ICS, standalone or together inside ZIP archives. Formats are detected from contents, including archive members with arbitrary filenames.
- Preview sampled records and warnings, cancel processing, and review results in a paged import history with source, date, version, status, processed/imported/updated/skipped/rejected counters, and diagnostics.
- Preserve listening events, Calendar definitions, original metadata, source filenames, record indexes, revisions and import provenance in SQLite.
- Browse a unified timeline in pages of 50, filter by source/type/date, and inspect playback or Calendar details. SQLite FTS5 searches music fields and Calendar titles/descriptions/locations.
- Handle all-day, UTC, floating and recognized timezone-aware Calendar events, bounded recurrence, recurrence dates, exclusions and individual overrides.
- Reimport overlapping exports using stable playback and Calendar identities while preserving repeated listening occurrences.
- View per-source counts, playback hours, recurring-series counts, successful imports and the most recent operation in the dark Avalonia interface.
- Recover interrupted imports, reject concurrent app access to the same library, and roll back fatal failures and cancellation.
- Stream every matching timeline/search result to UTF-8 CSV or versioned JSON, with native destination selection, cancellation and atomic publication.
- Preview and confirm removal of sources or individual import batches while retaining shared records and surviving Calendar revisions.
- Create verified offline ZIP backups using SQLite snapshots; preview and restore compatible libraries with a previous-library recovery copy and interruption recovery.
- Verify SQLite, schema, foreign keys, provenance and search-index health from Settings without automatic repair.
- Run a self-contained Windows x64 portable build or per-user NSIS installer, with application icons, version metadata, dependency notices and SHA-256 checksums.
- Use keyboard navigation shortcuts, visible control focus, accessible status/error regions and safer confirmation defaults. Cancelled previews preserve the current search and selection.
- Upgrade existing libraries through additive schema 3 indexes that accelerate provenance, integrity checks and filtered queries without changing event identity.

## Stack and architecture

C# / .NET 10, Avalonia 12.1.4 with Fluent theme and Inter typography, CommunityToolkit.Mvvm 8.4.2, Microsoft.Extensions.DependencyInjection, Microsoft.Data.Sqlite / SQLite FTS5, [Ical.Net 5.2.3](https://github.com/ical-org/ical.net) (MIT), NodaTime 3.3.5 (Apache-2.0), and xUnit v3 with Microsoft Testing Platform.

| Project | Responsibility |
| --- | --- |
| `src/Relliga.Domain` | Provider-independent event and import models |
| `src/Relliga.Application` | Import orchestration, library activity coordination and ownership service contracts |
| `src/Relliga.Infrastructure` | SQLite migrations, queries, provenance, export, deletion, integrity and backup/recovery |
| `src/Relliga.Importers` | Content routing, Spotify/Calendar parsers, recurrence evaluation and archive validation |
| `src/Relliga.Desktop` | Avalonia views, MVVM state and dependency composition |
| `tests/Relliga.Tests` | Synthetic parser and SQLite integration tests |

Domain and Application do not depend on Avalonia, SQLite or Ical.Net. Infrastructure queries use the application-owned recurrence contract; the desktop composition root supplies its implementation. All three sources share import orchestration, querying, paging, history and UI navigation.

## Telegram connection and privacy boundaries

Open **Connected accounts → Connect / resume**. Follow the phone number, verification code and optional two-step password/email prompts supplied by TDLib. Inputs are masked and cleared on submission, and are not saved or logged. Registration, purchase and unknown authorization states stop with an explanation; use an official Telegram client for those steps. QR/passkey login, secret chats, channel live ingestion and attachment downloads are not implemented. Authorization status comes from TDLib; an up-to-date state and last-success time require a completed synchronization pass.

Automatic synchronization runs only while Relliga is open. Valid saved sessions resume on subsequent launches; pause closes the worker while retaining its protected session. Refresh triggers a pass; a manual refresh of a paused account leaves automatic synchronization paused. Disconnect attempts server logout and retains ordinary local history; when no client is available it forgets the local protected key and explicitly says to revoke the device in **Telegram Settings → Devices** because server logout is unconfirmed. Pause or disconnect before local source/batch deletion. No startup task, background service, message sending, browser scraping or session interception is installed.

The integration follows [Telegram's API terms](https://core.telegram.org/api/terms) and [official TDLib guidance](https://core.telegram.org/tdlib/getting-started). Live ingestion covers accessible private cloud chats/basic groups/supergroups. Channels are deliberately disabled pending required sponsored-message support. Secret chats, protected content and messages/chats with disappearing-content settings are excluded; relevant provider privacy changes purge cached library content, all origins and FTS transactionally. Ordinary cache-eviction updates do not delete library records. Opening a connected conversation or inspecting a Telegram timeline/search message sends normal read receipts; synchronization alone does not open chats. TDLib presence tracks window activation. This is not a ghost-mode client or a claim of Telegram approval.

History uses bounded pages of 25, atomically saved with their continuation. New/edited/deleted updates are processed in order. Each completed pass reconciles stored live messages in batches of 100, including edits/deletions missed while stopped, then checks again after a minute or a refresh request. Unchanged live records do not add redundant provenance/history rows. The scope is bounded to 10,000 chats, 100,000 cached participant names, one million messages per chat/pass, a 512-update / 16 MiB queue and 4 MiB native responses. Overflow stops clearly; checkpoints survive. Very large libraries may need long reconciliation passes. Offline records reflect the last known provider state; disconnected clients cannot discover later deletions.

Windows uses **DPAPI CurrentUser**, via Microsoft's supported ProtectedData API. Encrypted credential blobs and encrypted TDLib state live under `%LOCALAPPDATA%/Relliga/Connections`, separately from the library; session keys are scoped by library location and connection identity. Verification codes, account passwords and raw authorization responses never enter SQLite, exports or backups. Application registration hashes are protected after provisioning. macOS Keychain/Linux Secret Service are abstraction extension points only; live sessions on those platforms fail explicitly. The ordinary SQLite library, exports, external backups and recovery copies remain **unencrypted**.

Backups contain conversation data and non-secret connection/checkpoint metadata, **not credentials or TDLib session databases**. Restore pauses accounts, clears stale checkpoints, and retains this device's known provider deletion markers while staging the candidate. A backup restored on another device has no session and cannot know later provider changes until reauthorized and reconciled. External old backups/exports cannot be retroactively erased; protect and retire them yourself. Provider deletion is logical removal, not forensic erasure.

### Publisher/developer setup and unverified live prerequisites

Ordinary users should receive an application already provisioned by its publisher; they should **not** be asked to obtain developer credentials. This checkout has no Telegram registration credentials or bundled native TDLib. A production live-enabled distribution therefore remains blocked until the maintainer provisions a Relliga-owned API registration and validates the native package and permitted test account.

1. Obtain Relliga's own application registration following [Telegram's official API ID instructions](https://core.telegram.org/api/obtaining_api_id). Do not use another client's registration or commit the ID/hash. Do not put real phone numbers, codes, passwords or session files in tests.
2. Build the official TDLib **JSON** native library for Windows x64 using the [official build generator](https://tdlib.github.io/td/build.html). TDLib requires a C++17 compiler, CMake, gperf, OpenSSL and zlib; use a compatible OpenSSL 3 build and include dependency/license notices when distributing it. The adapter targets the documented schema reviewed on 2026-10-16, source revision `a94b83d67ab4c8397c6245e541b31a4131ea2f79`, including `message.can_be_saved`, and the official `td_json_client_*` ABI. Incompatible message schemas fail closed. TDLib's [Boost Software License 1.0](https://github.com/tdlib/td/blob/master/LICENSE_1_0.txt) is permissive; native dependency terms must accompany the exact binary build. No unofficial wrapper or prebuilt native binary was downloaded/executed here.
3. Set `$env:RELLIGA_TDLIB_PATH` to the absolute trusted `tdjson.dll` path with its matching native dependencies beside it, or place those files beside `Relliga.exe`. Build Relliga, then run `pwsh -File .\scripts\Configure-TelegramDevelopment.ps1`. The script prompts for the application ID/hash without echoing the hash, starts Relliga with process-only configuration and restores the parent environment on exit. Connect once to protect the registration in DPAPI. The script itself does not authenticate a user. Avoid PowerShell transcription/debugging while handling secrets.
4. Use a permitted test account directly in Relliga. Validate code delivery and two-step/email prompts, synchronize a small cloud chat, edit/delete messages using the official client, pause/resume, interrupt networking, restart Relliga and check catch-up, then disconnect and inspect Telegram's device list. Do not claim these checks succeeded from the synthetic tests. No credentials or native TDLib were configured in this iteration; CMake/MSVC commands were unavailable in the current PATH, so no native TDLib build or live authentication was performed.

### Telegram Desktop JSON fallback

Use Telegram Desktop's official **Export Telegram data → Machine-readable JSON** or **Export chat history → JSON**. Import the JSON file or a bounded ZIP containing it. The parser detects `chats.list` or a single chat's `messages` from contents; HTML exports are unsupported. It preserves rich-text content, sender identity/name when present, UTC `date_unixtime`, nullable edit time, chat identity/type, source message metadata and media presence. It never follows attachment paths, links or executable content. `verification_codes`, secret chats and self-destructing records are excluded/reported.

A full export with `personal_information.user_id` shares identities with the same live account: account ID + normalized chat ID + server message ID (TDLib's server IDs use a 20-bit shift). More recent edits win; live data wins equal-revision disagreements. Export-only/unattributed chats use a deterministic isolated namespace, so identical reimports deduplicate while different unattributed exports are not speculatively merged. The whole JSON document is bounded to 64 MiB/depth 32; message text is limited to 64 KiB, per-message source JSON to 1 MiB, and records to one million. Malformed records are reported; fatal parsing/cancellation rolls back. Full account exports larger than the limit should be split using official per-chat exports, with the identity-isolation limitation above.

For a synthetic check, import `tests/Relliga.Tests/Fixtures/synthetic-telegram.json`: expect two messages in **Conversations → Synthetic studio chat**, search `Aurora`, inspect details, and reimport with zero new records. Choose **Telegram** in Timeline/Search to browse without Calendar's bounded date window. **Library & export → Telegram · all messages** exports every matching message with provenance; CSV includes the normalized body/sender/chat and source metadata JSON, and JSON retains all origins.

## Development and Windows launch

Install the **.NET 10 SDK**. A graphical desktop is required to run the application; package restore needs access to NuGet. No separate SQLite installation is required.

From PowerShell:

```powershell
Set-Location C:\Dev\Relliga
dotnet restore
dotnet build --no-restore
dotnet test --solution Relliga.slnx --no-build --no-restore
dotnet run --project .\src\Relliga.Desktop\Relliga.Desktop.csproj --no-build
```

After a Debug build, you can also run `C:\Dev\Relliga\src\Relliga.Desktop\bin\Debug\net10.0\Relliga.exe` directly with the .NET 10 runtime installed. The adjacent build output files are required. Published self-contained builds include the runtime.

The source targets Windows, macOS and Linux through Avalonia. Native desktop dependencies and platform testing are still required on macOS/Linux; this iteration is validated on Windows only. If a restricted build environment blocks MSBuild worker processes, use `dotnet build --no-restore -m:1 /nodeReuse:false /p:UseSharedCompilation=false`. The test runner requires local named-pipe access.

For isolated development or GUI testing, set `RELLIGA_DATA_DIRECTORY` to an absolute temporary directory outside the repository before launching. Relative overrides and locations inside the application directory are rejected with a startup message. Leave it unset for the normal per-user library. Settings displays the actual library path.

## Windows distribution

The **portable ZIP** contains the entire application and .NET runtime. Extract it into a writable folder and open `Relliga.exe`; no SDK/runtime installation or administrator access is required. Keep its adjacent files together. Data remains in `%LOCALAPPDATA%\Relliga`, rather than the extracted directory.

The **installer** installs for the current user into `%LOCALAPPDATA%\Programs\Relliga`, adds a Start Menu shortcut and registers an uninstaller in Windows Installed Apps. It does not launch Relliga automatically. Close the app before reinstalling/upgrading/uninstalling; a running executable blocks these operations before files change. Installation never opens or migrates the personal database. Ordinary uninstallation removes only packaged application files, shortcut and registration; libraries, exports, backups and unrelated files remain. The application migrates compatible libraries when subsequently launched.

Artifacts are **unsigned development builds**. Windows may display SmartScreen/reputation warnings; no signing, certificate trust, notarization or security-warning bypass is provided. A legitimate signing/distribution process remains future work. Automatic updates are not implemented.

To reproduce from the checkout, install **.NET SDK 10**, **PowerShell 7**, and optionally **[NSIS 3.x](https://nsis.sourceforge.io/Download)** for the installer:

```powershell
Set-Location C:\Dev\Relliga
pwsh -File .\scripts\Publish-Portable.ps1 -RuntimeIdentifier win-x64
pwsh -File .\scripts\Build-WindowsInstaller.ps1 -CompilerPath 'C:\Program Files (x86)\NSIS\Bin\makensis.exe'
pwsh -File .\scripts\Test-Distribution.ps1
```

The installer script also finds `makensis.exe` on PATH or in the usual NSIS directory. If the compiler is unavailable, it reports the exact prerequisite and produces no installer; the portable artifact remains usable. Build scripts never download/install NSIS themselves. The NSIS core/zlib installer components use permissive [zlib/libpng terms](https://nsis.sourceforge.io/License); full notices are bundled.

Outputs are under `artifacts/distribution/0.5.0/`: `Relliga-0.5.0-win-x64-portable/`, its `.zip`, `Relliga-0.5.0-win-x64-installer.exe`, and `SHA256SUMS.txt`. Use `Get-FileHash -Algorithm SHA256` to compare an artifact against that file. Repeat with `-Replace` to replace generated output deliberately. `-OutputRoot` supports spaces/Unicode but must remain inside repository `artifacts/`; linked output directories are rejected. ZIPs and installers include GPLv3, runtime/native notices, dependency inventory and package license/attribution files. These are reproducible build commands, not a claim of byte-identical ZIP/installer output across timestamps, SDKs or toolchains.

Before sharing GPLv3 binaries externally, provide the complete corresponding source for the exact build, including local modifications and packaging scripts. Local development artifacts are not automatically published or accompanied by a source offer. Generated binaries, datasets and build-tool downloads stay ignored by Git. Python is needed only to regenerate the checked-in icon via `scripts/Generate-AppIcon.py`.

To validate Windows distribution manually:

1. Compare the package SHA-256 with `SHA256SUMS.txt`. Extract the portable ZIP, keep its files together, and open `Relliga.exe`. Alternatively run `Relliga-0.5.0-win-x64-installer.exe`, review the GPLv3 page, complete installation, and open Relliga from the Start Menu. The installer does not launch it automatically.
2. For a disposable test, launch from PowerShell with `$env:RELLIGA_DATA_DIRECTORY = Join-Path $env:TEMP ('Relliga-QA-' + [guid]::NewGuid().ToString('N'))` and `& "$env:LOCALAPPDATA\Programs\Relliga\Relliga.exe"`, or use the extracted executable's full path. Confirm that Settings shows this isolated location and version 0.5.0. Keep this same environment value for subsequent launches.
3. Follow the synthetic-data walkthrough below, including search, export, backup, restore preview and health. Tab through controls, use Ctrl+F/Ctrl+I/Ctrl+E and Alt+1…9, resize/maximize/restore the window, and inspect long details. Check 100%, 150% and 200% DPI and screen-reader output on suitable test machines; those combinations are not certified here.
4. Close Relliga and reopen it with the same temporary directory; counts and records must persist. With the installed app running, try the installer again: it must ask you to close Relliga before changing files. Close it and reinstall; the shortcut, version and library must remain correct. A historical cross-version installer upgrade remains a separate release validation task.
5. Close Relliga and uninstall through Windows Installed apps or `%LOCALAPPDATA%\Programs\Relliga\Uninstall.exe`. Application files, shortcut and registration should disappear; the separate library and external backups/exports must remain. Extracted portable files have no registration; removing that application folder does not remove the separate library. Never manually remove personal libraries as part of ordinary application cleanup.

## Platform preparation and keyboard access

Windows x64 is the primary verified desktop target (this development machine runs Windows build 26300). Linux x64 and macOS ARM64 can be cross-published with the same script using `-RuntimeIdentifier linux-x64` or `osx-arm64`. Run `Test-Distribution.ps1 -RuntimeIdentifiers win-x64,linux-x64,osx-arm64` after generating all three. Cross-publish output is preparation, not native GUI validation or an installer. On Linux, preserve executable permissions (`chmod +x Relliga`) after ZIP extraction; the native X11/fontconfig/ICU/OpenGL requirements depend on the distribution. macOS output is a raw unsigned directory, not a notarized `.app` or installer. Consult [Avalonia's supported platforms](https://docs.avaloniaui.net/docs/supported-platforms) and [.NET publishing guidance](https://learn.microsoft.com/dotnet/core/deploying/) for platform prerequisites. No Linux/macOS GUI verification is claimed from Windows.

Use **Alt+1…9** for the nine navigation sections, **Ctrl+F** for Search, **Ctrl+I** for Import and **Ctrl+E** for Library & export. Search receives keyboard focus; Enter applies its query. Tab/Shift+Tab traverse controls; arrow keys operate navigation lists and selectors. Escape requests cancellation when an operation/query permits it. Confirmation dialogs initially focus Cancel, make Enter default to Cancel, and name their destructive action explicitly. Status/error regions use Avalonia live-region metadata. Per-monitor DPI awareness and wrapping layouts are configured; full screen-reader, text-scaling and WCAG conformance audits remain outstanding.

## Importing Spotify data

Request **Extended streaming history** from Spotify's privacy controls. See Spotify's [Understanding your data](https://support.spotify.com/article/understanding-your-data/) for the fields included and the technical README delivered with each export.

Choose **Import data → Choose file**, select a JSON file or ZIP, review the detected source, then choose **Import into library**. Open Timeline or Search after import. Import separate JSON files one at a time, or select the archive containing them.

The supported format is a JSON array of extended streaming records with `ts` (stream end time with UTC offset), non-negative integer `ms_played`, and Spotify identity fields such as `master_metadata_track_name`, `spotify_track_uri`, or `episode_name`. Track title, artist, album, URI and podcast fields are used when present; missing fields remain null. Other exported fields remain in the local metadata JSON. Standard account history with `endTime` / `msPlayed`, playlists and technical logs are unsupported.

File names do not determine source detection. ZIP members are inspected for supported content. Unsupported content is reported as skipped. Invalid members produce explicit warnings; any valid JSON records already decoded from that member are retained and other members continue. Calendar documents require valid component boundaries before their events are accepted. A standalone syntax error, unsafe archive, I/O failure or cancellation rolls back the operation. Wholly empty/unsupported input fails without saving events. Up to 100 diagnostic examples and the full warning count are retained. Unparsed archive members are reported separately; their unknown record counts are never invented.

Safety limits: 64 MiB per JSON/member, 256 MiB archive and total expanded content, 256 ZIP entries, 200:1 maximum compression ratio, 1,000,000 records, JSON depth 32, 1 MiB characters per record and 64 KiB per normalized string. Actual expanded bytes are checked while reading. Unsafe paths and duplicate archive member names are rejected. ZIP members are never extracted.

## Google Calendar and recurrence

Import an exported `.ics` file or a ZIP containing calendars. Compatible iCalendar 2.0 VEVENT exports are supported; no Google account connection is needed. Titles, descriptions, locations, start/end values, timezone IDs, UID, recurrence identity, sequence and export metadata are retained. Missing UIDs are rejected rather than replaced with generated IDs. Attachments, URLs and alarms remain passive metadata and are never fetched or executed.

Calendar definitions are stored once per **UID + RECURRENCE-ID**. Occurrences are evaluated only for a query window of **at most 366 days**, with a default 90-day window when including Calendar. Explicit date filters apply to both sources; the UI displays the effective window. Select Spotify to browse playback history without the Calendar window limit. Dates are inclusive in the UI, with an exclusive upper bound internally; filtering uses occurrence start dates rather than interval overlap.

Supported recurrence includes DAILY, WEEKLY, MONTHLY and YEARLY RRULEs, COUNT/UNTIL, common date selectors, RDATE (including periods), EXDATE, moved individual occurrences and cancellation components. Finite counts exclude RRULE-generated nonexistent DST times before exclusions are applied. Ambiguous explicit local times use the earlier offset; explicit gap values use the pre-transition offset. All-day events remain dates with exclusive end dates. Floating clocks retain no assigned timezone or UTC instant. Timed events sort/display in UTC; all-day dates and floating clocks use their exported date/clock for neutral timeline placement, which cannot establish an absolute instant.

**Explicit limitations:** subdaily recurrence frequencies, BYHOUR/BYMINUTE/BYSECOND expansion, multiple RRULEs, EXRULE and RECURRENCE-ID RANGE/THISANDFUTURE are rejected with diagnostics. Invalid components block other components of the same series in that input. Recognized timezone IDs use the bundled timezone database. Embedded VTIMEZONE definitions are preserved with a warning; custom timezone rules are not evaluated. Import limits are 16 MiB per ICS document, 10,000 components per document, nesting depth 32, and bounded text/date-selector lengths. Queries enforce definition, occurrence, memory and evaluation budgets and fail clearly rather than returning silently truncated results. Very large or pathological series can require narrower windows or simpler definitions.

Newer Calendar SEQUENCE values, or newer timestamps at the same sequence, update the definition. Older revisions are skipped; differing revisions with no ordering evidence retain the existing definition and produce a conflict warning. Incoming revisions are retained in provenance. Absence from a later partial export does not delete an earlier event or override; an explicit cancellation is required.

## Exporting your data

Open **Library & export**, choose JSON or CSV and a scope, then **Choose destination & export**. Scopes cover all Spotify playback, all Telegram messages, Calendar, all sources in a date range, or the last successfully applied Timeline/Search filters. The current-results scope freezes applied text/source/type/date filters, including the effective Calendar window; unapplied edits do not change it. All matches are exported, including records beyond the current page, in the same descending timeline order. Calendar exports contain occurrences in the bounded window; use a backup to preserve the complete library, including definitions outside that window and cancellation components.

Exports use a single query transaction, stream rows/provenance, and publish a flushed temporary sibling only after success. Existing destinations require confirmation. Failure/cancellation preserves the previous destination and removes partial output. A process crash can leave a hidden `.relliga-*.tmp` sibling; it is never reported as a completed export and can be removed after closing Relliga. Choose destinations outside the managed library directory. Original imported records are unchanged.

- **CSV:** UTF-8 without BOM, a header and quoted comma-separated cells; embedded quotes are doubled and line breaks retained. Columns, in order: `id, source, type, title, artist, album, ended_at_utc, duration_ms, uri, start_local, end_local, time_zone, all_day, floating, uid, recurrence_id, occurrence_id, source_file, record_index, importer_version, imported_at_utc, metadata_json, calendar_json`. UTC values use ISO 8601 with `Z`; Calendar local values have no offset, and all-day/floating flags preserve their meaning. CSV missing and empty values both become empty cells. Formula-like text beginning with `=`, `+`, `-`, `@` (including leading whitespace), tab or newline is prefixed with an apostrophe. This intentionally changes spreadsheet-facing text; use JSON for lossless values. CSV includes the selected origin and source metadata, rather than all overlapping origins.
- **JSON schema 1:** an object with `schemaVersion`, `applicationVersion`, effective `query`, `calendarWindowPolicy`, `events` and `recordCount`. Each event has a stable `id`, normalized `record`, parsed source `metadata`, and streaming `origins` with import IDs, filenames, record indexes, versions, timestamps and original metadata/definitions. JSON preserves null versus empty strings. Calendar records retain local clocks, nullable instants, timezone, UID/recurrence identity and raw ICS. Stable playback IDs use provider + playback fingerprint + multiplicity; Calendar IDs hash UID + occurrence identity. These survive backup/restore and shared-owner reparenting. JSON exports are interchange files, not directly reimportable Relliga backups.

## Removing imported information

Use **Library & export → Manage imported sources** to review source counts and request removal. Use **Import history → select a batch → Review removal of selected batch** for one import. The confirmation shows stored playback/Calendar-definition counts, shared records retained, Calendar components recomputed, and history entries removed. Counts refer to stored definitions, not their potentially infinite recurring occurrences. A changed library invalidates a deletion preview.

Deletion is one transaction, including origins, history, source statistics and FTS triggers. A playback survives while another import owns it; displayed metadata/provenance is reparented to the earliest surviving origin. Calendar definitions are recomputed from surviving revisions using sequence then timestamp. Equal-order revisions retain the earliest surviving import, preferring its previously accepted revision, then source filename/record order. Removing a newer revision or cancellation can make an older owned event reappear. A surviving override can appear independently if its master was removed, consistent with existing import behavior.

Deleting a source removes its origins across mixed batches. History with no remaining origins is removed; mixed history needed by another source keeps its import ID/time but clears its filename, diagnostics and historical counters. Failed imports with an undetected source cannot reliably be attributed to a source; remove those batches individually. There are no personal-data tombstones in the active tables. External original files are never modified or deleted; no originals are copied into managed storage. External exports/backups and the deliberate previous-restore recovery copy remain until you remove them separately.

Deletion is **logical, not forensic or cryptographic erasure**. SQLite free pages, storage blocks, device/filesystem snapshots and external backups can retain information. Relliga does not claim secure erasure.

## Offline backup, restore and recovery

In **Settings & about**, choose **Create backup** and a destination outside the managed library directory. The app serializes library operations, uses SQLite's `BackupDatabase` API to snapshot live/WAL data, verifies the snapshot and completed archive, then atomically publishes it. No external originals, directory contents, logs, caches or credentials are copied. The database is currently the only required managed asset.

**Backup format 1** is a ZIP containing exactly `manifest.json` and `library.db`. The manifest records format/schema/application versions, creation time, database length and SHA-256, playback/component/import counts and source names. Format 1 accepts database schemas 1–4 from Relliga versions through 0.5.x; older databases migrate only in staging. Newer versions/schemas, unknown database schema objects, invalid counts, checksum mismatches, corrupt databases and inconsistent FTS/provenance are rejected. SHA-256 detects corruption; it does not authenticate an archive whose database and manifest were both intentionally replaced. Restore only backups you trust.

Archives/database payloads are limited to **2 GiB**, manifests to **64 KiB**, compression ratio to **500:1**, and central directories to **4 KiB / exactly two entries**. ZIP64, archive comments, extra/duplicate entries, paths and links are rejected before activation. Fixed filenames are streamed to isolated, bounded staging; contained paths are never used. Do not manually repackage backups using a tool that adds comments or extra files.

Choose **Preview & restore backup**, select a ZIP, review its validated date/source/count summary and replacement warning, then confirm. This replaces the whole active library; merging is unsupported. The archive is hashed again after confirmation. All app database work is serialized, connections are unpooled and closed, the active WAL is checkpointed, and the candidate is consolidated into one database file before replacement.

Activation requires same-filesystem `File.Replace` support. Relliga flushes the candidate and a fixed recovery journal (`relliga.db.restore-journal`), atomically replaces `relliga.db` while preserving `relliga.db.previous`, checks the activated library, then removes the journal as its commit point. Any failure before that commit rolls back. On startup, a surviving journal triggers conservative rollback to the previous library before normal database access. Unexpected hashes or ambiguous files stop recovery without overwriting them. Keep the database, sidecars, `.previous`, journal and original ZIP if recovery reports an error; do not delete the journal to force startup. A fresh isolated data directory can restore a known healthy ZIP without touching the damaged directory. Owned abandoned staging is cleaned on startup; unrelated staging is preserved.

The successful previous-library copy remains until **Review discarding recovery copy** is confirmed or a subsequent restore replaces it. It may contain information subsequently deleted from the active library. Cancellation is honored before activation; activation/rollback finishes without cancellation. SQLite's synchronous snapshot call cannot be interrupted mid-call, so cancellation may wait for that stage. Filesystem/hardware durability still depends on the OS, storage device and filesystem honoring flush/atomic-replacement guarantees; real power-loss testing has not been performed.

Backups, staging, recovery copies, exports and the active database are **unencrypted**. Protect them as sensitive personal data. Settings verification checks integrity and index consistency using rolled-back FTS integrity commands; it never rebuilds indexes or deletes records automatically.

## Persistence, privacy and limitations

- Windows stores the library at `%LOCALAPPDATA%\Relliga\relliga.db`; other platforms use .NET's per-user LocalApplicationData location. Application data is outside the source checkout. SQLite uses WAL, foreign keys, versioned transactional migrations and indexed queries. Schema 4 upgrades schemas 1/2/3 without deleting rows, provenance or original importer versions. Version 0/unrecognized nonempty schemas are rejected before initialization.
- The library is **not encrypted at rest**. Full export metadata may include Spotify usernames, IP addresses or other sensitive information. Protect your device and backups. Original source files are read without modification.
- Prefer the in-app portable backup. For manual filesystem copies, close Relliga and preserve the database together with sidecars and any recovery journal/copy; copying a live database alone is unsafe.
- Playback identity uses media URI, UTC end instant, played duration, account and device, plus multiplicity within the selected input. Title, album, IP and similar metadata changes no longer create new rows when this identity is available. Without a URI, canonical JSON remains the conservative fallback. Spotify exports contain no universal playback event ID: two physically distinct plays with identical identity fields in separate exports remain indistinguishable. Multiplicity is preserved up to the highest observed count, and device/account differences remain distinct. Historical rows already duplicated by version 0.1 are preserved rather than destructively merged during migration. Incoming metadata remains in provenance.
- Preview samples at most 1,000 valid events and does not guarantee the remainder of the input is valid. Progress is record-based feedback rather than a percentage. Large imports still require a bounded in-memory dictionary of event fingerprints.
- Search matches Unicode word prefixes, combining terms with AND. It is not arbitrary substring or advanced query syntax. History is paged in groups of 20; version 0.1 operations display unknown processed/rejected counters honestly. Dashboard Calendar counts represent logical events/series, not an infinite total of recurring occurrences; import counters count decoded components.
- Spotify/Calendar imports and the Telegram integration described above are implemented. There is no whole-library encryption, localization or automatic update service. Windows development packages are unsigned. Full screen-reader auditing and macOS/Linux GUI verification remain outstanding.

## Test both sources on Windows without personal data

For a fresh test library, set this in PowerShell before launching:

```powershell
$env:RELLIGA_DATA_DIRECTORY = Join-Path $env:TEMP ('Relliga-Synthetic-' + [guid]::NewGuid().ToString('N'))
dotnet run --project C:\Dev\Relliga\src\Relliga.Desktop\Relliga.Desktop.csproj --no-build
```

1. Import `C:\Dev\Relliga\tests\Relliga.Tests\Fixtures\synthetic-spotify.json`.
2. Import `C:\Dev\Relliga\tests\Relliga.Tests\Fixtures\synthetic-calendar.ics`.
3. In Timeline choose All sources / All event types, set dates to `2025-01-01` through `2025-01-31`, then Apply. Expect **7 timeline occurrences**: 2 plays, an all-day event, a floating reminder and 3 planning occurrences with an exclusion and moved instance.
4. Inspect the moved event on January 12. Search `studio` with the same dates to find 3 Calendar occurrences; search `Aurora` to find the Spotify play. Exercise source/type filters and Import history.
5. Reimport both files: expect zero additional records. Dashboard should show **5 plays + logical Calendar events/series**, 2 sources, and real import counts. Four Calendar components include an override belonging to an existing series.
6. Apply January filters in Timeline or Search, open Library & export, choose current applied filters and JSON, and save outside the temporary library directory. Verify `schemaVersion: 1` and 7 events for the unsearched timeline. Export Spotify/all playback as CSV: expect a header and 2 records. Cancel a Save dialog; try an existing destination and cancel replacement to check preservation.
7. In Settings create a ZIP backup, run Verify library, and expect healthy SQLite/provenance/FTS. Preview that ZIP for restore: expect 2 playback records, 4 Calendar definitions, and the actual number of imports (4 after step 5). Cancel and verify unchanged totals.
8. In Import history, remove one of the repeated Spotify batches: preview should retain both shared plays. Remove the remaining Spotify source from Library & export: expect only Calendar in the timeline, with source history/FTS updated. Confirm only in this isolated synthetic library. Original fixture files must still exist. Close/reopen using the same environment variable and check persistence.
9. Preview and confirm restoration of the ZIP. Reapply the January timeline range: expect 7 occurrences, preserved import history/identities and successful search/health checks. Close/reopen again. Inspect the reported recovery copy path, then optionally confirm discarding it. Try a truncated copy of the ZIP: validation must fail before replacing the active library.

Close the test app and unset `RELLIGA_DATA_DIRECTORY` in that shell to return to the normal library. Both fixtures contain synthetic data only.

## Measured performance (0.4 baseline)

Run `pwsh -File .\scripts\Measure-Performance.ps1 -Sizes 10000,100000,1000000 -Label local -SkipLargeArtifacts`. The existing test executable supplies the benchmark entry point; no additional solution project is needed. Reports and deterministic synthetic fixtures are generated locally, never committed. JSON reports live in `artifacts/benchmarks/`; temporary libraries are retained under `%TEMP%/Relliga.Benchmark` for investigation. The flag skips exports/backup/restore only at one million plays.

Measured on 15 October 2026: Intel Core i7-1165G7 (4 cores/8 threads), 16 GiB RAM, Windows build 26300, SDK 10.0.401, .NET 10.0.12, Release, workstation GC. Queries report medians of five warm-cache runs; imports/artifact operations are single wall-clock observations, including validation. These are local observations, not latency guarantees.

| Operation | 10k plays + 100 Calendar definitions | 100k + 1,000 | 1m + 1,000 |
| --- | ---: | ---: | ---: |
| Spotify import | 2.44 s | 37.35 s | 417.86 s |
| First / last playback page | 9.7 / 8.9 ms | 28.0 / 43.7 ms | 195.3 / 290.7 ms |
| Rare / common term search | 3.3 / 30.2 ms | 8.4 / 329.3 ms | 38.4 / 3,026.1 ms |
| Playback date filter | 9.3 ms | 24.8 ms | 27.9 ms |
| Calendar / mixed January window | 72.4 / 64.7 ms | 279.5 / 251.6 ms | 172.7 / 216.2 ms |
| CSV / JSON export, January window | 0.64 / 3.41 s | 4.09 / 15.94 s | Not measured |
| Verified backup / restore | 0.93 / 0.92 s | 11.38 / 9.67 s | Not measured |
| SQLite database size | 15.4 MiB | 154.5 MiB | 1,540.2 MiB |
| Cumulative process peak working set | 79.1 MiB | 137.8 MiB | 165.4 MiB |

The input uses 500 track titles, 60 artists, rare terms every 1,000 plays, unique 30-second timestamps, and mixed all-day/floating/zoned/weekly Calendar definitions. Large playback inputs are split into supported 100k-record files. Calendar queries and exports use January 2025, not an unbounded recurrence expansion. Memory figures include native/runtime memory and are cumulative across dataset sizes in one harness process; they exclude the desktop GUI. Managed allocation totals in the report measure allocation traffic, not retained heap.

Before optimization, the same quiet 10k harness measured JSON export **51.15 s**, backup **36.24 s**, and restore **36.92 s**. Additive provenance indexes and planner-friendly predicates reduced those final observations to **3.41 s**, **0.93 s**, and **0.92 s**. Query-plan checks confirm indexed provenance lookups and provider/date scans; text search uses FTS row IDs without scanning the provider index. CSV and Calendar timings varied and did not show an improvement at 10k. Broad searches still count/sort many matches, summary counts scale with library size, and bounded Calendar evaluation allocates substantially; these remain useful optimization targets.

## Tests

Iteration 5's final regression suite passes **190 tests, 0 failed, 0 skipped**, with **0 build warnings/errors**. New tests exercise real SQLite message/provenance persistence, cross-method/account identity, revision conflicts, deterministic conversation paging, CSV/JSON export, deletion and restore tombstones, atomic checkpoint rollback, Windows DPAPI encryption, synthetic TDLib authentication/update flows, reconnect/key reuse, rate-limit cancellation, one-pass refresh while paused and privacy exclusions. Provider responses are synthetic: this is not live Telegram authorization or native-ABI verification.

Windows GUI inspection in an isolated synthetic library verified version 0.5.0, all three source statistics, Connected Accounts disclosure/status, Alt+8/9 navigation, imported conversation browsing, message details/provenance and a real persisted conversation search. Authentication was not attempted. The normal personal library was not used for testing. The final self-contained executable was launched, then the user stopped Computer Use with Escape before its final visual recheck; no further GUI actions were taken. Historical platform/installer checks below refer to Iteration 4. An earlier 0.5 Windows installer compiled and passed structural checks; the final installer regeneration was still running at delivery and was not verified complete. No 0.5 installer execution is claimed.

`dotnet test` runs synthetic parser/security tests and real temporary SQLite integration tests, including normalization, nulls, malformed input, ZIP safety, persistence, FTS/date/paging queries, repeated imports, rollback, cancellation, schema checks and interrupted-import recovery. No real personal information is used in fixtures.

The suite also covers export escaping/formula protection, null/time/source/provenance fidelity, all-page streaming, cancellation/output preservation, source/batch/shared-owner deletion, Calendar revision rollback, FTS/FKs, live-WAL snapshots, larger libraries, legacy backup migration, corrupt/unsafe archives, activation fault injection and simulated interrupted-startup recovery. Iteration 4 finished with **162 passed, 0 failed, 0 skipped**, and a build with **0 warnings/errors**. Packaging validation exercises script parsing, path boundaries, checksum replacement, payloads, runtime configuration and archive contents for all three runtime identifiers.

Historical 0.4 Windows checks used published and installed executables with an isolated synthetic library in a path containing spaces and accented characters. The published process loaded its adjacent `coreclr.dll` with `DOTNET_ROOT` pointing to an absent directory; a clean machine without .NET was not available. Checks completed dashboard/version display, native Spotify selection and duplicate reimport, the seven-occurrence mixed timeline, date/text search and event provenance, JSON export with seven records, verified ZIP creation, restore preview/cancellation, healthy SQLite/FTS verification, keyboard focus/shortcuts, maximization and installed-app restart persistence. Native dialog element caching is intermittent; screenshot/keyboard interaction completed those workflows.

During Iteration 4, the per-user NSIS installer was actually executed, reinstalled and launched. Registration/shortcut/metadata were checked; an attempted reinstall while its app was running returned exit 2 and preserved the executable. Uninstallation removed application files/registration/shortcut while retaining the synthetic library and an unknown synthetic file in the installation directory. Historical cross-version upgrades, a clean Windows VM, exhaustive window/text/DPI combinations, screen readers, macOS/Linux GUI and hardware power-loss recovery remain unverified. Destructive GUI restore/removal confirmations were not executed; real service and desktop-command tests cover those operations.

## License

Relliga is licensed under the **GNU General Public License, version 3**. See [LICENSE](LICENSE). Avalonia, CommunityToolkit, Ical.Net, NodaTime and the Microsoft libraries use permissive licenses; bundled native components retain their respective licenses. Relliga is not affiliated with Spotify or Google.
