# Specification traceability

Every functional requirement, safety control and acceptance criterion from the specification, mapped
to where it is implemented and what proves it.

"Test" names are in `tests/QbReclass.Tests`. "Spike" means the requirement cannot be settled without
QuickBooks and is deferred to `qbreclass spike`.

## Section 7 — Functional requirements

| ID | Requirement | Implementation | Proof |
| --- | --- | --- | --- |
| FR-001 | Company identification | `CompanyIdentity`, shown in the window header and on the approval screen | `AgainstADifferentCompanyFile_ExecutionIsRefused` |
| FR-002 | Read-only default | `QbConnectionOptions.ReadOnly = true`; `MainViewModel.WriteModeEnabled` starts false | `AReadOnlySession_RefusesToExecute`, `ThePreviewStageWritesNothing` |
| FR-003 | Date filter | `Job.FromDate`/`ToDate`, pushed into `TxnDateRangeFilter` | `TheQueryPushesTheDateRangeAndLineItemsIntoTheRequest` |
| FR-004 | Source account | `Job.SourceAccount`, matched per line by ListID | `SingleLineCharge_IsReclassifiedAndVerified` |
| FR-005 | Destination account | `Job.Validate()` rejects same-account, inactive and non-expense-side | `ABalanceSheetDestination_FailsJobValidation`, `SameSourceAndDestination_FailsJobValidation` |
| FR-006 | Transaction type filter | `Job.TransactionTypes` + `AdapterRegistry` | `Checks_ArePreviewedAsUnsupported_AndNeverWritten` |
| FR-007 | Optional filters | `JobFilters`; SDK-side where available, local otherwise | `ReclassificationTests` filter usage throughout |
| FR-008 | One row per line | `ReclassificationPlanner.Plan` emits one candidate per matching line | `MultipleTargetLinesOnOneTransaction_AreAppliedInASingleRequest` |
| FR-009 | Selection | `IAuditStore.SetSelection`; select all/none/individual in the grid | `Checks_ArePreviewedAsUnsupported_AndNeverWritten` (refuses to select) |
| FR-010 | Batch sizing | `Job.BatchSize`, default 25; `BatchPlanner.SplitIntoBatches` | `BatchesRespectTheSizeLimitAndKeepTransactionsWhole` |
| FR-011 | Preflight refresh | `PreflightService.Check` re-queries immediately before the write | `TransactionEditedAfterPreview_IsSkippedAsStale_AndLeftUnchanged` |
| FR-012 | Conflict detection | `EditSequence` comparison **plus** protected-field hash | same, and `PreflightFailure.SnapshotDrift` |
| FR-013 | Minimal mutation | Header fields omitted; every line resubmitted | `TheModificationRequestCarriesNoHeaderFields`, `TheAdapterAlwaysResubmitsEveryLine` |
| FR-014 | Post-write verification | `VerificationService` re-queries and compares | `MultiLineCharge_ChangesOnlyTargetLine_AndRetainsTheOthers` |
| FR-015 | Failure isolation | One transaction per request; completed work never repeated | `SessionLostMidBatch_HaltsAndPreservesCompletedWork` |
| FR-016 | Progress persistence | `SqliteAuditStore`; `MarkSubmitted` before the write; `ReconciliationService` | `InterruptedAfterWrite_...`, `InterruptedBeforeWrite_...`, `TheAuditStoreSurvivesAReopen` |
| FR-017 | Audit export | `ExportService.ExportCsv` / `ExportJson` | `TheCsvExportCarriesBeforeAndAfterClassificationWithIdentifiers`, `TheJsonExportRoundTripsTheWholeJob` |
| FR-018 | No silent fallback | `ModificationSupport`; unsupported rows cannot be selected | `Checks_ArePreviewedAsUnsupported_AndNeverWritten`, `UnsupportedRecordShapes_ArePreviewedAndRefused` |

## Section 8 — Preview screen

| Requirement | Where |
| --- | --- |
| Columns: Select, Type, Date, Payee, Ref/Check #, Amount, Current, Proposed, Memo, Class, Reconciled, Status | `MainWindow.xaml` grid columns, bound to `CandidateRow` |
| Count and dollar total for all matches and for selected rows | `MainViewModel.TotalsText` |
| Filters remain visible above the grid | `MainWindow.xaml` rule-builder panel |
| Row click opens a detail pane with TxnID, line identifier, accounts, warnings | `CandidateRow.Detail` |
| Makes clear no change has been made | Header banner, section heading, read-only badge |
| Future: open the transaction in QuickBooks | `QueryService.DisplayInQuickBooks`, "Open in QuickBooks" button |

## Section 9 — Batch approval screen

Every listed item is a property of `BatchApprovalSummary` and rendered by `BatchApprovalWindow` from
that model, so a field cannot be dropped from the screen without being dropped from the model.
Covered by `TheApprovalSummaryStatesScopeAndNamesTheAction`.

The action button is `BatchApprovalSummary.ButtonText` — "Apply 25 Approved Reclassifications" —
never "OK" or "Continue".

## Section 10 — Safety and accounting integrity

| Control | Implementation | Test |
| --- | --- | --- |
| Backup confirmation | `Job.BackupConfirmedAt`; enforced in `BatchPlanner.Approve` and `ExecutionService.GuardPreconditions` | `WithoutBackupConfirmation_ExecutionIsRefused` |
| Company-file lock | `CompanyIdentity.Fingerprint`, re-read before every batch | `AgainstADifferentCompanyFile_ExecutionIsRefused` |
| Stale-data protection | `PreflightService`; a stale record is skipped, never forced | `TransactionEditedAfterPreview_...` |
| Reconciled transactions | Warned in preview; cleared status is a protected field | `ReconciledCharge_IsFlagged_AndItsClearedStatusSurvives` |
| Line preservation | Every line resubmitted | `AModificationThatOmitsALine_DeletesIt` (proves the hazard is real), `TheAdapterAlwaysResubmitsEveryLine` |
| Idempotency | `CandidateStatus` state machine + `ReconciliationService` | `InterruptedAfterWrite_ReconciliationMarksComplete_AndDoesNotReapply` |
| No automatic delete/recreate | No adapter can emit one | `NoAdapterEmitsADeleteOrAddRequest` |
| Verification | `ProtectedFields.Compare` | `VerificationMismatch_HaltsTheBatchEvenWhenContinuingOnFailures` |
| Stop control | `CancellationToken` between transactions only | `CancellationBetweenTransactions_StopsCleanly` |
| Logging | One `ExecutionResult` per attempt, SDK status verbatim | `EveryAttemptIsLoggedIncludingSkips` |

## Section 11 — Transaction-type strategy

| Type | Status | Where |
| --- | --- | --- |
| Credit card charge | Writable via `CreditCardChargeMod`, subject to the spike | `CreditCardChargeAdapter` |
| Check | Preview-only; no `CheckMod` in Intuit's object matrix | `CheckAdapter` |
| Credit card credit, bill | Not registered — cannot appear in a job at all | `AdapterRegistry.Default` |

## Section 12 — Architecture

| Component named in the specification | Type |
| --- | --- |
| QuickBooksSessionService | `IQbSession`, `QbComSession`, `SimulatedQbSession` |
| QueryService | `QueryService` |
| ReclassificationPlanner | `ReclassificationPlanner` |
| TransactionAdapter(s) | `ITransactionAdapter`, `CreditCardChargeAdapter`, `CheckAdapter` |
| PreflightService | `PreflightService` |
| ExecutionService | `ExecutionService` |
| VerificationService | `VerificationService` |
| AuditStore | `IAuditStore`, `SqliteAuditStore` |
| ExportService | `ExportService` |
| UI | `QbReclass.App` (WPF), `QbReclass.Cli` |

## Section 13 — Data model

| Entity | Type |
| --- | --- |
| Job | `Job` |
| CandidateLine | `CandidateLine` |
| Batch | `Batch` |
| ExecutionResult | `ExecutionResult` |
| AuditSnapshot | `AuditSnapshot` |

## Section 14 — Modification algorithm

All twelve steps are `ExecutionService.ExecuteTransaction`, in order. Step 12 — stop the batch and
show a high-severity warning on verification failure — is `ExecutionResult.ForcesHalt`, which
overrides the stop policy.

## Section 15 — Error handling

| Condition | Handling |
| --- | --- |
| Authorization/connection error | `QbComSession.TranslateComFailure` produces recovery instructions |
| Wrong company open | `QbCompanyMismatchException` blocks execution |
| EditSequence conflict | `PreflightFailure.Stale`; skipped, returned to review |
| Account invalid/inactive | `PreflightFailure.DestinationAccountInvalid`; skipped |
| Unsupported transaction type | `ModificationSupport.NotSupported`; never improvised |
| SDK validation error | Status code and message logged verbatim; continues only if the stop policy permits |
| QuickBooks closes / session drops | Batch halts, state persisted, reconciled on next launch |
| Verification mismatch | Batch halts immediately, transaction identified prominently |

## Section 18 — Testing plan

| Scenario | Test | Status |
| --- | --- | --- |
| Single-line charge | `SingleLineCharge_IsReclassifiedAndVerified` | Simulated |
| Multi-line, one line changes | `MultiLineCharge_ChangesOnlyTargetLine_AndRetainsTheOthers` | Simulated |
| Multiple lines on one transaction | `MultipleTargetLinesOnOneTransaction_AreAppliedInASingleRequest` | Simulated |
| Memo, class, payee, reference data | `ChargeWithMemoClassPayeeAndRef_KeepsAllOfThem` | Simulated |
| Historical cleared/reconciled | `ReconciledCharge_IsFlagged_AndItsClearedStatusSurvives` | Simulated; **reconciliation report comparison requires the spike** |
| Edited after preview | `TransactionEditedAfterPreview_IsSkippedAsStale_AndLeftUnchanged` | Simulated |
| Destination made inactive | `DestinationAccountMadeInactiveAfterPreview_SkipsEverything` | Simulated |
| QuickBooks closed mid-batch | `SessionLostMidBatch_HaltsAndPreservesCompletedWork` | Simulated |
| Terminated after write, before flag | `InterruptedAfterWrite_ReconciliationMarksComplete_AndDoesNotReapply` | Simulated |
| 10,000 candidate lines | `APreviewOfTenThousandCandidateLinesIsPagedAndAccurate` | Simulated |
| Check feasibility | `Checks_ArePreviewedAsUnsupported_AndNeverWritten` | **Documented as unsupported; spike must confirm** |
| Backup/restore drill | — | **Operational; not automatable here** |

## Section 19 — Acceptance criteria for version 1

| Criterion | Status |
| --- | --- |
| Connects through the supported SDK | Implemented (`QbComSession`); **unverified against QuickBooks** |
| Accurate read-only preview from a date/account rule | Implemented and tested |
| No transaction changes during preview | Tested — `ThePreviewStageWritesNothing` |
| User can approve a limited batch | Implemented and tested |
| At least one type modified in place via a documented Mod request | Implemented; **proof requires the spike** |
| Every modification preflighted and verified | Tested |
| Stale EditSequence skipped | Tested |
| Complete audit record exportable | Tested |
| Restart does not duplicate completed work | Tested |
| Unsupported check behaviour disclosed, not hidden | Tested |
| Regression tests show no unintended changes to protected attributes | Tested against the simulator |

Two criteria depend on QuickBooks itself and are honestly open until Phase 0 runs. Everything else
is implemented and covered.
