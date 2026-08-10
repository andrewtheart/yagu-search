# VENDOR PROVENANCE — xterm.js

**These are one-time copies of published npm build artifacts.
They have never been refreshed from upstream since they were taken.**

These files are *distribution bundles*, not repository source. They do not exist in the upstream git
tree, so provenance is pinned to the published npm package plus the git tag of that release.

| Vendored file | npm package | Version | Package member |
|---|---|---|---|
| `xterm.js` | `@xterm/xterm` | `5.5.0` | `lib/xterm.js` |
| `xterm.css` | `@xterm/xterm` | `5.5.0` | `css/xterm.css` |
| `addon-fit.js` | `@xterm/addon-fit` | `0.10.0` | `lib/addon-fit.js` |

| | |
|---|---|
| Upstream | <https://github.com/xtermjs/xterm.js> |
| **Base commit (tag `5.5.0`)** | **`9ba6c00a195c95fcf8292a2b9084d91450e5daae`** |
| Commit date | 2024-04-05 07:00:05 -0700 |
| Commit subject | `Merge pull request #5021 from Tyriar/tyriar5_5` |
| `@xterm/xterm@5.5.0` integrity | `sha512-hqJHYaQb5OptNunnyAnkHyM8aCjZ1MEIDTQu1iIbbTD/xops91NB5yq1ZK/dC2JDbVWtF23zUtl9JE2NqwT87A==` |
| `@xterm/addon-fit@0.10.0` integrity | `sha512-UFYkDm4HUahf2lnEyHvio51TNGiLK66mqP2JoATy7hRZeXaGMRDr00JiSF7m63vR5WKATF605yEggJKsw0JpMQ==` |

## How this was determined

Each stable `@xterm/xterm` and `@xterm/addon-fit` release tarball was downloaded from the npm
registry and the relevant member compared byte-for-byte (CR-insensitive) against the vendored file:

- `xterm.js` matches `@xterm/xterm@5.5.0` **only** — it differs from `5.4.0` and `6.0.0`.
- `xterm.css` matches `5.4.0` and `5.5.0` (the stylesheet is unchanged between them).
- `addon-fit.js` matches `@xterm/addon-fit@0.9.0` and `0.10.0` (byte-identical dist output).
  `0.10.0` is recorded because it was published in the same minute as `@xterm/xterm@5.5.0`
  (2024-04-05) and is the release paired with it.

## Yagu-local divergences

None. The three files are unmodified published artifacts.

`src/Yagu/Assets/terminal.html` consumes only the public `Terminal`, `FitAddon.FitAddon`, and
`loadAddon` APIs. Do not introduce dependencies on private `_core` internals — that would make future
version bumps unsafe.

## Refresh rule

Replace all three files together, from a single matched release pair of `@xterm/xterm` and
`@xterm/addon-fit`. Copy `lib/xterm.js`, `css/xterm.css`, and `lib/addon-fit.js` out of the official
npm tarballs and record the new versions, commit, and integrity hashes here.
