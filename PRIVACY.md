# Yagu — Privacy Policy

_Last updated: 2026-07-30_

Yagu ("Yet Another Grep Utility") is a desktop search tool that runs entirely on
your own Windows PC. This policy explains, in plain language, exactly what Yagu
does and does not do with your data.

**The short version:** Everything runs on-device. Your files, file contents,
directory names, and search queries never leave your PC. Yagu does not have
accounts, does not require sign-in, and does not sell or share personal data with
anyone. The only network features are optional and are described below.


## What stays on your machine (everything you search)

All of Yagu's core work happens locally and is never transmitted anywhere:

- **Search queries and results** — the text you search for and the files/lines it
  matches stay on your PC.
- **File names, paths, directory names, and file contents** — read locally to
  produce results; never uploaded.
- **Semantic (natural-language) AI search** — when you describe a search in plain
  English, a small model running locally through Microsoft Foundry Local
  translates it into concrete search options. **The query is never sent over the
  network.** The model file itself is downloaded once from Microsoft (see
  "Model downloads" below), but your queries are not.
- **Image-text (OCR) and PDF-text extraction** — performed on-device by bundled
  tools; the images, PDFs, and extracted text never leave your PC.
- **The optional content index** — an on-device accelerator stored locally.
  Index files, diagnostics, and query logs stay on the machine.
- **The Yagu log file** — written locally for troubleshooting. It is not sent
  anywhere unless you explicitly submit a bug report (see below).


## Optional network features (all off or opt-in by default)

Yagu makes **no** network requests for searching. The only features that can use
the network are the following, and each is either off by default or asks first:

### 1. Anonymized diagnostics (telemetry) — OFF until you opt in

- Disabled by default. When you turn it on, Yagu may send a small batch of
  anonymized, **path-scrubbed** error summaries and performance measurements
  (for example, startup time).
- It **never** includes file paths, directory names, file contents, search
  queries, or personal data — any filesystem path found in an error message is
  redacted before anything is sent.
- The only identifier tied to your install is a **random GUID** generated once,
  used solely to count distinct installs.
- Diagnostics travel to a self-hosted proxy endpoint, **not** to any third party.
  If the build you are running has no endpoint configured, this feature is
  completely inert and makes no network calls regardless of the toggle.
- Command-line / headless runs never send anything.

### 2. Bug reports — OFF until you opt in, and review-before-send

- Disabled by default. When enabled, if Yagu hits a critical error it opens a
  dialog showing you **exactly** what would be submitted — the error and stack
  trace, GPU/NPU details, a copy of your `settings.json`, and a tail of your log
  file — plus an optional comment box.
- **Nothing is sent unless you review the contents and click Submit.**

### 3. Application update checks — asks once

- Yagu can check GitHub for a newer version. By default it **asks once** before
  making any network request; you can set it to automatic or off.
- An update check contacts GitHub's public release metadata only. No personal
  data is sent.

### 4. Model downloads and new-model notices (semantic AI only)

- The first time you run a semantic (AI) search, the local model is downloaded
  once from Microsoft Foundry Local. Only users who have used semantic search
  are affected; the model is never downloaded on its own.
- If enabled, Yagu may check the Foundry Local catalog about once a day to notify
  you when a new on-device model is available. This contacts Microsoft's catalog
  service only and sends no personal data or queries.

These telemetry, bug-report, and update settings can be reviewed and changed at
any time in **Settings ▸ Privacy** (and the update behavior under app updates).


## Data retention and third parties

- Yagu keeps your data on your device. Uninstalling removes the application; your
  own files are untouched.
- Yagu has no user accounts and does not use advertising or third-party analytics
  SDKs. It does not sell or share personal data.
- The optional diagnostics/bug-report endpoint is a self-hosted proxy operated for
  the project, not a third-party data broker.


## Children's privacy

Yagu is a general-purpose developer/utility tool and is not directed at children.
It collects no personal information by default.


## Changes to this policy

If this policy changes, the updated version will ship with the corresponding
release of Yagu (this file) and be viewable from **Help** inside the app.


## Contact

Questions about privacy can be raised on the project's issue tracker at
<https://github.com/andrewtheart/yagu-search>.
