---
description: "Yagu on-device semantic (natural-language) search subsystem. Use when: editing Services/Ai, FoundryLocalSemanticQueryTranslator, FoundryModelSelector, SemanticPlanApplier, SemanticPlanJsonExtractor, semantic plan, NL to search, model selection, model context/memory guard, SemanticSearchSystemPrompt, Foundry Local."
applyTo: "Yagu/Services/Ai/**"
---

# Yagu — Semantic (NL→search) Subsystem

On-device natural-language search: a query is translated to a JSON search plan by a small local model
via Microsoft Foundry Local, then applied to the normal search inputs.

## Hard invariants

- **On-device only.** The query MUST NOT leave the machine. Never add a network call that sends the
  user's query anywhere. (Model/EP files download on first use; the query never does.)
- **Semantic search must not mutate persisted defaults.** `MainViewModel` snapshots the search
  filter fields before `SemanticPlanApplier.ApplyToTarget` and restores + persists the user's saved
  defaults afterward (the model-resolved **Directory** is the one deliberate exception that persists).
  Any new search-affecting VM field must be added to the snapshot (`SemanticSearchInputSnapshot`,
  capture/restore) **and** the `PersistSettingsAsync` guard, or it will leak the plan value to disk.

## Pipeline

`FoundryLocalSemanticQueryTranslator` (loads model, runs inference) → `SemanticPlanJsonExtractor`
(extracts/repairs the model's JSON) → `SemanticPlanApplier` (`Resolve` → `ResolvedSearchPlan`,
normalizes + applies to an `ISemanticPlanTarget`). The deterministic UI summary comes from
`SemanticPlanApplier.BuildExplanation`, **not** the model's free-text `explanation`.

## Small local models are unreliable — fix in code, not the prompt

Prompt rules alone do not constrain a 0.5–4B on-device model. The durable pattern here is a **pure,
unit-tested helper** in `SemanticPlanApplier` / `SemanticPlanJsonExtractor` that corrects the output
deterministically (existing examples: language-name→extension, explicit `.ext files`, extension
depluralization, hidden-files toggle, image-OCR enablement, integer-multiplication folding,
`<think>` reasoning-trace stripping). When the model gets something wrong, add a helper + tests;
change the prompt only as belt-and-suspenders.

- The system prompt `Prompts/SemanticSearchSystemPrompt.prompt.md` is an **`<EmbeddedResource>`** —
  editing it requires a **rebuild** to take effect; its YAML front matter is stripped before sending.

## Model selection (device / memory / context / modality)

- Trust the **variant Id**, not Foundry's reported `DeviceType` (DirectML registers on almost any
  box, so it reports GPU for everything). Resolve device with `FoundryModelSelector.ResolveVariantDevice`.
- Exclude variants for absent GPU/NPU (via `ISemanticCapabilityDetector.HasGpu/HasNpu`), models that
  don't fit RAM (`ModelMemoryBudget.Fits`), models whose context window is too small
  (`ModelContextBudget` + `GenAiConfigReader` reading `genai_config.json`), and non-chat models
  (whisper/embed/vision) **by alias, not just Task**. Auto-select also excludes reasoning models
  (`IsAutoSelectable`); an explicit user override deliberately bypasses these guards.

## Testing

Pure helpers (`SemanticPlanApplier`, `SemanticPlanJsonExtractor`, `ModelMemoryBudget`,
`ModelContextBudget`, `GenAiConfigReader`, `SemanticPromptText`, `FoundryModelUpdateChecker`,
`SlowSemanticModelAdvisor`) are compiled into Yagu.Tests → real unit tests (add new ones to
`Yagu.Tests.csproj`'s `<Compile Include>` list). `FoundryLocalSemanticQueryTranslator` and
`FoundryModelSelector` pull in Foundry/WinML types → **not** in Yagu.Tests → **source-pin only**.

The GPU dev box cannot exercise the CPU-only / low-RAM / clean-machine paths — those are validated on
a clean Windows Sandbox (they also depend on the app-local VC++ runtime; see the installer instruction).
