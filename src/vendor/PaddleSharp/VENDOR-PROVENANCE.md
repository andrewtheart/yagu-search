# VENDOR PROVENANCE — PaddleSharp

**This tree is a one-time snapshot of upstream, with Yagu-local edits applied on top.
It has never been refreshed from upstream since it was taken.**

| | |
|---|---|
| Upstream | <https://github.com/sdcb/PaddleSharp> |
| Vendored release | `3.0.1` |
| **Base commit** | **`05ea890e37131fcbbc5b86d4c69116c529c915a7`** |
| Commit date | 2025-06-23 17:47:45 +0800 |
| Commit subject | `Merge pull request #133 from sdcb/feature/linux` |
| Tag at that commit | `3.0.1` |

`6fd3b0e15bbab12d4798e21727c65c583ab1803f` ("update document") has a byte-identical tree
(`025efa3b5e1e74fc90bc1460a57b8ad142a4d250`) and is equally valid as the base reference.

## How this was determined

Every vendored file was hashed as a git blob (both LF and CRLF variants, to neutralize checkout
line-ending conversion) and compared against the blob SHAs of every commit reachable from upstream
`HEAD`. Commit `05ea890e` is the unique maximum: **193 of 199 vendored files are byte-identical**
to their upstream counterparts there. The score drops monotonically for every earlier and later
commit.

## Yagu-local divergences from the base commit

These six files differ from upstream `3.0.1` and carry Yagu-specific changes:

- `PaddleSharp.sln`
- `README.md`
- `build/00-common.linq`
- `src/Sdcb.PaddleOCR/Sdcb.PaddleOCR.csproj`
- `src/Sdcb.PaddleOCR.Models.Online/Details/Utils.cs`
- `src/Sdcb.PaddleOCR.Models.Online/Sdcb.PaddleOCR.Models.Online.csproj`

## Deliberately not vendored

The snapshot is sparse. Upstream content intentionally omitted includes the `Sdcb.PaddleNLP*`
projects and their model payloads, the `build/*.nuspec` native runtime packaging files, and the
embedded `Sdcb.PaddleOCR.Models.Local*/models/**` inference payloads.

## Refresh rule

Do not overwrite this directory with a newer upstream checkout. Reconstruct the base commit above,
replay this snapshot on top of it as a local patch layer, then merge the desired upstream release so
the Yagu-local changes stay visible during conflict resolution.
