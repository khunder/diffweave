# AI-assisted merging: extension points (design)

> **Status: NOT IMPLEMENTED.** This document describes how assisted-merge features could be added *later*, as
> plugins, on top of the extension API that exists today. DiffWeave itself contains no model calls, no AI
> dependencies, no network code and no prompt templates. The only shipped merge assistant is the deterministic
> whitespace sample in `samples/`.

## 1. Goals and non-goals

Goals:

* Let third parties add assistants that **explain** and **propose** — never silently apply — merge resolutions.
* Keep the core deterministic and offline. Every assisted feature is a plugin the user installs and enables.
* Make data flow explicit: the user always knows what leaves the machine, to whom, and why.
* Make every assisted change reviewable, undoable and auditable.

Non-goals:

* No built-in provider, no bundled model, no default endpoint, no telemetry.
* No automatic acceptance of proposals, not even above a confidence threshold.

## 2. Future use-cases

| Use-case | Input (from `MergeAssistantRequest`) | Output (`MergeAssistantResult`) |
| --- | --- | --- |
| **Explain a conflict** | one target region; base/left/right text around it | a proposal with *no* replacement (or diagnostics) whose `Rationale` explains what each side changed and why they clash |
| **Propose a resolution** | target conflict regions; optional `UserInstruction` ("keep both log lines") | one `ResolutionProposal` per region: replacement lines, rationale, confidence |
| **Semantic / rename-aware merge** | whole snapshot (a rename on one side and an edit on the other touch many regions) | proposals for several regions that are meant to be accepted together (grouping via a shared option/diagnostic) |
| **Risk review of an auto-merge** | all regions with `IsResolved` set by auto-merge (`LeftChanged`, `RightChanged`, `BothChangedSame`) | diagnostics with severity per region ("left renamed `foo` but right added a new call to `foo`") and no proposals |
| **Merge commit message summary** | final output + regions | a diagnostic carrying the suggested message; the git integration could offer it to the user |

Each use-case maps onto the existing contracts:

* `IMergeAssistant` with `Capabilities` (`ProposeResolutions`, `ExplainConflicts`, `RequiresUserInstruction`,
  `UsesNetwork`, `Deterministic`),
* `CommandContribution` for entry points ("Explain this conflict", "Review auto-merge"),
* `IEditorDecorationProvider` for inline risk markers produced by a review,
* `IMergeLifecycleHook.BeforeSaveAsync` for a "review before save" gate,
* `ISettingsStore` for per-assistant configuration (provider, model name, limits — all owned by the plugin).

## 3. Request / response data flow

```
 User action (command, conflict context menu, "Assist" panel)
        │
        ▼
 App builds MergeAssistantRequest
   Snapshot = MergeSession.CreateSnapshot(names, languageId)      ← immutable copy
   TargetRegionIds = [selected regions]  (empty = all unresolved conflicts)
   UserInstruction = optional text typed by the user
   Options = assistant settings
        │
        ▼
 Consent gate (only if Capabilities has UsesNetwork)  ──── denied ──▶ stop, nothing sent
        │ granted
        ▼
 Redaction hooks (§4.2) produce the outbound view of the request
        │
        ▼
 Budget check (§5)  ──── over budget ──▶ ask the user to narrow the selection
        │
        ▼
 MergeAssistantRunner.RunAsync(assistant, request, timeout)       ← thread pool, timeout, exception isolation
        │
        ▼
 Validation: unknown/duplicate regions dropped, confidence clamped, stale check (§6.4)
        │
        ▼
 Preview UI (§6) ── accept / edit / reject per proposal
        │ accepted
        ▼
 MergeSession.ApplyProposals(accepted)                             ← one undo step
        │
        ▼
 Audit log entry (§7)
```

The assistant never receives a reference to live editor state: only the immutable snapshot. It cannot modify the
merge; the only way to change the output is through proposals the user accepts.

## 4. Consent and privacy model

### 4.1 Consent

* **No silent network.** An assistant that sends any data off the machine must declare
  `MergeAssistantCapabilities.UsesNetwork`. The host treats an undeclared network use as a plugin bug; plugins
  distributed through any future catalogue would be reviewed for it.
* **Explicit per-file opt-in.** Before the first run of a `UsesNetwork` assistant on a file, the App shows which
  assistant, which provider/endpoint (as reported by the plugin), and exactly which data (file names, which panes,
  how many lines) would be sent. Consent is remembered per (assistant, repository path or file) and can be revoked
  in the Plugins dialog. Running on another file asks again.
* **Local providers** (assistants that run entirely in-process or talk to a service on `localhost`) still declare
  their behaviour; the App labels them "local" when the plugin states so, but the per-file prompt remains the
  default and can be relaxed only by the user.
* Git `mergetool` invocations from scripts (non-interactive) never trigger network assistants.

### 4.2 Redaction hooks

A future, additive API (`IRequestRedactor`, registered through the host like other contributions) would let the user
or organisation install redactors that transform the outbound request, for example:

* strip lines matching secret patterns (keys, tokens, connection strings),
* replace file paths with neutral names,
* send only the target regions plus N context lines instead of whole files.

Redactors run in registration order; the consent dialog shows the redacted view, i.e. what is actually sent. A
redactor that fails blocks the request (fail closed).

### 4.3 Data handling rules for providers

* Requests contain only what is listed in the consent dialog.
* Responses are treated as untrusted data: they are parsed into `ResolutionProposal`s, never executed, never used to
  run commands or access files.
* Plugins must not persist request content beyond the audit log described below, unless the user enables it.

## 5. Budget and size limits

* **Request size**: hosts enforce a configurable maximum (default proposal: 2 000 lines or 200 KB of text in the
  outbound view). Larger selections are cut down to target regions plus context, or the user is asked to select
  fewer regions.
* **Time**: `MergeAssistantRunner` already enforces a timeout (default 60 s) and supports cancellation from the UI.
* **Rate/cost**: assistants that incur cost expose their own limits in settings; the host shows a running count of
  requests per session and can require confirmation above a threshold.
* **Output size**: proposals whose replacement is wildly larger than the region (e.g. > 10× and > 500 lines) are
  flagged in the preview.

## 6. Preview, accept, reject, undo

The App already has the scaffolding: `IEditorContext.OfferProposalsAsync` and an assistant panel that shows
"No merge assistants installed" until one is registered. The intended interaction:

1. **Preview**: each proposal appears attached to its region — the OUTPUT pane shows the proposed lines as a
   ghost/diff overlay against the current output, with rationale, confidence and source assistant.
2. **Per-proposal actions**: *Accept*, *Accept and edit* (opens the lines in the output editor), *Reject*. A
   "accept all shown" action exists but is never the default button.
3. **Apply**: accepted proposals are applied with `MergeSession.ApplyProposals`, which records **one undo step**;
   `Undo` restores the previous resolutions exactly. Regions resolved by a proposal are marked (gutter badge) until
   edited further.
4. **Stale proposals**: if the user changed a region after the request was built, its proposal is shown as stale
   and cannot be accepted without re-running (detected by comparing the snapshot's region output span/text with the
   current one).
5. **Saving**: proposals never bypass the normal save flow; `IMergeLifecycleHook.BeforeSaveAsync` hooks still run.

## 7. Determinism and audit log

* Assistants declare `Deterministic` when the same request always yields the same result (the sample does). For
  non-deterministic assistants the UI shows a "results may vary" hint and never re-runs implicitly.
* **Audit log** (per merge session, kept locally, optionally saved next to the merge result or in the user's data
  folder): for every run — timestamp, assistant id and version (from `PluginManifest`), capabilities, consent
  decision, hash and size of the outbound request, redactors applied, proposals received (region ids, confidence),
  and the user's decision per proposal (accepted / edited / rejected / undone). Raw request and response text are
  stored only if the user enables it.
* The audit log makes it possible to answer, after the fact, "which lines of this merge came from an assistant, and
  who approved them".

## 8. API evolution needed (all additive, minor version bump)

| Addition | Purpose |
| --- | --- |
| `IRequestRedactor` + registry | §4.2 redaction hooks |
| `ResolutionProposal.GroupId` (optional) | accept/reject related proposals together (rename-aware merges) |
| `MergeAssistantResult.Summary` (optional text) | merge message summaries, review summaries |
| `IAssistantConsentService` (host-provided) | lets an assistant query (not grant) consent state for UI hints |
| Streaming/progress callback for `RunAsync` | progress reporting for long runs |

None of these exist today; the current API (version 1.0) is sufficient for local, deterministic assistants like the
sample.
