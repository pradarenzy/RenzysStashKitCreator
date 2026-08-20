# Renzy's Stash Kit Maker

A local-only, non-destructive Windows desktop application for mining external samples referenced by FL Studio projects and planning clean, deduplicated personal stash kits.

## Current implementation

This repository contains a native WPF workflow, FLP selection/folder discovery, a replaceable read-only parser, sampler-aligned pattern MIDI extraction, layered path resolution, SHA-256 exact deduplication with provenance, explainable filename/channel classification, conservative loop/song/tag exclusion, variants/BPM/key preservation, deterministic curated kawaii rōmaji naming, Windows collision validation, explicit build approval, byte-preserving copy and post-copy hash verification, manifests, persistent settings/history, safe generated-kit merging, Unknown review/sorting, and local audio playback.

Analysis code never creates a destination. `KitBuilder.BuildAsync` requires explicit approval, refuses unrecognized non-empty roots, and validates case-insensitive collisions before its first write. Recognized generated kits can be merged only when merge mode is enabled. It copies bytes without audio decoding or metadata edits, compares destination SHA-256 with source SHA-256, and records only successful copies in the manifest.

## Architecture

- `src/StashKitMaker.Core`: dependency-free models and services. UI code does not parse FLPs or copy files.
- `src/StashKitMaker.App`: .NET 6 WPF Windows UI.
- `tests/StashKitMaker.Tests`: dependency-free executable integration test suite using synthetic bytes.
- `outputs`: user-facing packaged artifacts.

The parser is behind `IFlpParser`. `EventStreamFlpParser` validates `FLhd`/`FLdt`, walks typed FLP events, and interprets known tempo, pattern, channel-name, sampler-path, and pattern-note records. It groups every supported note block by pattern and sampler channel, preserving PPQ, position, length, key, velocity, and MIDI channel. Unknown events are skipped. Every successful scan is marked `Partial` because plugin-owned notes, automation, playlist context, and future/unknown structures are not claimed as supported. The app never saves an FLP. This is informed by the unofficial FLP event model documented by PyFLP and the clean-room FLP format research from flpdiff; Image-Line does not publish a complete native format specification.

The Stacks page lists those independent pattern/channel sequences as Standard MIDI files, grouped by the category of the channel's referenced sample. Its sampler preview plays every note at the original FL timing and velocity, transposes the matched sample by MIDI key with C5 as the root, and makes each new note choke/restart the preceding hit. MIDI extraction is non-overwriting, persists between tab changes and restarts, and leaves the project, source sound, and any pre-existing MIDI untouched. Export copies the selected or filtered MIDI plus its matching sounds into category folders and writes a hash-backed `midi-stack.json` manifest.

Known compatibility: the event envelope and sampler path IDs are known from FL Studio 11.5–21-era community implementations. The parser has not been validated here against a licensed corpus of real FLPs, and FL Studio 2024/2025 changes are not claimed. Unsupported/corrupt projects fail individually without aborting a batch.

Path resolution tries absolute stored paths, project-relative paths, then exact filename searches of configured roots. Multiple matches remain `Ambiguous`; none become `Missing`. Hashes are computed only for resolved files. Exact duplicates share one identity while retaining all source paths and projects. Near-duplicate merging is intentionally absent.

Classification uses boundary-aware tokens from filename and FL channel context, with weighted evidence and a confidence score. Channel evidence outweighs filename evidence. Uncertain items become `Unknown`; no audio-aware classifier is claimed yet. Variant, BPM, and key extraction is conservative and filename-based. Key/BPM audio detection is not implemented.

Names are deterministic from the full SHA-256 value plus dictionary version `kawaii-v3`. They use curated compatible families—cute modifier plus animal, established nature phrases, recognized food combinations, colors, or sound-symbolic forms—while preserving confident type/variant/key/BPM labels. Original branding is excluded by construction, Windows names are sanitized, and collisions are resolved case-insensitively.

Drumkits analysis enumerates directory names read-only and returns recurring patterns. Review can play/pause/stop Unknown WAV/MP3 files, manually move them to a category with a category-correct name, and update the manifest/hash index. A completed build opens the existing Build Kit tab, where a selector lists marker-valid kits discovered from the configured kit folder and history. The manifest-backed hierarchy remains live across navigation, and every sound remains individually renameable with one-at-a-time play/stop controls.

The Build Kit tab also stages real FL Studio Browser metadata. One exact `#RRGGBB` base colour drives a deterministic HSL hierarchy: the root preserves the selected colour, direct children use 60% of its saturation and move 15% toward white, and deeper entries use 30% of its saturation and move 30% toward white. The preview displays both web RGB and FL Studio `$BBGGRR`. Folder names, parent relationships, native numeric `IconIndex`, `HeightOfs`, `SortGroup`, and optional tooltip remain staged until **Apply to Kit** is pressed. Apply validates stable IDs and Windows-safe names, safely renames or moves the real generated folders, updates manifest paths, and writes same-named `.nfo` sidecars beside the root and every folder. It never generates `Bitmap=` metadata or presents Unicode as an FL Studio native icon. Committed folder state is stored in `_metadata/kit-state.json`; old kit-state versions load with safe defaults. Later builds and Unknown sorting honor committed custom category paths.

History stores builds locally, opens kits in Explorer, and can send only marker-validated generated kits to the Windows Recycle Bin. SQLite caching, learned audio-feature classification, resumable builds, near-duplicate review, and installer polish remain future work.

## Build and test

Requires Windows 10/11 x64 and the .NET 6 SDK.

```powershell
dotnet build StashKitMaker.sln -c Release
dotnet run --project tests/StashKitMaker.Tests -c Release
dotnet publish src/StashKitMaker.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o outputs/RenzysStashKitMaker
```

Run `outputs/RenzysStashKitMaker/RenzysStashKitMaker.exe`. The self-contained package does not require a separate .NET Desktop Runtime install.

## Privacy and limits

There is no network code, telemetry, cloud classifier, destructive source operation, arbitrary overwrite, or audio re-encoding. Exported provenance is controlled by `IncludePrivateProvenance`; operational history and processed hashes are stored separately under the user's local application-data directory. Kit deletion is explicit, marker-gated, and recoverable through the Windows Recycle Bin. Advanced audio classification, a broad real-FLP compatibility corpus, SQLite-scale indexing, and an installer are not yet complete.

Research references: [Image-Line FLP documentation](https://www.image-line.com/fl-studio-learning/fl-studio-online-manual/html/fformats_open_flp.htm), [PyFLP](https://pyflp.readthedocs.io/en/latest/), and [flpdiff format research](https://github.com/dawhubapp/flpdiff/blob/main/docs/fl-format/flp-format-spec.md).
