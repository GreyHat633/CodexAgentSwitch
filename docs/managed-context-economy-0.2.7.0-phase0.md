# 0.2.7.0 Managed Context Economy — Phase 0 Boundary

Date: 2026-08-22

## Supported first-version topology

```text
CAS project with Applied Snapshot and explicit Context Economy opt-in
  -> ControlledTaskService.StartInProjectAsync/CreateConversationAsync
  -> one CAS-owned CodexRuntimeManager
  -> one CAS-owned CodexAppServerClient process/connection
  -> CodexMainAgentSession creates or resumes the bound thread
  -> ManagedContextSession binds project/root/thread/task-session/app-server/lease
  -> bound events only
  -> safe-boundary decision
  -> thread/compact/start
  -> contextCompaction item/started + item/completed
  -> multi-sample verification and cooldown
```

`CodexRuntimeManager` owns a single App Server client for CAS controlled tasks. A
thread created through `ControlledTaskService` can therefore be assigned to that
connection without sharing the Desktop controller. The first version is limited
to those CAS-created conversations.

The official App Server contract confirms that `thread/compact/start` returns an
immediate `{}` acknowledgement and that completion is reported through the
standard `contextCompaction` item lifecycle. It also provides
`thread/tokenUsage/updated` and `thread/status/changed` notifications for the
active, loaded thread. See <https://learn.chatgpt.com/docs/app-server>.

## Excluded topology

- Tasks launched by `NativeCodexLauncher` are not controlled by the managed App
  Server connection and are excluded.
- Ordinary Codex Desktop tasks are excluded even if their working directory is a
  registered CAS project.
- A persisted thread that cannot prove the same project, canonical root, task
  session, App Server instance, and ownership lease is marked Lost and is not
  resumed for context control.
- A second App Server is never started to take over a Desktop-controlled thread.
- Global rollout discovery is not an ownership mechanism.

## Allow/deny matrix

| Case | Decision | Reason |
|---|---|---|
| Project absent from CAS | Deny | No managed project identity |
| Project archived | Deny | Archived projects are outside the active boundary |
| Applied Snapshot missing | Deny | Applied profile provenance is unproved |
| Context Economy disabled | Deny | Upgrade and project default is off |
| Ordinary Desktop task | Deny | No CAS controlled-task session or connection lease |
| `NativeCodexLauncher` task | Deny | CLI process is not the managed App Server controller |
| Exact registered root | Allow enrollment | Default path scope |
| Child directory without opt-in | Deny | Subdirectory management is explicit |
| Child directory with opt-in | Allow enrollment | Canonical descendant is explicitly authorized |
| Nested registered projects | Longest root wins | One task has exactly one owner |
| thread/session/root/connection mismatch | Deny | Fail-closed ownership validation |
| Ownership Pending/Compacting/Verifying/Lost/Faulted | Deny control | Only Owned or verified Idle can decide compaction |
| RPC acknowledgement only | Keep Compacting | Ack is not completion evidence |
| `contextCompaction` item completed | Enter Verifying | Authoritative completion lifecycle |

## Located production seams

- Project and Applied Snapshot: `AgentProject`, `ProjectService`,
  `SqliteProjectRepository`.
- CAS managed start path: `ControlledTaskService`.
- App Server process ownership: `CodexRuntimeManager` and
  `CodexAppServerClient`.
- Thread creation/resume and event stream: `CodexMainAgentSession`.
- Existing pressure, retry, cooldown, and effectiveness logic:
  `MainContextEconomyCoordinator` and `ContextEconomyOptions`.
- Existing persistence base: `SqliteMainContextEconomyStateStore`.
- Hook generation/hot path to freeze in Phase 2: `CodexDesktopAppLauncher`,
  `CodexAgentSwitch.ToolHost`, `SchedulerIpcServer`, and `WorkerScheduler`.

## Implementation sequence after Phase 0

1. Persist project opt-in and a `ManagedContextSession` ownership record.
2. Assign a stable App Server instance identity and ownership lease only for
   `ControlledTaskService` conversations.
3. Route token/status/turn/item events only after the full binding policy passes.
4. Move existing Context Economy decisions from post-turn/global observations to
   the bound event stream.
5. Freeze Hook production and disconnect Hook-triggered scheduler branches in a
   separate reviewed phase.
