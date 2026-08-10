# VENDOR PROVENANCE — nChronic

**This tree is a one-time snapshot of upstream, with Yagu-local edits applied on top.
It has never been refreshed from upstream since it was taken.**

| | |
|---|---|
| Upstream | <https://github.com/robertwilczynski/nChronic> |
| Vendored release | `0.3.2` |
| **Base commit** | **`ca25e7d493c75653dd0fb0b5d20c25db7f847056`** |
| Commit date | 2016-05-09 10:09:12 +0200 |
| Commit subject | `version  @ 0.3.2` |
| Tag at that commit | none — upstream only tags `0.2.1` and `0.3.0` |
| Upstream path mapping | vendored `Chronic/**` == upstream `src/Chronic/**` |

`ca25e7d` is also the last upstream commit to touch `src/Chronic/**` before 2026.

## How this was determined

Every vendored file was hashed as a git blob (both LF and CRLF variants, to neutralize checkout
line-ending conversion) and compared against the blob SHAs of every commit reachable from upstream
`HEAD`. **90 of 98 vendored files are byte-identical** at `ca25e7d`, including `LICENSE` and
`README.markdown`.

The identical score also holds for the 2014 commits preceding `ca25e7d` (the library source did not
change between them) and for upstream `HEAD`. `ca25e7d` is selected because it is the `0.3.2` release
state and the newest upstream commit predating Yagu's own edits.

## Relationship to upstream `HEAD`

Upstream commit `2a5661c` ("Remove dynamic usage so the library works under Native AOT",
merged as `b1a8350`, 2026-07) is a *later, independently refined* version of the Native AOT work that
exists here. It is **not** the base of this snapshot: the vendored files differ from it as well
(e.g. `Tags/GrabberScanner.cs` is +5/-16 against `2a5661c` but +9/-4 against `ca25e7d`).

## Yagu-local divergences from the base commit

- `Chronic/Chronic.csproj` (retargeted to .NET 10, modern SDK project)
- `Chronic/Handlers/Registration/HandlerBuilder.cs`
- `Chronic/Numerizer.cs`
- `Chronic/Tags/GrabberScanner.cs`
- `Chronic/Tags/PointerScanner.cs`
- `Chronic/Tags/SeparatorScanner.cs`
- `Chronic/Tags/Repeaters/DayPortion.cs`
- `Chronic/Tags/Repeaters/RepeaterScanner.cs`

Most of these are the Native AOT hardening: removal of `dynamic` and reflection-based scanner/factory
registration in favour of concrete implementations.

## Deliberately not vendored

`src/Chronic/Properties/AssemblyInfo.cs` (superseded by SDK-generated assembly attributes), the
upstream test project, NuGet packaging, and build scripts.

## Refresh rule

Do not overwrite this directory with a newer upstream checkout. Reconstruct the base commit above,
replay this snapshot on top of it as a local patch layer, then merge the desired upstream revision so
the Yagu-local AOT changes stay visible during conflict resolution.
