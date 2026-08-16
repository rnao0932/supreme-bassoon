# The safety model

What actually happens between "the user clicks approve" and "the audit row is written", and why each
step is there.

## The write path, step by step

For each transaction in the approved batch — one transaction at a time, never in parallel:

```
1  Guard the batch          job has a backup confirmation
                            batch is Approved with a timestamp
                            session is not read-only
                            company file re-read and matched to the job     <-- refuse, do not skip

2  Preflight every line     destination account still exists, active, expense-side
   on this transaction      transaction still exists
                            target line still exists
                            line is not already at the destination          <-- complete, not work
                            EditSequence unchanged since preview            <-- skip if stale
                            protected-field hash unchanged since preview    <-- skip if drifted
                            line still points at the source account
                            line amount unchanged
                            record shape still supported

3  All-or-none              if any line on this transaction failed preflight,
                            none of them is written

4  Record the intent        candidate marked Submitted, committed durably
                            before-snapshot stored                          <-- survives power loss

5  Build the request        TxnID, EditSequence, every line resubmitted
                            target lines carry the new AccountRef
                            no header fields                                <-- minimal mutation

6  Send                     one CreditCardChargeModRq

7  Read back                fresh query by TxnID                            <-- not the response echo

8  Compare                  target lines now at the destination
                            date, amount, payee, posting account, ref, memo,
                            cleared status, currency, line count unchanged
                            every other line's account unchanged
                            every line's amount, memo, class, customer,
                            billable status, item, quantity unchanged

9  Record the outcome       ExecutionResult + AuditSnapshot
                            candidate marked Verified or Failed

10 Decide                   verification mismatch  -> halt, always
                            failure + StopOnFirstFailure -> halt
                            failure + ContinueOnIsolatedFailure -> continue
                            cancellation requested -> halt before the next transaction
```

## Why each unusual decision was made

**One transaction per request, not one batch per request.** qbXML would let many modifications share
an envelope. Batching them would make failure isolation, per-record verification and per-record
audit impossible, and specification section 16 is explicit that no performance target justifies
skipping preflight or verification.

**All lines on a transaction in one request.** Two requests against one transaction would guarantee
the second failed on a stale `EditSequence`. Re-reading between them would mean writing to a state
the user never approved.

**Header fields omitted.** In qbXML an omitted optional *header* element leaves the stored value
unchanged, while an omitted existing *line* is deleted. So the smallest safe request is `TxnID` +
`EditSequence` + every line. Resubmitting the date, payee and memo would be a larger mutation.

**`MarkSubmitted` commits before the request is sent.** This is the only ordering that survives a
crash. Recording afterwards would leave a completed change looking untried, and the next run would
apply it twice. The store runs `synchronous=FULL` for this reason — slower, and the point.

**Already-at-destination is checked before staleness.** A record this utility successfully modified
in an earlier run is legitimately stale relative to the preview. Checking staleness first would
strand completed work in a permanent error state.

**The snapshot hash is checked as well as `EditSequence`.** `EditSequence` is QuickBooks' own
guarantee and should be sufficient. The hash catches a change QuickBooks did not version. If that
never fires in practice, it cost one comparison.

**Verification re-queries rather than trusting the modification response.** A status code of 0 says
QuickBooks accepted the request, not that the stored record is what was intended. Specification
section 14, step 8 asks for the read; this does the read.

**A verification mismatch overrides the stop policy.** Every other failure mode leaves QuickBooks in
a state the utility understands. A mismatch means it does not, and continuing would compound it.

## What is deliberately refused

`RecordShapeRules.FindUnsupportedShape` refuses, rather than approximating:

| Shape | Why |
| --- | --- |
| Item group lines | Resubmitting a group line to retain it is not modelled; omitting it deletes the group |
| Foreign currency | Home-currency amounts are recalculated on save and this version does not verify that arithmetic |
| Tax-inclusive lines | Modification semantics differ and are unverified |
| Any line without a `TxnLineID` | A line that cannot be identified cannot be retained, and cannot be tracked across the write |
| Duplicate `TxnLineID`s | Lines could not be matched reliably before and after |
| No expense lines | Nothing to reclassify |

Refusal is visible: the row appears in the preview, greyed, with the reason in the detail pane and
in its tooltip, and it cannot be selected. `IAuditStore.SetSelection` re-checks this, so a bug in
the grid cannot smuggle one into a batch.

## What the safety model does not cover

- **Whether QuickBooks itself preserves reconciliation** across `CreditCardChargeMod`. The utility
  verifies that the *reported* cleared status is unchanged. Whether the Previous Reconciliation
  report agrees is an empirical question for the spike.
- **Another user editing in multi-user mode.** `EditSequence` turns this into a skip rather than an
  overwrite, which is a correctness guarantee, not a throughput one.
- **The backup itself.** The utility records that the user confirmed one exists. It does not create
  or verify it, per specification section 6 step 2.
