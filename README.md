# Relliga

**Your digital life, together.**

Relliga is a local-first, open-source desktop application for preserving and exploring personal data exports. Your history stays on your device: no account, telemetry, cloud storage, or external API calls during normal use.

## Implemented in 0.3.0

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
dotnet test --no-build --no-restore
dotnet run --project .\src\Relliga.Desktop\Relliga.Desktop.csproj --no-build
```

After a Debug build, you can also run `C:\Dev\Relliga\src\Relliga.Desktop\bin\Debug\net10.0\Relliga.Desktop.exe` directly with the .NET 10 runtime installed. The adjacent build output files are required. No installer is included yet.

The source targets Windows, macOS and Linux through Avalonia. Native desktop dependencies and platform testing are still required on macOS/Linux; this iteration is validated on Windows only. If a restricted build environment blocks MSBuild worker processes, use `dotnet build --no-restore -m:1 /nodeReuse:false /p:UseSharedCompilation=false`. The test runner requires local named-pipe access.

For isolated development or GUI testing, set `RELLIGA_DATA_DIRECTORY` to a temporary directory outside the repository before launching. Leave it unset for the normal per-user library. Settings displays the actual library path.

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

**Backup format 1** is a ZIP containing exactly `manifest.json` and `library.db`. The manifest records format/schema/application versions, creation time, database length and SHA-256, playback/component/import counts and source names. Format 1 accepts database schemas 1–2 from Relliga versions through 0.3.x; older databases migrate only in staging. Newer versions/schemas, unknown database schema objects, invalid counts, checksum mismatches, corrupt databases and inconsistent FTS/provenance are rejected. SHA-256 detects corruption; it does not authenticate an archive whose database and manifest were both intentionally replaced. Restore only backups you trust.

Archives/database payloads are limited to **2 GiB**, manifests to **64 KiB**, compression ratio to **500:1**, and central directories to **4 KiB / exactly two entries**. ZIP64, archive comments, extra/duplicate entries, paths and links are rejected before activation. Fixed filenames are streamed to isolated, bounded staging; contained paths are never used. Do not manually repackage backups using a tool that adds comments or extra files.

Choose **Preview & restore backup**, select a ZIP, review its validated date/source/count summary and replacement warning, then confirm. This replaces the whole active library; merging is unsupported. The archive is hashed again after confirmation. All app database work is serialized, connections are unpooled and closed, the active WAL is checkpointed, and the candidate is consolidated into one database file before replacement.

Activation requires same-filesystem `File.Replace` support. Relliga flushes the candidate and a fixed recovery journal (`relliga.db.restore-journal`), atomically replaces `relliga.db` while preserving `relliga.db.previous`, checks the activated library, then removes the journal as its commit point. Any failure before that commit rolls back. On startup, a surviving journal triggers conservative rollback to the previous library before normal database access. Unexpected hashes or ambiguous files stop recovery without overwriting them. Keep the database, sidecars, `.previous`, journal and original ZIP if recovery reports an error; do not delete the journal to force startup. A fresh isolated data directory can restore a known healthy ZIP without touching the damaged directory. Owned abandoned staging is cleaned on startup; unrelated staging is preserved.

The successful previous-library copy remains until **Review discarding recovery copy** is confirmed or a subsequent restore replaces it. It may contain information subsequently deleted from the active library. Cancellation is honored before activation; activation/rollback finishes without cancellation. SQLite's synchronous snapshot call cannot be interrupted mid-call, so cancellation may wait for that stage. Filesystem/hardware durability still depends on the OS, storage device and filesystem honoring flush/atomic-replacement guarantees; real power-loss testing has not been performed.

Backups, staging, recovery copies, exports and the active database are **unencrypted**. Protect them as sensitive personal data. Settings verification checks integrity and index consistency using rolled-back FTS integrity commands; it never rebuilds indexes or deletes records automatically.

## Persistence, privacy and limitations

- Windows stores the library at `%LOCALAPPDATA%\Relliga\relliga.db`; other platforms use .NET's per-user LocalApplicationData location. Application data is outside the source checkout. SQLite uses WAL, foreign keys, versioned transactional migrations and indexed queries. Schema version 2 upgrades version 1 libraries without deleting rows, provenance or original importer versions.
- The library is **not encrypted at rest**. Full export metadata may include Spotify usernames, IP addresses or other sensitive information. Protect your device and backups. Original source files are read without modification.
- Prefer the in-app portable backup. For manual filesystem copies, close Relliga and preserve the database together with sidecars and any recovery journal/copy; copying a live database alone is unsafe.
- Playback identity uses media URI, UTC end instant, played duration, account and device, plus multiplicity within the selected input. Title, album, IP and similar metadata changes no longer create new rows when this identity is available. Without a URI, canonical JSON remains the conservative fallback. Spotify exports contain no universal playback event ID: two physically distinct plays with identical identity fields in separate exports remain indistinguishable. Multiplicity is preserved up to the highest observed count, and device/account differences remain distinct. Historical rows already duplicated by version 0.1 are preserved rather than destructively merged during migration. Incoming metadata remains in provenance.
- Preview samples at most 1,000 valid events and does not guarantee the remainder of the input is valid. Progress is record-based feedback rather than a percentage. Large imports still require a bounded in-memory dictionary of event fingerprints.
- Search matches Unicode word prefixes, combining terms with AND. It is not arbitrary substring or advanced query syntax. History is paged in groups of 20; version 0.1 operations display unknown processed/rejected counters honestly. Dashboard Calendar counts represent logical events/series, not an infinite total of recurring occurrences; import counters count decoded components.
- Spotify and Calendar are implemented. There is no live synchronization, encryption, installer, localization or automatic update service yet. Full screen-reader auditing and macOS/Linux GUI verification remain outstanding.

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

## Tests

`dotnet test` runs synthetic parser/security tests and real temporary SQLite integration tests, including normalization, nulls, malformed input, ZIP safety, persistence, FTS/date/paging queries, repeated imports, rollback, cancellation, schema checks and interrupted-import recovery. No real personal information is used in fixtures.

The suite also covers export escaping/formula protection, null/time/source/provenance fidelity, all-page streaming, cancellation/output preservation, source/batch/shared-owner deletion, Calendar revision rollback, FTS/FKs, live-WAL snapshots, larger libraries, legacy backup migration, corrupt/unsafe archives, activation fault injection and simulated interrupted-startup recovery. Windows GUI checks in an isolated synthetic library completed JSON export, ZIP backup creation, health verification, restore preview/cancellation and source-removal preview/cancellation. Final destructive GUI confirmations and post-restore GUI reopening were not executed; real service and desktop-command tests cover those operations. Native dialog automation is intermittent. macOS/Linux GUI and actual hardware power-loss recovery remain unverified. See the delivery report for final test totals.

## License

Relliga is licensed under the **GNU General Public License, version 3**. See [LICENSE](LICENSE). Avalonia, CommunityToolkit, Ical.Net, NodaTime and the Microsoft libraries use permissive licenses; bundled native components retain their respective licenses. Relliga is not affiliated with Spotify or Google.
