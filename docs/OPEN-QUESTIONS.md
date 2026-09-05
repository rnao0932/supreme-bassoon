# Open questions

Specification section 22 lists eight questions the developer must resolve before production. None of
them can be answered from a keyboard without QuickBooks, so each is recorded here with what the code
currently assumes, what happens if the assumption is wrong, and how to answer it.

**Fill these in from the Phase 0 spike.** Until then, every "Current assumption" below is a guess
that the code makes safe rather than a fact.

---

### 1. Exact QuickBooks product, year/release, country edition, and 32/64-bit environment?

- **Status:** unanswered.
- **Current assumption:** none. The utility negotiates the qbXML version from what QuickBooks
  reports (`QbXmlRequestBuilder.NegotiateVersion`) rather than assuming one, and fails loudly if
  nothing between 8.0 and 16.0 is offered.
- **If wrong:** a version-specific element could be rejected. The negotiated version is recorded on
  every session and printed by the spike.
- **How to answer:** `qbreclass spike` — the "Environment" finding.

### 2. Are the target "bank transactions" ordinary Checks, Write Checks entries, downloaded bank-feed entries that became checks/expenses, or another type?

- **Status:** unanswered, and it materially changes scope.
- **Current assumption:** the writable population is credit card charges. Checks are queried and
  shown but never written.
- **If wrong:** if the client's population is mostly checks, version 1 delivers a preview and little
  else, and the check feasibility question below becomes the whole project.
- **How to answer:** ask the client, then confirm with `qbreclass preview` over a representative
  date range and count what comes back per type.

### 3. Does the desired reclassification concern expense-line `AccountRef`, class, customer/job, or more than one field?

- **Status:** assumed to be `AccountRef` only.
- **Current assumption:** the utility changes exactly one thing — the expense line's account. Class,
  customer and billable status are **protected fields**: a write that changes one halts the batch.
- **If wrong:** if the client also wants class reclassification, that is a feature, not a
  configuration change. It needs its own protected-field carve-out and its own tests.

### 4. How does QuickBooks expose the reconciliation/cleared state for these records, and does the supported modification preserve it in practice?

- **Status:** unanswered, and this is the one the code is most defensive about.
- **Current assumption:** none. `QbXmlResponseParser.ParseCleared` reads `ClearedStatus` when the
  return element carries it and records `ClearedStatus.Unknown` when it does not. It never infers
  "not reconciled" from an absent element.
- **What the user sees when it is absent:** the preview shows `?` in the Reconciled column, a banner
  says the reconciled count is unavailable, and the approval screen says so rather than showing a
  zero. `PlanResult.ClearedStatusUnavailable` drives this; tested by
  `WhenQuickBooksOmitsClearedStatus_ThePlanSaysItIsUnavailable`.
- **How to answer:** the spike's per-type cleared-status finding, **plus** a Previous Reconciliation
  report before and after a write probe. The second half is the one that matters and the spike
  cannot do it for you.

### 5. Can a reconciled check be safely reclassified in place through any supported SDK workflow? If not, should checks be excluded from automated writes?

- **Status:** unanswered, but now **testable in one command**.
- **How to settle it:** run the spike with `--allow-write` and read the "Checks" finding. The probe
  sends a `CheckModRq` naming a transaction that cannot exist, so nothing is modified either way;
  the answer is entirely in which rejection comes back. Status 500 means the edition does not
  implement the request. Status 3120 means it does, and got as far as looking for the record.
  Credit card charges are probed alongside as a control, so "supported" has a known shape on the
  same edition to compare against.
- **Why this matters more than it looks:** the reason checks are excluded is a claim about Intuit's
  published object matrix, taken from the specification and never verified against an installation.
  If that claim does not hold on the target edition, the scope of version 1 is materially larger
  than currently assumed - and if the client's population is mostly checks, it is the difference
  between a working tool and a preview.
- **What is still required even if `CheckModRq` exists:** a `CheckAdapter` with its own
  line-preservation handling, its own regression tests mirroring `ReclassificationTests`, and a
  reconciled multi-line check proven end to end with a Previous Reconciliation report compared
  before and after. The probe establishes that the door is unlocked, not that it is safe to walk
  through.
- **Implementation status:** the write path now exists and is off by default. `CheckAdapter` takes
  an `enableModification` flag, wired to `--enable-check-writes` on the command line and an "Allow
  check writes" checkbox in the application. Enabled, checks travel exactly the same path as credit
  card charges: same preflight, same all-lines-resubmitted request, same read-back verification
  against the same protected fields, same audit record. A check's `RefNumber` is its check number
  and its `AccountRef` is the bank account it draws on; both are header fields and neither is sent,
  so a reclassification cannot renumber a check or move it between bank accounts.
- **Why it stays off by default:** the operation is unverified, not thought to be wrong. Shipping it
  on would replace one unexamined assumption with another.
- **If a supported path exists:** see the check procedure in
  [PHASE0-SPIKE.md](PHASE0-SPIKE.md). It belongs inside `CheckAdapter`, behind its own tests.
- **What is explicitly not on the table:** delete-and-recreate. Specification sections 4, 5, 10 and
  11; enforced by `NoAdapterEmitsADeleteOrAddRequest`.

### 6. Are item-based lines present, or only expense-account lines?

- **Status:** unanswered.
- **Current assumption:** both may be present. Item lines are resubmitted verbatim so QuickBooks
  retains them; item **group** lines cause the record to be refused, because this version does not
  model them well enough to reconstruct one.
- **If wrong:** if the population is item-heavy, the refusal count in the preview will be high and
  visible. That is the intended failure mode — the alternative is silently deleting group lines.
- **How to answer:** the spike reports how many sampled records carry item lines.

### 7. Are multi-currency, sales tax, billable expenses, classes, or customer/job allocations in use?

- **Status:** unanswered.
- **Current assumption:**
  - **Multi-currency:** refused. Home-currency amounts are recalculated on save and this version does
    not verify that arithmetic.
  - **Sales tax:** tax-inclusive transactions are refused.
  - **Billable expenses:** a `HasBeenBilled` line is resubmitted *without* `BillableStatus`, because
    QuickBooks owns that state and rejects a request asserting it. The line carries a warning in the
    preview, since reclassifying a billed expense may affect invoicing.
  - **Classes and customer/job:** preserved verbatim and protected.
- **How to answer:** the spike's record-shape findings, plus the client's own account.

### 8. Single-user or multi-user mode during execution, and what permissions does the integrating application have?

- **Status:** unanswered.
- **Current assumption:** single-user. The utility works in multi-user mode but the spike raises a
  warning, because another user can edit a transaction between preflight and write.
- **Mitigation already in place:** the preflight/write window is one round trip, and QuickBooks'
  own `EditSequence` check rejects the write if anyone got in between — that is a skip, not a
  silent overwrite. The residual risk is throughput, not correctness.
- **How to answer:** the spike's "Environment" finding reports `QBFileMode`.

---

## Questions this project added

### 9. Does the qbXML element order in `QbXmlRequestBuilder` match what QuickBooks accepts?

- **Status:** unanswered against real QuickBooks.
- **Why it matters:** element order inside a qbXML request is schema-enforced. A correctly named
  element in the wrong position is rejected outright.
- **Current state:** orderings follow the published schema and are asserted by
  `TheModificationRequestEmitsElementsInSchemaOrder` and `ItemLinesAreEmittedAfterExpenseLines` —
  against this project's simulator, which is not authoritative.
- **How to answer:** the write probe. A rejection here produces a specific status code and message,
  recorded verbatim.

### 10. Is there a durable company-file identifier better than the fingerprint?

- **Status:** unanswered.
- **Current state:** `CompanyIdentity.Fingerprint` is a SHA-256 over the file path, legal name and
  EIN, because the SDK exposes no company-file GUID. A file restored to a different path produces a
  different fingerprint and the user is re-prompted — deliberately conservative.
- **How to answer:** check whether the target edition exposes anything more stable; if so, prefer it.

### 11. What is the correct QBXMLRPConnectionType numbering on the target edition?

- **Status:** partially answered, the hard way.
- **What happened:** passing `0` for a local connection to `OpenConnection2` against QuickBooks
  Desktop Pro 2021 was rejected with "The requested connection type could not be found". Published
  descriptions of `QBXMLRPConnectionType` disagree about whether the enum is 0-based or 1-based.
- **Current state:** the ordinary path no longer names the constant at all. `OpenConnection`, whose
  signature is `(appID, appName)`, always opens a local connection. Only the start-QuickBooks-on-
  demand path needs `OpenConnection2`, and there the candidates are tried in turn rather than
  assumed.
- **Still to confirm:** which value the launch-UI connection actually accepts on the target edition.
  The spike reports it once that path is exercised.
- **Lesson worth keeping:** this is exactly the class of assumption section 4 warns about. It cost
  one round trip against real QuickBooks and would have cost nothing to discover in Phase 0.

### 12. Does this QuickBooks implement BillModRq, and how does modifying a paid bill behave?

- **Status:** unanswered; the write path exists and is off by default.
- **How to settle the first half:** the spike's capability probe now covers `BillModRq` alongside
  checks and the credit-card control. Same read-only method: a modification naming a transaction
  that cannot exist.
- **What the probe cannot answer:** what happens when the bill has already been **paid**. QuickBooks
  permits reclassifying an expense line on a settled bill, and it is often exactly the correction
  wanted, but the effect on the bill payment linkage has not been observed here. The preview marks
  such rows with a PAID warning; before enabling bill writes against real data, reclassify one paid
  bill in a disposable company and check the vendor's transaction history and the A/P ageing either
  side of it.
- **Bills that are partly paid, or paid across several payments**, are the case most worth watching.
