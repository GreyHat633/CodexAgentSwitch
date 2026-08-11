# Codex Agent Switch 0.2.3 acceptance report

Date: 2026-08-11
Canonical workspace: `E:\AISPace\Codex Agent Switch Project`
Branch: `feat/0.2.3-proactive-delegation`
Worktree policy: direct canonical workspace only; no worktree was created.

## Result

PASS for the 0.2.3 development-plan deliverables. Main now receives proactive
delegation instructions, current-package policy, closed reason codes, continuous
Repartition triggers, persisted telemetry, and serial-Worker guidance. The final
natural task exercised the behavior with a real Native Worker and did not rely only
on unit tests or static prompt inspection.

## Implementation map

| Requirement | Implementation |
| --- | --- |
| Initial Delegation Check | Managed native and external instructions require the check after minimum localization. |
| Positive policy | `EconomicPolicyV2.EvaluateRepartition` evaluates clear, bounded, stable, capable, verifiable, non-overlapping, and worthwhile signals; 4/7 is a majority. |
| Current-package risk | Risk is supplied per `RepartitionWorkPackage`; HIGH stays Main, while later LOW/MEDIUM packages are evaluated independently. |
| MAIN / WORKER reasons | Closed `RepartitionReasonCode` set; decisions, current state, and telemetry reject owner/reason mismatch. |
| Lightweight state | `CurrentWorkState` carries current work, remaining work, owner, last trigger, reason, and Worker state without introducing a planner. |
| Persistence | Append-only `scheduler_repartitions` table keyed by task group and sequence, with UTC timestamp and ordered reads. |
| Runtime boundary | `record_repartition` and `list_repartitions` use strict schemas. Main reports semantics; the host only validates and stores. |
| Serial Worker | Scheduler active count remains concurrency state. Automated and real-development evidence shows a later Worker can be dispatched after the earlier one is terminal. |

## Eight Repartition triggers

The enum contains exactly these eight values. None are merged or treated as aliases.

| Trigger | Event class | Runtime entry |
| --- | --- | --- |
| `INITIAL_LOCALIZATION_COMPLETE` | Main semantic event | Managed instruction after minimum localization, then `record_repartition`. |
| `ARCHITECTURE_RESOLVED` | Main semantic event | Main records when interfaces/boundaries become stable and re-evaluates the now-bounded package. |
| `WORKER_RESULT_RECEIVED` | Worker lifecycle event plus Main report | Scheduler result reaches `ResultReceived`; Main records before bounded review. |
| `WORKER_REVIEW_COMPLETE` | Review lifecycle event plus Main report | Main completes risk-appropriate review/adoption, then records and re-evaluates all remaining work. |
| `PHASE_CHANGE` | Main semantic event | Main records before entering the next substantive phase. |
| `BUILD_TEST_BOUNDED_FIXES` | Main semantic event | A concrete build/test fix becomes bounded and is re-evaluated independently. |
| `MODULE_COMPLETE` | Main semantic event | A module completion changes remaining-work boundaries and triggers a new decision. |
| `WORK_CONVERGED` | Main semantic event | Remaining work has converged to final integration or another bounded package. |

The Scheduler intentionally does not infer architecture, phase, module, or convergence
semantics. It validates the closed enum and persists Main's report. Worker result and
review state have concrete Scheduler transitions, but their Repartition meaning is
still explicitly recorded by Main so the history remains auditable.

## Positive delegation and no-delegation policy

Worker-positive conditions are:

1. clear requirement;
2. bounded scope;
3. stable interfaces;
4. configured Worker has the capability;
5. independently verifiable result;
6. no ownership overlap;
7. delegation value exceeds overhead.

For LOW/MEDIUM current packages, four or more positive conditions prefer Worker unless
the package is trivial. HIGH risk, unresolved architecture/investigation, capability
gaps, overlap, required review, and final integration remain Main work.

MAIN reasons: `ARCHITECTURE_UNRESOLVED`, `CROSS_MODULE_DECISION`,
`INVESTIGATION_UNRESOLVED`, `WORKER_CAPABILITY_MISSING`,
`TOO_SMALL_TO_DELEGATE`, `REVIEW_REQUIRED`, `FINAL_INTEGRATION`.

WORKER reasons: `BOUNDED_IMPLEMENTATION`, `BOUNDED_FIX`, `BOUNDED_UI`,
`BOUNDED_TESTING`, `REPETITIVE_WORK`.

The ToolHost schema accepts only declared trigger/owner/reason names. The Scheduler and
domain models additionally reject a MAIN decision with a WORKER reason and vice versa.

## Automated verification

| Gate | Result |
| --- | --- |
| Orchestration policy focused tests | PASS, 29/29 |
| Scheduler/IPC/SQLite focused tests | PASS, 15/15 |
| Main test project | PASS, 182/182 |
| Bootstrapper tests | PASS, 19/19 |
| ToolHost build | PASS, 0 warnings/errors |
| x64 Debug App build | PASS, 0 warnings/errors |
| `git diff --check` | PASS |

Coverage includes exact eight-trigger closure, 4/7 majority behavior, trivial and HIGH
risk fallback, risk changes between packages, all reason codes, persisted current state,
owner/reason rejection, undefined numeric enum rejection, real named-pipe IPC, real
SQLite roundtrip, UTC/sequence ordering, and a second serial dispatch after the first
Worker reaches `ResultReceived`.

The first solution-level `--no-restore` attempt passed the 182 main tests but the
Bootstrapper project lacked the `Microsoft.Windows.SDK.NET.Ref` `any` runtime pack in
the E-drive cache (`NETSDK1112`). Restoring that runtime to the repository-local E-drive
cache fixed the environment; Bootstrapper then passed 19/19. This was not a code failure.

## Serial Worker evidence

- Real 0.2.3 development used `20260811-CAS023-001-L1`, adopted it, observed active
  count return to zero, then dispatched and adopted `20260811-CAS023-001-L2` in the
  same canonical workspace.
- `Default_active_worker_limit_is_concurrency_only_after_first_task_reaches_terminal_result`
  dispatches two different task IDs serially and asserts both results are stored.
- The natural acceptance task used only one Worker because its remaining post-review
  work was final integration; no artificial second call was made to inflate a metric.

## Final natural-prompt acceptance

Fixture: `E:\AISPace\TestSpace\cas-023-natural` (ordinary Git repository, not a worktree).
Task group: `cas-023-natural-order-repair`.
Worker task: `cas-023-natural-order-repair-impl-v1`.
Event log: `E:\AISPace\TestSpace\cas-023-natural\.natural-output\events-final.jsonl`.
Persisted evidence: `E:\AISPace\TestSpace\cas-023-natural\.natural-output\scheduler-evidence.json`.

Prompt:

> 请检查这个小型订单处理仓库，找出测试失败的原因，修复实现并补齐关键边界回归测试。请运行构建和测试，必要时处理你发现的明确问题，最后简要说明改动、验证和剩余风险。

The prompt supplied no manual decomposition and did not name the orchestration product,
Worker type, provider, or implementation Plan.

Observed persisted history:

| # | Trigger | Decision | Reason | Outcome |
| ---: | --- | --- | --- | --- |
| 1 | `INITIAL_LOCALIZATION_COMPLETE` | WORKER | `BOUNDED_IMPLEMENTATION` | Bounded four-file implementation/testing package dispatched. |
| 2 | `WORKER_RESULT_RECEIVED` | MAIN | `REVIEW_REQUIRED` | Main inspected the actual four-file diff without reimplementing it. |
| 3 | `WORKER_REVIEW_COMPLETE` | MAIN | `FINAL_INTEGRATION` | Result adopted; remaining work re-evaluated as restore/build/test integration. |
| 4 | `MODULE_COMPLETE` | MAIN | `FINAL_INTEGRATION` | E-drive restore/build/test passed; final audit remained. |
| 5 | `WORK_CONVERGED` | MAIN | `FINAL_INTEGRATION` | No remaining code issue; final delivery produced. |

The Native invocation specified `agentRole=cas_luna_worker` and `forkTurns=none`. The
Worker modified only the four authorized files and reported 8/8 tests. Main reviewed
and adopted those changes, then independently restored with an E-drive NuGet cache,
built with 0 warnings/errors, and ran 8/8 tests. No Main/Worker duplicate implementation
was detected.

Two setup probes are excluded from the valid acceptance run: one could not start the
Windows sandbox helper; another pointed at an incomplete App debug apphost rather than
the complete ToolHost output. Both were diagnosed explicitly and neither produced a
Scheduler task or Repartition record. The latter small probe completed with 153,590
input tokens (134,400 cached), 2,481 output tokens, and 449 reasoning tokens; it is
reported here rather than hidden.

## Usage and economic record

Historical 0.2.2 baseline was reused instead of paying for a new pre-change run. The
recorded baseline used one early Worker and then a long Main-only continuation of about
74 minutes.

Valid 0.2.3 natural run usage reported by Codex:

| Input | Cached input | Output | Reasoning output |
| ---: | ---: | ---: | ---: |
| 669,369 | 597,248 | 6,596 | 1,945 |

Native Worker token usage is unavailable from the current interface, so no fabricated
Worker-token total, currency cost, or savings percentage is claimed. Qualitatively, the
run demonstrates the desired ownership shift: the Worker implemented and tested all
four authorized files; Main skipped that implementation, performed bounded review,
and handled final integration. The direction is likely work-saving, while numeric token
savings remain unproven from available data.

Development Task Group economics:

- L1 policy package: adopted after R2 correction; Sol skipped the delegated full
  implementation; no duplicate work detected.
- L2 telemetry package: adopted after R2 correction and independent 15/15 verification;
  Sol skipped the delegated database/IPC/tool implementation; no duplicate work detected.
- Worker token values were unavailable for both packages. Economic result: likely saved
  implementation work, numeric savings unknown.

## Scope and limitations

- 0.2.3 does not add a planner, multi-Worker parallelism, scheduler rewrite, transport
  change, provider endpoint change, credential change, or new model-capability proof.
- Repartition sequence allocation is serialized inside the single application Scheduler
  instance; the SQLite primary key protects duplicate `(task_group_id, sequence)` rows.
- Semantic triggers depend on Main following the managed instruction. The host deliberately
  validates and stores reports rather than pretending to understand project semantics.
- Natural validation used the complete ToolHost build output and a dedicated E-drive
  pipe/data root. Initial sandbox/tool-path setup failures are environment evidence, not
  product PASS results.
