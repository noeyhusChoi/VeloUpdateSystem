# Update Agent and Watchdog Design (v1.0)

## 1. Purpose and Scope
This document defines the architecture and IPC protocol for a Windows Service based
UpdateAgent and Watchdog that manage a Velopack-updated WPF app.

## 2. Components and Responsibilities
### UpdateAgent (Windows Service, LocalSystem)
- Central controller for all update decisions.
- Identifies target App by packId.
- Computes current version via Velopack.
- Schedules periodic update checks.
- Downloads and applies updates.
- Decides installation timing.
- Requests App shutdown before install.
- Determines hang via Heartbeat.
- Publishes status flags to Watchdog.

### Watchdog (Windows Service, LocalSystem)
- Final safety net that monitors process liveness.
- Detects crashes and missing processes.
- Restarts App and/or Agent with backoff.
- Executes forced termination on Agent request.

### App (WPF)
- Displays state to user.
- Reports idle and responsiveness to Agent.
- Cooperates with graceful shutdown and restart.

## 3. Execution Model
- Both services run at boot.
- Agent runs on its own schedule (default interval is configurable).
- Watchdog continuously monitors App/Agent processes.
- App sends Heartbeat on a fixed interval.

## 4. Security Model
- Named Pipes with explicit ACLs.
- App <-> Agent pipe is user-session restricted.
- Agent <-> Watchdog pipe is LocalSystem restricted.

## 5. IPC Transport
- Named Pipes
- App <-> Agent: `\\.\pipe\Moneybox.Agent`
- Agent <-> Watchdog: `\\.\pipe\Moneybox.Watchdog`
- Watchdog <-> App: `\\.\pipe\Moneybox.Watchdog.App` (optional)
- Payload encoding: JSON

## 6. Message Envelope (Standard)
```json
{
  "id": "uuid",
  "type": "MessageType",
  "source": "App|Agent|Watchdog",
  "target": "App|Agent|Watchdog",
  "timestamp": "ISO-8601",
  "correlationId": "uuid",
  "payload": { }
}
```

## 7. Common State Model (Agent-Owned)
```json
{
  "appId": "VeloUpdateSystem",
  "packId": "VeloUpdateSystem",
  "currentVersion": "1.0.1-beta.8",
  "availableVersion": "1.0.2-beta.1",
  "channel": "beta|stable",
  "state": "Idle|Checking|Downloading|ReadyToInstall|Installing|RestartPending|Error",
  "progress": { "percent": 0, "bytes": 0, "totalBytes": 0 },
  "hangDetected": false,
  "lastError": { "code": "string", "message": "string" }
}
```

## 8. Message Types (Minimal Set, Final)
### App <-> Agent
- `Status`
  - Request payload: `{ "want": "status" }`
  - Response payload: Common State Model
- `Heartbeat`
  - Payload: `{ "pid": 1234, "responsive": true, "idleMinutes": 12 }`
- `PrepareToExit` (Agent -> App)
  - Payload: `{ "reason": "updateInstall", "timeoutSec": 30 }`

### Agent <-> Watchdog
#### `WatchdogStatus` (Agent -> Watchdog)
- Required fields:
  - `appPid` (int or null)
  - `agentPid` (int)
  - `appExpected` (bool)
  - `appRunning` (bool)
  - `hangDetected` (bool)
- Example:
  - `{ "appPid": 1234, "agentPid": 4321, "appExpected": true, "appRunning": true, "hangDetected": false }`

#### `Restart` (Agent -> Watchdog)
- Required fields:
  - `target` ("App" | "Agent")
  - `reason` ("updateInstalled" | "hung" | "crash" | "agentRequest")
  - `minDelaySec` (int)
- Example:
  - `{ "target": "App", "reason": "updateInstalled", "minDelaySec": 5 }`

#### `ProcessMissing` (Watchdog -> Agent)
- Required fields:
  - `target` ("App" | "Agent")
  - `missingSinceSec` (int)
- Example:
  - `{ "target": "App", "missingSinceSec": 120 }`

### Watchdog <-> App (Optional)
- `ForceExit` (Watchdog -> App)
  - Payload: `{ "reason": "agentRequest|hung" }`

## 9. Sequences (Standard Flows)
### Update Apply
1) Agent checks updates and downloads package.
2) Agent -> App: `PrepareToExit`.
3) App exits or times out.
4) Agent applies update.
5) Agent -> Watchdog: `Restart` (App).

### Hang Detection
1) App sends `Heartbeat` periodically.
2) Agent detects missed heartbeat and sets `hangDetected=true`.
3) Agent -> Watchdog: `Restart` or `ForceExit` (optional).

### Process Missing
1) Watchdog detects missing App/Agent process.
2) Watchdog -> Agent: `ProcessMissing`.
3) Watchdog applies backoff and restarts target.

## 10. Backoff Policy (Watchdog)
- Example: max 5 restarts per 10 minutes, then wait 30 minutes.
- Policy is configurable via service settings.

## 11. Versioning and Compatibility
- Envelope is stable; new fields must be optional.
- Enums may expand without breaking older clients.
- Time units must be explicit (`idleMinutes`, `timeoutSec`).
