# Phase 0 — the technical spike

Specification sections 4, 20 and 25 all say the same thing: prove the SDK operations before building
on them. This document is how to do that with what is in this repository, and what to write down.

The spike is not throwaway. It ships as `qbreclass spike` so it can be re-run on every new
workstation, after every QuickBooks upgrade, and against every new company file.

## Before you start

1. **Make a disposable test company.** Restore a copy of the client file under a different name, or
   build a representative file from scratch. Specification section 18: production testing is not an
   acceptable substitute.
2. Install the QuickBooks Desktop SDK on the workstation.
3. Match bitness. A 64-bit build of this utility cannot load a 32-bit `QBXMLRP2.RequestProcessor`.
   The error message says so if you get it wrong.
4. Open QuickBooks with the test company, in single-user mode, with no modal dialog open.

## Run the read-only spike

```
qbreclass spike --report spike-read.md
```

This connects, authorizes (QuickBooks will prompt the first time), and reports:

- product name, version, country, file mode, 32/64-bit
- every qbXML version QuickBooks supports, and which one the utility negotiated
- the chart of accounts, and how many accounts are expense-side
- for each transaction type: whether the query works and how many records came back
- **whether QuickBooks reports a cleared/reconciled status for these records** — specification
  section 22, question 4, answered empirically
- which sampled records this utility would refuse to modify, and why

Findings are graded `Info`, `Warning` or `Blocker`. The command exits `2` if any blocker was found.

## Run the write spike

Only against the disposable test company.

Pick a credit card charge and an expense account to move a line to. Prefer a charge that is
**multi-line** and **reconciled** — those are the two cases that matter and the two the read-only
spike cannot answer.

```
qbreclass spike --allow-write --confirm-test-company \
                --write-txn <TxnID> --write-dest <ListID> \
                --report spike-write.md
```

It reclassifies one expense line to the destination, verifies the result field by field, then puts
it back. Both directions are checked. `--confirm-test-company` is mandatory; without it the write
probe refuses and records a blocker.

## What to record

Write these into `docs/OPEN-QUESTIONS.md` as they are answered. Attach the spike reports.

### The feasibility questions

| Question | How the spike answers it |
| --- | --- |
| Does `CreditCardChargeMod` work on this edition? | Write probe, "Write path" findings |
| Does it preserve all lines on a multi-line charge? | Write probe against a multi-line record; `ProtectedFields` compares line by line |
| Does it preserve reconciliation state? | Write probe against a reconciled record — **and** a before/after reconciliation report in QuickBooks, which the spike cannot do for you |
| Is `ClearedStatus` reported at all? | Read spike, per-type cleared-status finding |
| Is there any supported in-place path for checks? | Not answered by the spike. See below. |

### The reconciliation check the spike cannot do

Run **Reports → Banking → Previous Reconciliation** for the affected account before and after the
write probe, and compare. The SDK reporting what it did is not the same as the reconciliation report
being unchanged, and only the second one is what the client cares about. If the reconciliation report
moves, stop: the write path is not safe for reconciled records, and that is a Phase 4 conversation,
not a bug fix.

### Checks

Intuit's object matrix lists Check as queryable with no `CheckMod` operation, so this repository
treats checks as preview-only (`CheckAdapter`). If you believe a supported in-place path exists on
the target edition:

1. Establish it against Intuit's documentation, not by experiment alone.
2. Prove it in a disposable company against a reconciled multi-line check.
3. Implement it inside `CheckAdapter`, with its own regression tests mirroring
   `ReclassificationTests`.
4. Only then change `CheckAdapter.TypeSupport`.

Do not solve this by deleting and recreating checks. Specification sections 4, 5 and 11 all prohibit
it, and `NoAdapterEmitsADeleteOrAddRequest` fails if anyone tries.

## If the spike finds a blocker

Stop and report it. That is a successful spike — it is exactly the outcome section 4 is written to
produce. Record the exact status code and message before designing around anything.

## What "proven" means before Phase 2

- [ ] Read spike clean on the target workstation and edition
- [ ] Write probe round-trips a **single-line** charge
- [ ] Write probe round-trips a **multi-line** charge with every other line intact
- [ ] Write probe round-trips a **reconciled** charge
- [ ] Previous Reconciliation report unchanged across the write probe
- [ ] `ClearedStatus` availability recorded, and the preview's behaviour when it is absent confirmed
- [ ] Element order accepted by QuickBooks for every request in `QbXmlRequestBuilder`
- [ ] Check feasibility documented either way
