# QuickBooks Desktop Bulk Reclassification Utility

A Windows companion application for accountants and bookkeepers who need to correct the expense
classification of large numbers of transactions already recorded in QuickBooks Desktop, without
recreating them.

It is built to the specification in
*QuickBooks Desktop Bulk Reclassification Utility — Software Requirements & Technical Specification,
Version 1.0*. The operating model is the one that specification asks for:

> filter → preview → select → approve small batch → modify in place → verify → log

with accounting integrity and recoverability taking priority over speed.

---

## Read this first

**The Phase 0 spike has not been run.** This repository was built without access to a QuickBooks
Desktop installation, so nothing here has been exercised against real QuickBooks. That is the
opposite order from the one the specification recommends (section 25: "Do not begin by building a
polished interface. First prove the exact read and write operations"), and the consequence is
specific and important:

- The qbXML **request shapes and element orderings** in `QbXmlRequestBuilder` are written to the
  published qbXML schema, and are asserted by tests — but asserted against this project's own
  simulator, not against QuickBooks. Element order in a qbXML Mod request is schema-enforced, and a
  correctly named element in the wrong position is rejected outright.
- Whether **`CreditCardChargeMod` behaves as designed on the target QuickBooks edition** — in
  particular whether it preserves reconciliation state — is unproven.
- **Whether checks can be reclassified at all** is unresolved, and is treated here as unsupported.

`qbreclass spike` exists to answer these on a real workstation before the write path is trusted. See
[docs/PHASE0-SPIKE.md](docs/PHASE0-SPIKE.md). Until it has been run against a disposable test
company, treat the write path as unproven — the code refuses to write to a company file it has been
told is disposable only by the operator, and it will not refuse on its own behalf.

The unresolved questions the specification itself raises in section 22 are carried forward, with
what is known and what is not, in [docs/OPEN-QUESTIONS.md](docs/OPEN-QUESTIONS.md).

---

## What is here

| Project | Target | Purpose |
| --- | --- | --- |
| `src/QbReclass.Core` | `net8.0` | Domain model, qbXML, adapters, services, audit store. No QuickBooks dependency. |
| `src/QbReclass.QuickBooks` | `net8.0-windows` | Live session over the `QBXMLRP2.RequestProcessor` COM component. |
| `src/QbReclass.Simulator` | `net8.0` | A qbXML-speaking test double for QuickBooks Desktop. |
| `src/QbReclass.Cli` | `net8.0` | `qbreclass` — the Phase 0 spike plus a headless driver of the full workflow. |
| `src/QbReclass.App` | `net8.0-windows` (WPF) | The desktop application. |
| `tests/QbReclass.Tests` | `net8.0` | 58 tests covering the specification's section 18 test plan and section 19 acceptance criteria. |

Only `IQbSession` knows what QuickBooks is. Everything above it deals in qbXML strings and
normalized models, which is what makes the safety behaviour testable without QuickBooks installed.

---

## Building and running

Full instructions, including how to get binaries onto a client workstation, are in
**[docs/RUNNING.md](docs/RUNNING.md)**. The short version:

```bash
dotnet test QbReclass.sln                                    # 59 tests
dotnet run --project src/QbReclass.Cli -f net8.0 -- demo     # the whole workflow, simulated
```

That needs only the .NET 8 SDK — no QuickBooks, no Windows. `demo` runs preview, batch approval,
execution, verification and audit against a simulated company and prints the result.

Everything cross-compiles, so the Windows binaries can be produced from any machine and are
self-contained (the workstation needs no .NET runtime):

```bash
dotnet publish src/QbReclass.Cli -c Release -f net8.0-windows -r win-x64 --self-contained true -o out/cli
dotnet publish src/QbReclass.App -c Release                   -r win-x64 --self-contained true -o out/app
```

Use `-r win-x86` instead if the workstation's QuickBooks SDK is 32-bit — the process bitness must
match the registered request processor. On the workstation, with QuickBooks open on a **disposable
copy** of the company file:

```
qbreclass.exe spike --report spike-read.md      # do this first
```

`qbreclass` with no arguments prints the full option list.

---

## How the safety model actually works

Each control in specification section 10 is implemented in one place, and tested.

| Control | Where it lives | Test |
| --- | --- | --- |
| Read-only by default | `QbSessionInfo.IsReadOnly`, checked in `QbComSession.SendRequest` and `ExecutionService` | `AReadOnlySession_RefusesToExecute` |
| Backup confirmation | `Job.BackupConfirmedAt`, enforced in `BatchPlanner.Approve` and `ExecutionService` | `WithoutBackupConfirmation_ExecutionIsRefused` |
| Company-file lock | `CompanyIdentity.Fingerprint`, re-read before every batch | `AgainstADifferentCompanyFile_ExecutionIsRefused` |
| Stale-data protection | `PreflightService` compares `EditSequence` **and** a protected-field hash | `TransactionEditedAfterPreview_IsSkippedAsStale...` |
| Line preservation | `CreditCardChargeAdapter` resubmits every line | `AModificationThatOmitsALine_DeletesIt`, `TheAdapterAlwaysResubmitsEveryLine` |
| Idempotency | `IAuditStore.MarkSubmitted` before the write; `ReconciliationService` after a restart | `InterruptedAfterWrite_ReconciliationMarksComplete_AndDoesNotReapply` |
| No delete/recreate | No adapter can emit one | `NoAdapterEmitsADeleteOrAddRequest` |
| Read-back verification | `VerificationService` + `ProtectedFields.Compare` | `VerificationMismatch_HaltsTheBatch...` |
| Stop control | `CancellationToken` honoured between transactions, never inside one | `CancellationBetweenTransactions_StopsCleanly` |
| Logging | Every attempt, including skips, writes an `ExecutionResult` | `EveryAttemptIsLoggedIncludingSkips` |

Two design decisions are worth calling out because they are not obvious:

**Header fields are deliberately omitted from the modification request.** In qbXML, an omitted
optional *header* element leaves the stored value unchanged, while an omitted existing *line* is
deleted. So the smallest safe request is: `TxnID`, `EditSequence`, and every line. Echoing the date,
payee and memo back would be a larger mutation, not a smaller one.

**Lines that share a transaction are written in one request.** Two requests against one transaction
would guarantee the second failed on a stale `EditSequence`, and re-reading between them would mean
writing to a state the user never approved.

---

## What version 1 will not do

Faithful to specification section 5 and FR-018:

- **Checks are preview-only.** Intuit's object matrix lists Check as queryable with no `CheckMod`
  operation. They appear in the preview, clearly marked, and cannot be selected. The utility will
  not delete and recreate a check to simulate a modification.
- **Records it cannot faithfully reconstruct are refused**, not approximated: item group lines,
  foreign-currency transactions, tax-inclusive transactions, and any line QuickBooks did not give a
  `TxnLineID`.
- **Nothing but the expense-line account is changed.** Dates, amounts, payees, posting accounts,
  reference numbers, memos, classes, customers and reconciliation state are protected fields, and a
  write that disturbs one halts the batch.

---

## Documentation

- [docs/RUNNING.md](docs/RUNNING.md) — how to build, deploy and drive it
- [docs/PHASE0-SPIKE.md](docs/PHASE0-SPIKE.md) — what to run first, and what to record
- [docs/OPEN-QUESTIONS.md](docs/OPEN-QUESTIONS.md) — specification section 22, carried forward
- [docs/SPEC-TRACEABILITY.md](docs/SPEC-TRACEABILITY.md) — every FR and acceptance criterion mapped to code and tests
- [docs/SAFETY-MODEL.md](docs/SAFETY-MODEL.md) — the write path, step by step

## Intuit references

- [Desktop SDK, Get started](https://developer.intuit.com/app/developer/qbdesktop/docs/get-started)
- [QuickBooks objects and operations accessible with the SDK](https://developer.intuit.com/app/developer/qbdesktop/docs/additional-reference/quickbooks-objects-and-operations-accessible-with-the-sdk)
- [CreditCardChargeMod API reference](https://developer.intuit.com/app/developer/qbdesktop/docs/api-reference/qbdesktop/creditcardchargemod)
- [Modify, delete and void requests and responses](https://developer.intuit.com/app/developer/qbdesktop/docs/develop/exploring-the-quickbooks-desktop-sdk/modify-delete-and-void-requests-and-responses)
