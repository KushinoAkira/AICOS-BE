# Docker stack stop investigation — 2026-10-27

Scope: READ ONLY incident investigation. Gate C runtime remains PAUSED.
All times below are Asia/Saigon (+07) unless explicitly marked UTC.

## Conclusion

**The six AICOS containers received explicit stop requests through the Docker
API from a client identifying as compose/v5.5.1 at 21:55:01.** They then shut
down normally. This is direct evidence of a coordinated Compose-client stop,
rather than six independent application crashes.

The exact initiating human, terminal, script or Desktop button is NOT proven.
The observations fit compose stop / a Compose-backed UI action. An interrupted
down sequence is also not excluded, but there is no evidence of a completed
down: all six containers and the project network remain, and the inspected
21:54–21:59 API window has zero container/network DELETE requests.

Docker's idle/Resource Saver engine cycle at about 22:00 occurred AFTER the
21:55 container stops. It is not the cause of the initial six stop requests.
No evidence supports an OOM kill as the direct cause of this incident.

## Primary retained evidence

Source A:
C:/Users/ASUS/AppData/Local/Docker/log/host/monitor.log.20261027-220009.085

Lines 3842, 3843, 3846, 3849, 3852, 3856 are incoming requests:

```text
component=apiproxy
msg=>> POST /v1.55/containers/<corresponding AICOS container ID>/stop
user_agent=compose/v5.5.1
```

These are historical API log records, NOT requests executed by this audit.

| Service | Stop request (21:55) | FinishedAt (21:55) | Exit | OOMKilled | RestartCount |
|---|---|---|---:|---|---:|
| Menu | 01.947297 | 02.908210 | 0 | false | 0 |
| Ordering | 01.947297 | 02.849402 | 0 | false | 0 |
| Redis | 01.961894 | 02.296055 | 0 | false | 0 |
| Payment | 01.972156 | 02.851988 | 0 | false | 0 |
| Table | 01.980868 | 02.887483 | 0 | false | 0 |
| Identity | 01.994693 | 02.891596 | 0 | false | 0 |

All State.Error values were empty. All six retain restart policy unless-stopped.
Compose project aicos-be is exited(6); no recreation/removal was observed.

Application logs for 21:54–21:56 were inspected without displaying raw contents:
all five .NET services contain the application-shutdown message. Redis contains
Received SIGTERM and normal user-requested shutdown/exit messages. No OOM
message was detected in those container log slices.

## Subsequent Docker idle cycle

Source B:
C:/Users/ASUS/AppData/Local/Docker/log/host/com.docker.backend.exe.log

| Local time | Source line | Evidence |
|---|---:|---|
| 21:55:07.099 | 3832 | idle timer started (5m0s), after container stops |
| 22:00:10.731 | 3869–3872 | idle stopping VM; engine running → stopping |
| 22:00:12.184 | 3946–3954 | engine stopped / idle shutdown |
| 22:00:12.541 | 3969–3978 | idle starting VM; engine stopped → starting |
| 22:00:28.689 | container state/events | Three unrelated VietAI containers start |
| 22:00:32.264 | 4160 | engine starting → running |

Docker Desktop/backend processes still had start times around 20:36, so this
evidence shows an engine idle cycle, not a Desktop application restart at 21:55.
The backend uses the label “VM” in these log messages; on WSL, Docker documents
Resource Saver as pausing the Docker Engine inside its WSL distribution, not
necessarily shutting down the shared WSL virtual machine.

[Docker Resource Saver documentation](https://docs.docker.com/desktop/use-desktop/resource-saver/)
describes the idle timer and automatic wake behavior. Read-only CLI access may
coincide with waking an idle engine; the exact triggering caller for this wake
was not attributed. This investigation issued NO start/restart command.

During the read-only audit, VietAI containers were observed running after the
engine wake. Their policy is always; AICOS policy is unless-stopped. This is
consistent with their differing behavior after a daemon cycle under
[Docker restart-policy semantics](https://docs.docker.com/engine/containers/start-containers-automatically/).
No VietAI or AICOS container was explicitly started/stopped by this audit, and
no attempt was made to reverse those automatic platform side effects.

## Docker events, commands and attribution limits

- docker events was queried with finite UTC since/until bounds around the
  incident. Returned events included the newer VietAI start/health activity
  around 22:00, but not the original AICOS stop sequence.
- Absence from this result does not disprove the older event. Docker documents
  [a limit of the last 256 retained events](https://docs.docker.com/reference/cli/docker/system/events/);
  the engine also cycled after the incident. Retained proxy logs provide the
  stronger evidence here.
- PowerShell PSReadLine history was inspected only for stop/down classifications;
  no full commands or possible secrets were displayed. The available history
  file last changed 2026-10-26 23:40:07, before this incident. It contained no
  matching compose stop line and one old compose down line, with no per-command
  timestamps. That old line is NOT attributed to today's incident.
- Windows Security process-creation events (4688) had no matching records in
  21:50–22:05. Sysmon Operational log was unavailable. Thus there is no process/
  command-line audit record tying the requests to a specific terminal/person.
- The API user-agent identifies the client string, not an authenticated human
  identity. Exact CLI stop versus Desktop action versus automation is unproven.
- No container/network DELETE request was found in the inspected API v1.55 proxy window.
  “No completed down observed” is justified; “no one ever invoked down” is not.

## Windows/system and resource checks

| Check | Observation | Interpretation |
|---|---|---|
| Windows last boot | 2026-10-27 18:46:53 | No OS reboot at 21:55 |
| System events 12/13/41/42/1074/6005/6006/6008/2004, 21:50–22:05 | No matching events | No recorded boot/shutdown/sleep/resource-exhaustion event in queried set |
| Six container OOM flags | false; exit 0 | Not an observed container OOM kill |
| VM kernel log scan | 10 retained init.log files; zero matching OOM-kill messages in 21:50–21:59 window | No retained kernel OOM evidence for this incident |
| Host visible RAM during audit | 7.74 GiB; about 0.73–0.81 GiB free | Limited CURRENT headroom; not historical usage at 21:55 |
| Docker memory / CPUs | About 3.70 GiB; 12 CPUs | Capacity, not proof of incident-time exhaustion |
| Current VietAI usage | Redis ~10 MiB, MongoDB ~294 MiB, Ollama ~108 MiB | Current sample only; AICOS was already stopped |
| Current disk free | C: ~33.71 GiB, D: ~7.28 GiB | No proof of incident-time disk exhaustion |

No historic RAM/CPU time series at 21:55 was available from these checks.
Resource pressure as an upstream motive for a person/automation stopping the
stack cannot be ruled out, but is NOT established. The directly recorded
mechanism is the six Compose-client stop requests.

Older unrelated containers with exit 137/128 from weeks/months ago were not
used as evidence for this incident. No memory setting, restart policy,
Resource Saver setting or Docker/Windows configuration was changed.

## Current status and boundaries

Engine responds, version 29.7.2; Compose CLI v5.5.1; context desktop-linux.
AICOS project remains exited(6), with original containers retained.
Kitchen remains NOT STARTED. Gate C remains PAUSED.

Only Docker/state/log/event/history classification, Windows event/resource and
filesystem capacity reads were performed. No container start/stop/restart/
recreate/delete command, image build/pull, volume action or stack mutation ran.
No SQL was executed or credentials loaded/reset. No database/config/Mobile
change or commit/push occurred. Report Markdown is the only workspace output.

## Safe continuation proposal — NOT executed

The owner must first choose the runtime scope; this READ ONLY investigation
does NOT authorize restoring containers or resuming Kitchen.

Recommended recovery if the full previously passed baseline is wanted:

1. Approve starting the six EXISTING AICOS containers: Redis, then Identity,
   Menu, Table, Ordering, Payment, one at a time. Use existing-container start
   semantics, not compose up/recreate/build. Existing IDs/images/env/credentials
   can be retained; StartedAt will necessarily change after a start.
2. Before starting, inspect immutable runtime guards privately:
   MigrateOnStartup=false on all five and Payment mutations=false. Keep clients
   from issuing business mutations during the controlled smoke window.
3. Require Redis healthy, then live/ready/metadata checks after each approved
   service start. Stop the sequence and report any failure. Monitor host/VM
   memory; avoid concurrent builds. Do not stop unrelated stacks automatically.
4. Recheck the authorized 25-table READ ONLY baseline. Old fingerprints/PASS
   results are historical, not proof of a freshly restored runtime.
5. Only after explicit Kitchen resume approval, run its scoped SELECT-matrix/
   fingerprint and GET-only smoke plan; no Gateway or further service.

Alternative: explicitly keep the five services stopped and approve only the
existing Redis dependency plus Kitchen. Kitchen currently uses its reviewed
direct SQL exception and no active typed service client. That plan needs the
preflight changed to preserve the stopped baseline rather than requiring the
five services running, and must not be silently chosen by the agent.

No reset, volume deletion, migration, credentials reset, global Resource Saver
change or resource configuration change is required by the current evidence.

