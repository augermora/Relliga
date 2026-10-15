# Relliga

**Your digital life, together.**

Relliga is a local-first, open-source desktop application for preserving and exploring personal data exports. Your history stays on your device: no account, telemetry, cloud storage, or external API calls during normal use.

## Implemented in 0.4.0

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

Domain and Application do not depend on Avalonia, SQLite or Ical.Net. Infrastructure queries use the application-owned recurrence contract; the desktop composition root supplies its implementation. Both sources share import orchestration, querying, paging, history and UI navigation.

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

Outputs are under `artifacts/distribution/0.4.0/`: `Relliga-0.4.0-win-x64-portable/`, its `.zip`, `Relliga-0.4.0-win-x64-installer.exe`, and `SHA256SUMS.txt`. Use `Get-FileHash -Algorithm SHA256` to compare an artifact against that file. Repeat with `-Replace` to replace generated output deliberately. `-OutputRoot` supports spaces/Unicode but must remain inside repository `artifacts/`; linked output directories are rejected. ZIPs and installers include GPLv3, runtime/native notices, dependency inventory and package license/attribution files. These are reproducible build commands, not a claim of byte-identical ZIP/installer output across timestamps, SDKs or toolchains.

Before sharing GPLv3 binaries externally, provide the complete corresponding source for the exact build, including local modifications and packaging scripts. Local development artifacts are not automatically published or accompanied by a source offer. Generated binaries, datasets and build-tool downloads stay ignored by Git. Python is needed only to regenerate the checked-in icon via `scripts/Generate-AppIcon.py`.

To validate Windows distribution manually:

1. Compare the package SHA-256 with `SHA256SUMS.txt`. Extract the portable ZIP, keep its files together, and open `Relliga.exe`. Alternatively run `Relliga-0.4.0-win-x64-installer.exe`, review the GPLv3 page, complete installation, and open Relliga from the Start Menu. The installer does not launch it automatically.
2. For a disposable test, launch from PowerShell with `$env:RELLIGA_DATA_DIRECTORY = Join-Path $env:TEMP ('Relliga-QA-' + [guid]::NewGuid().ToString('N'))` and `& "$env:LOCALAPPDATA\Programs\Relliga\Relliga.exe"`, or use the extracted executable's full path. Confirm that Settings shows this isolated location and version 0.4.0. Keep this same environment value for subsequent launches.
3. Follow the synthetic-data walkthrough below, including search, export, backup, restore preview and health. Tab through controls, use Ctrl+F/Ctrl+I/Ctrl+E and Alt+1…7, resize/maximize/restore the window, and inspect long details. Check 100%, 150% and 200% DPI and screen-reader output on suitable test machines; those combinations are not certified here.
4. Close Relliga and reopen it with the same temporary directory; counts and records must persist. With the installed app running, try the installer again: it must ask you to close Relliga before changing files. Close it and reinstall; the shortcut, version and library must remain correct. A historical cross-version installer upgrade remains a separate release validation task.
5. Close Relliga and uninstall through Windows Installed apps or `%LOCALAPPDATA%\Programs\Relliga\Uninstall.exe`. Application files, shortcut and registration should disappear; the separate library and external backups/exports must remain. Extracted portable files have no registration; removing that application folder does not remove the separate library. Never manually remove personal libraries as part of ordinary application cleanup.

## Platform preparation and keyboard access

Windows x64 is the primary verified desktop target (this development machine runs Windows build 26300). Linux x64 and macOS ARM64 can be cross-published with the same script using `-RuntimeIdentifier linux-x64` or `osx-arm64`. Run `Test-Distribution.ps1 -RuntimeIdentifiers win-x64,linux-x64,osx-arm64` after generating all three. Cross-publish output is preparation, not native GUI validation or an installer. On Linux, preserve executable permissions (`chmod +x Relliga`) after ZIP extraction; the native X11/fontconfig/ICU/OpenGL requirements depend on the distribution. macOS output is a raw unsigned directory, not a notarized `.app` or installer. Consult [Avalonia's supported platforms](https://docs.avaloniaui.net/docs/supported-platforms) and [.NET publishing guidance](https://learn.microsoft.com/dotnet/core/deploying/) for platform prerequisites. No Linux/macOS GUI verification is claimed from Windows.

Use **Alt+1…7** for the seven navigation sections, **Ctrl+F** for Search, **Ctrl+I** for Import and **Ctrl+E** for Library & export. Search receives keyboard focus; Enter applies its query. Tab/Shift+Tab traverse controls; arrow keys operate navigation lists and selectors. Escape requests cancellation when an operation/query permits it. Confirmation dialogs initially focus Cancel, make Enter default to Cancel, and name their destructive action explicitly. Status/error regions use Avalonia live-region metadata. Per-monitor DPI awareness and wrapping layouts are configured; full screen-reader, text-scaling and WCAG conformance audits remain outstanding.

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

Open **Library & export**, choose JSON or CSV and a scope, then **Choose destination & export**. Scopes cover all Spotify playback, Calendar, both sources, or the last successfully applied Timeline/Search filters. The current-results scope freezes applied text/source/type/date filters, including the effective Calendar window; unapplied edits do not change it. All matches are exported, including records beyond the current page, in the same descending timeline order. Calendar exports contain occurrences in the bounded window; use a backup to preserve the complete library, including definitions outside that window and cancellation components.

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

**Backup format 1** is a ZIP containing exactly `manifest.json` and `library.db`. The manifest records format/schema/application versions, creation time, database length and SHA-256, playback/component/import counts and source names. Format 1 accepts database schemas 1–3 from Relliga versions through 0.4.x; older databases migrate only in staging. Newer versions/schemas, unknown database schema objects, invalid counts, checksum mismatches, corrupt databases and inconsistent FTS/provenance are rejected. SHA-256 detects corruption; it does not authenticate an archive whose database and manifest were both intentionally replaced. Restore only backups you trust.

Archives/database payloads are limited to **2 GiB**, manifests to **64 KiB**, compression ratio to **500:1**, and central directories to **4 KiB / exactly two entries**. ZIP64, archive comments, extra/duplicate entries, paths and links are rejected before activation. Fixed filenames are streamed to isolated, bounded staging; contained paths are never used. Do not manually repackage backups using a tool that adds comments or extra files.

Choose **Preview & restore backup**, select a ZIP, review its validated date/source/count summary and replacement warning, then confirm. This replaces the whole active library; merging is unsupported. The archive is hashed again after confirmation. All app database work is serialized, connections are unpooled and closed, the active WAL is checkpointed, and the candidate is consolidated into one database file before replacement.

Activation requires same-filesystem `File.Replace` support. Relliga flushes the candidate and a fixed recovery journal (`relliga.db.restore-journal`), atomically replaces `relliga.db` while preserving `relliga.db.previous`, checks the activated library, then removes the journal as its commit point. Any failure before that commit rolls back. On startup, a surviving journal triggers conservative rollback to the previous library before normal database access. Unexpected hashes or ambiguous files stop recovery without overwriting them. Keep the database, sidecars, `.previous`, journal and original ZIP if recovery reports an error; do not delete the journal to force startup. A fresh isolated data directory can restore a known healthy ZIP without touching the damaged directory. Owned abandoned staging is cleaned on startup; unrelated staging is preserved.

The successful previous-library copy remains until **Review discarding recovery copy** is confirmed or a subsequent restore replaces it. It may contain information subsequently deleted from the active library. Cancellation is honored before activation; activation/rollback finishes without cancellation. SQLite's synchronous snapshot call cannot be interrupted mid-call, so cancellation may wait for that stage. Filesystem/hardware durability still depends on the OS, storage device and filesystem honoring flush/atomic-replacement guarantees; real power-loss testing has not been performed.

Backups, staging, recovery copies, exports and the active database are **unencrypted**. Protect them as sensitive personal data. Settings verification checks integrity and index consistency using rolled-back FTS integrity commands; it never rebuilds indexes or deletes records automatically.

## Persistence, privacy and limitations

- Windows stores the library at `%LOCALAPPDATA%\Relliga\relliga.db`; other platforms use .NET's per-user LocalApplicationData location. Application data is outside the source checkout. SQLite uses WAL, foreign keys, versioned transactional migrations and indexed queries. Schema 3 upgrades schemas 1/2 without deleting rows, provenance or original importer versions. Version 0/unrecognized nonempty schemas are rejected before initialization.
- The library is **not encrypted at rest**. Full export metadata may include Spotify usernames, IP addresses or other sensitive information. Protect your device and backups. Original source files are read without modification.
- Prefer the in-app portable backup. For manual filesystem copies, close Relliga and preserve the database together with sidecars and any recovery journal/copy; copying a live database alone is unsafe.
- Playback identity uses media URI, UTC end instant, played duration, account and device, plus multiplicity within the selected input. Title, album, IP and similar metadata changes no longer create new rows when this identity is available. Without a URI, canonical JSON remains the conservative fallback. Spotify exports contain no universal playback event ID: two physically distinct plays with identical identity fields in separate exports remain indistinguishable. Multiplicity is preserved up to the highest observed count, and device/account differences remain distinct. Historical rows already duplicated by version 0.1 are preserved rather than destructively merged during migration. Incoming metadata remains in provenance.
- Preview samples at most 1,000 valid events and does not guarantee the remainder of the input is valid. Progress is record-based feedback rather than a percentage. Large imports still require a bounded in-memory dictionary of event fingerprints.
- Search matches Unicode word prefixes, combining terms with AND. It is not arbitrary substring or advanced query syntax. History is paged in groups of 20; version 0.1 operations display unknown processed/rejected counters honestly. Dashboard Calendar counts represent logical events/series, not an infinite total of recurring occurrences; import counters count decoded components.
- Spotify and Calendar are implemented. There is no live synchronization, encryption, localization or automatic update service. Windows development packages are unsigned. Full screen-reader auditing and macOS/Linux GUI verification remain outstanding.

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

## Measured performance

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

`dotnet test` runs synthetic parser/security tests and real temporary SQLite integration tests, including normalization, nulls, malformed input, ZIP safety, persistence, FTS/date/paging queries, repeated imports, rollback, cancellation, schema checks and interrupted-import recovery. No real personal information is used in fixtures.

The suite also covers export escaping/formula protection, null/time/source/provenance fidelity, all-page streaming, cancellation/output preservation, source/batch/shared-owner deletion, Calendar revision rollback, FTS/FKs, live-WAL snapshots, larger libraries, legacy backup migration, corrupt/unsafe archives, activation fault injection and simulated interrupted-startup recovery. Iteration 4 finished with **162 passed, 0 failed, 0 skipped**, and a build with **0 warnings/errors**. Packaging validation exercises script parsing, path boundaries, checksum replacement, payloads, runtime configuration and archive contents for all three runtime identifiers.

Actual Windows checks used published and installed executables with an isolated synthetic library in a path containing spaces and accented characters. The published process loaded its adjacent `coreclr.dll` with `DOTNET_ROOT` pointing to an absent directory; a clean machine without .NET was not available. Checks completed dashboard/version display, native Spotify selection and duplicate reimport, the seven-occurrence mixed timeline, date/text search and event provenance, JSON export with seven records, verified ZIP creation, restore preview/cancellation, healthy SQLite/FTS verification, keyboard focus/shortcuts, maximization and installed-app restart persistence. Native dialog element caching is intermittent; screenshot/keyboard interaction completed those workflows.

The per-user NSIS installer was actually executed, reinstalled and launched. Registration/shortcut/metadata were checked; an attempted reinstall while its app was running returned exit 2 and preserved the executable. Uninstallation removed application files/registration/shortcut while retaining the synthetic library and an unknown synthetic file in the installation directory. Historical cross-version upgrades, a clean Windows VM, exhaustive window/text/DPI combinations, screen readers, macOS/Linux GUI and hardware power-loss recovery remain unverified. Destructive GUI restore/removal confirmations were not executed; real service and desktop-command tests cover those operations.

## License

Relliga is licensed under the **GNU General Public License, version 3**. See [LICENSE](LICENSE). Avalonia, CommunityToolkit, Ical.Net, NodaTime and the Microsoft libraries use permissive licenses; bundled native components retain their respective licenses. Relliga is not affiliated with Spotify or Google.
