# VENDOR PROVENANCE — SharpSevenZip

**This tree is a one-time snapshot of upstream, with Yagu-local edits applied on top.
It has never been refreshed from upstream since it was taken.**

| | |
|---|---|
| Upstream | <https://github.com/JeremyAnsel/SharpSevenZip> |
| **Base commit** | **`04e40b664808a5b41858f390dd1ac250974183ea`** |
| Commit date | 2026-04-27 19:25:26 +0200 |
| Commit subject | `Merge pull request #18 from IsaMorphic/feature/impl-native-aot` |
| Tag at that commit | none — the upstream repository has no tags |
| `<Version>` in csproj at that commit | `2.0.0` |
| NuGet packages published that day | `2.0.42`, `2.0.45` (CI publishes `2.0.<build>`) |
| Bundled native library | 7-Zip `26.00` (`SharpSevenZip/SharpSevenZip/{x64,x86}/7z.dll`) |

`c2dc99ee9520efafb17c42cf672b236a6e0de2cc` ("Fix: reimplement support for .NET Framework and .NET
Standard") is the merged branch tip and has a byte-identical tree
(`70fb3b255179bb2045693664e708e96838236afc`).

## How this was determined

Every vendored file was hashed as a git blob (both LF and CRLF variants, to neutralize checkout
line-ending conversion) and compared against the blob SHAs of every commit reachable from upstream
`HEAD`. Commit `04e40b66` is the unique maximum: **79 of 97 vendored files are byte-identical** to
their upstream counterparts there. The score drops monotonically for every earlier and later commit.

## Yagu-local divergences from the base commit

These 18 files differ from upstream and carry Yagu-specific hardening (bounds/overflow checks, path
traversal rejection, offset-aware stream handling, native-library search-path restrictions,
buffer clearing, AES-256 default, non-imposition of Yagu analyzer settings):

- `SharpSevenZip/SharpSevenZip/ArchiveEmulationStreamProxy.cs`
- `SharpSevenZip/SharpSevenZip/ArchiveExtractCallback.cs`
- `SharpSevenZip/SharpSevenZip/ArchiveFileInfo.cs`
- `SharpSevenZip/SharpSevenZip/CallbackBase.cs`
- `SharpSevenZip/SharpSevenZip/ExtractStream.cs`
- `SharpSevenZip/SharpSevenZip/FileChecker.cs`
- `SharpSevenZip/SharpSevenZip/Lzma/LzmaDecodeStream.cs`
- `SharpSevenZip/SharpSevenZip/Lzma/LzmaEncodeStream.cs`
- `SharpSevenZip/SharpSevenZip/NativeMethods.cs`
- `SharpSevenZip/SharpSevenZip/Sdk/Compression/Lz/InWindow.cs`
- `SharpSevenZip/SharpSevenZip/Sdk/Compression/Lzma/Decoder.cs`
- `SharpSevenZip/SharpSevenZip/Sdk/Compression/RangeCoder/Decoder.cs`
- `SharpSevenZip/SharpSevenZip/SharpSevenZip.csproj`
- `SharpSevenZip/SharpSevenZip/SharpSevenZipCompressor.cs`
- `SharpSevenZip/SharpSevenZip/SharpSevenZipExtractorAsynchronous.cs`
- `SharpSevenZip/SharpSevenZip/SharpSevenZipLibraryManager.cs`
- `SharpSevenZip/SharpSevenZip/SharpSevenZipSfx.cs`
- `SharpSevenZip/SharpSevenZip/StreamWrappers.cs`

## Deliberately not vendored

Upstream tests (`SharpSevenZip.Tests/**` and their archive payloads), the docfx `Documentation`
project, the solution file, `appveyor.yml`, and repo dotfiles.

## Refresh rule

Do not overwrite this directory with a newer upstream checkout. Reconstruct the base commit above,
replay this snapshot on top of it as a local patch layer, then merge the desired upstream revision so
the Yagu-local security changes stay visible during conflict resolution.
