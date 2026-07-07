---
description: "Yagu settings persistence & search-scope semantics — session-only vs persisted (JsonIgnore) toggles, BinaryExtensions skip-list, empty exclude/directory meaning, semantic snapshot leak guard, and the Yagu.Tests Compile-list requirement. Use when: editing AppSettings, SettingsService, MainViewModel, persist/load settings, JsonIgnore, search-box toggle default, BinaryExtensions, SkipExtensions, exclude globs, all-drives search, semantic snapshot, a new setting field, a new pure helper used by tests."
applyTo: "src/Yagu/Services/SettingsService.cs, src/Yagu/ViewModels/MainViewModel.cs"
---

# Yagu — Settings Persistence & Search-Scope Semantics

`AppSettings` (in `SettingsService.cs`) is System.Text.Json source-generated
(`AppSettingsJsonContext`); a new field serializes automatically with **no** registration. What
matters is deciding whether a field is **persisted** or **session-only**, and preserving the exact
"empty means…" contracts below.

## Session-only vs persisted (`[JsonIgnore]`)

- The four inline search-box toggles are **`[JsonIgnore]` (session-only)** and reset to shipped
  defaults every launch: **CaseSensitive=false, UseRegex=false, ExactMatch=true, Multiline=false**.
  `MultilineSearchDefault` MUST stay `[JsonIgnore]` — persisting it made a multiline session stick
  across restarts/reinstalls (and, via `OnMultilineChanged` force-setting `UseRegex`, lit two toggles
  at once). Any toggle that should NOT survive a restart needs `[JsonIgnore]`.
- `SkipBinary` is also `[JsonIgnore]` (session-only; always defaults to "search-binary OFF" at launch).
- The CLI-command generator compares the live toggle against a **fresh** `new SettingsService().Load()`
  (which yields the fixed `[JsonIgnore]` defaults), so it emits `--multiline` / `--case` only when the
  toggle differs from the shipped default — this stays correct as long as the defaults above hold.

## `BinaryExtensions` is internally a SKIP list

- `AppSettings.BinaryExtensions` (and its universe `SettingsBinaryExtensions`) is the **skip** list,
  even though the dropdown display is flipped to "checked = searched". Keep the flip **only** at the VM
  display boundary; every downstream consumer (`BuildEffectiveSkipExtensionSet`,
  `ParseExtensionSetForCli`, `ExcludedExtensionPredictor`) stays skip-semantics.
- Binary is a **content-skip only, never a listing-skip**: `BuildEffectiveSkipExtensionSet()` folds
  `BinaryExtensions` into the early skip set **only when `SearchMode == Content`**. In Both / FileNames
  / FileNameThenContent it returns just `SkipExtensions`, so binary files stay **listed and findable by
  name**. The CLI never folds binary either (parity). Don't "simplify" this into an unconditional fold.

## "Empty means…" contracts (do not silently substitute defaults)

- **Empty exclude box = NO excludes.** `EffectiveExcludeGlobsText => ExcludeGlobs ?? string.Empty` —
  never fall back to `DefaultExcludeGlobs` when the box is blank. `DefaultExcludeGlobs`
  (`node_modules;bin;obj;.git`) is **placeholder example text only**; a saved copy of that exact string
  migrates to an empty box on load. (The hardcoded `!"\.git\"` exclusion in `FileLister` is a separate
  always-on mechanism — leave it.)
- **Empty Directory = search all eligible drives** (multi-root). `ResolveTargetRoots()` returns
  `[Directory]` when set, else `DriveEnumerator.GetSearchRoots(...)`. Network / removable / cloud roots
  are opt-in (`SearchAllDrivesIncludes{Network,Removable,Cloud}`, default false); a cloud mount is
  excluded by default even when it reports as a Fixed drive.

## Semantic-plan leak guard

The AI/semantic flow mutates VM search inputs (Directory, includeGlobs, `BinaryExtensions`,
`SettingsBinaryExtensions`, …) to run the resolved plan, then restores the user's values afterward.
**Any new search-affecting VM field that the semantic plan can touch MUST be added to the semantic
input snapshot (capture + restore)** — otherwise the AI-resolved value leaks into the next manual
search. `BinaryExtensions` and `SettingsBinaryExtensions` are already captured/restored; follow that
pattern for new fields.

## Testing seam

`Yagu.Tests` pulls engine/helper **source files individually** via `<Compile Include>` (not a
`ProjectReference`). A new pure helper in `Services/` or `Helpers/` that a test needs MUST get a
matching `<Compile Include="..\..\src\Yagu\...">` line in `tests/Yagu.Tests/Yagu.Tests.csproj`, or the
test fails to compile with `CS0103`. `MainViewModel` and `CliRunner` are **not** in the test project,
so their persistence wiring is validated by source-pin tests, not runtime coverage.
