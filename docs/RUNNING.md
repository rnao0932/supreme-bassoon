# How to run it

Three situations, in the order you are likely to hit them.

---

## 1. See it work, on any machine, with no QuickBooks

You need the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). Nothing else.

```bash
git clone https://github.com/rnao0932/supreme-bassoon.git
cd supreme-bassoon
git checkout claude/build-discussion-oy1jpg

dotnet test QbReclass.sln                          # 59 tests
dotnet run --project src/QbReclass.Cli -f net8.0 -- demo
```

`demo` builds a small simulated company and runs the entire workflow against it — preview, batch
approval, execution, read-back verification, audit summary — and prints the resulting state. The
check in that company is deliberately left alone, and the multi-line charge keeps its unrelated
line, so you can see both behaviours in the output.

To drive the workflow yourself against the simulator, with the real command surface:

```bash
BIN="dotnet run --project src/QbReclass.Cli -f net8.0 --"

$BIN accounts --simulate
$BIN preview  --simulate --from 2020-01-01 --to 2030-12-31 --source ACC-0003 --dest ACC-0004
$BIN run      --simulate --from 2020-01-01 --to 2030-12-31 --source ACC-0003 --dest ACC-0004 \
              --backup-confirmed --batch-size 2
```

`run` stops at each batch and waits for you to type `APPLY`. Add `--yes` to skip the prompt — only
ever appropriate against the simulator or a test company.

The `-f net8.0` picks the cross-platform build. On Windows you can drop it and use `-f
net8.0-windows` to get the build that can also talk to real QuickBooks.

---

## 2. Run the Phase 0 spike on the client's Windows workstation

**Do this before anything else touches a real company file.** Full detail in
[PHASE0-SPIKE.md](PHASE0-SPIKE.md); this is just the mechanics.

### Build the binaries

You can build these anywhere — including from Linux or macOS — because the Windows projects
cross-compile. They are self-contained, so the workstation does not need the .NET runtime installed.

**Match the bitness to the installed QuickBooks.** A 64-bit process cannot load a 32-bit
`QBXMLRP2.RequestProcessor`, and the failure is an unhelpful `REGDB_E_CLASSNOTREG`.

Intuit shipped no 64-bit QuickBooks Desktop until the 2022 release, so:

| Installed QuickBooks | Publish with |
| --- | --- |
| 2021 and earlier (32-bit) | `-r win-x86` |
| 2022 and later (64-bit) | `-r win-x64` |

If unsure, check Help > About in QuickBooks, or just try `win-x86` first — an older
installation is the more common case.

```bash
# 64-bit (most current installations)
dotnet publish src/QbReclass.Cli -c Release -f net8.0-windows -r win-x64 --self-contained true -o out/cli
dotnet publish src/QbReclass.App -c Release                   -r win-x64 --self-contained true -o out/app

# 32-bit, if that is what the SDK is
dotnet publish src/QbReclass.Cli -c Release -f net8.0-windows -r win-x86 --self-contained true -o out/cli
dotnet publish src/QbReclass.App -c Release                   -r win-x86 --self-contained true -o out/app
```

Copy `out/cli` and `out/app` to the workstation. `out/cli/qbreclass.exe` and `out/app/QbReclass.exe`
are the two programs.

`-f net8.0-windows` on the CLI is what includes the live QuickBooks session. Without it you get the
cross-platform build, which only knows `--simulate`.

### On the workstation

1. Install the [QuickBooks Desktop SDK](https://developer.intuit.com/app/developer/qbdesktop/docs/get-started/download-and-install-the-sdk).
2. **Restore a copy of the company file under a different name.** The spike writes.
3. Open QuickBooks with that copy, single-user mode, no dialog open.

```
qbreclass.exe spike --report spike-read.md
```

QuickBooks will raise its authorization prompt the first time. Grant access; the grant lives in the
company file's integrated-application list where it can be revoked.

Read the report. Then exercise the write path — pick a credit card charge that is **multi-line** and
**reconciled**, because those are the two cases that matter:

```
qbreclass.exe accounts
qbreclass.exe spike --allow-write --confirm-test-company ^
                    --write-txn <TxnID> --write-dest <ListID> ^
                    --report spike-write.md
```

It reclassifies one line, verifies field by field, and puts it back. Exit code `2` means a blocker
was found — that is a successful spike, not a failure.

Then run **Reports → Banking → Previous Reconciliation** before and after and compare. The spike
cannot do that for you, and it is the check the client actually cares about.

---

## 3. Use the desktop application

Run `QbReclass.exe` from `out/app`.

It opens **read-only**, with a yellow banner saying so. Nothing can change a company file until you
have, in order:

1. Clicked **Connect** (or ticked *Use simulated company* to try the interface with no QuickBooks).
2. Ticked **A current QuickBooks backup exists** — the utility records when you confirmed it. It
   does not make the backup for you.
3. Ticked **Enable write mode**, which is refused until step 2 is done. The banner turns red.

Then the normal loop:

- Set dates, **From account**, **To account**, transaction types, and any filters. Click **Preview**.
- The grid fills in. Nothing has been written. Greyed rows cannot be changed — hover for the reason,
  or click for the full detail pane. Checks always appear this way.
- Tick the rows you want. The totals bar shows matched, selected, and unchangeable counts.
- Set a batch size and click **Review and approve a batch…**. The approval dialog states the company
  file, the rule, the counts, the dollar total, reconciled and warning counts, your backup timestamp,
  and exactly what will change. The button says *Apply N Approved Reclassifications*.
- One batch runs per click. The next never starts on its own.
- **Export audit** writes CSV and JSON to `Documents\QbReclass`.

If a batch is interrupted — QuickBooks closes, the machine restarts — reconnect. The application
finds anything that was in flight, asks QuickBooks what actually happened, and tells you before
letting the job continue.

---

## Where things are kept

| What | Where |
| --- | --- |
| Job and audit database | `%LOCALAPPDATA%\QbReclass\qbreclass.db` |
| Audit exports from the app | `Documents\QbReclass\` |
| Audit exports from the CLI | wherever `--csv` / `--json` point |

Override the database with `--db <path>` on any CLI command. Exports contain financial data and
QuickBooks identifiers — handle them accordingly.

---

## Troubleshooting

| Message | What it means |
| --- | --- |
| "The QuickBooks Desktop SDK is not available to this application" / `0x80040154` / "Class not registered" | The SDK is not installed — installing QuickBooks alone is not enough, the SDK is a separate download — or the process bitness does not match the registered request processor. Rebuild with the other `-r win-x86` / `-r win-x64`. To work through the application without QuickBooks, use the simulated company instead. |
| "QuickBooks does not have a company file open" | Open the company file first. |
| "This application has not been authorized" | Grant access in QuickBooks under Edit → Preferences → Integrated Applications. |
| "QuickBooks is busy or a modal dialog is open" | Close the dialog in QuickBooks. |
| "Job is bound to X but QuickBooks has Y open" | Working. The company file changed; the job refuses to run against a different one. |
| "Write mode requires --backup-confirmed" | Working. Take a backup, then pass the flag. |
| "A live QuickBooks session is only available in a Windows build" | You are running the `net8.0` CLI build. Republish with `-f net8.0-windows`, or use `--simulate`. |
| "Cannot find non-neutral culture related to 'en-us'" | Fixed. If you are on a copy from before that fix, delete the `<InvariantGlobalization>true</InvariantGlobalization>` line from `Directory.Build.props`. Setting it leaves .NET with only the invariant culture, and WPF's DatePicker and DataGrid resolve a specific culture during layout. |
| `MSBUILD : error MSB1009: Project file does not exist` | The terminal is not in the project folder. `cd` there first — a `cd` only applies to the window you typed it in. |
