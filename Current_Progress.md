# Multiplayer Top-Down Shooter — Current Progress

**Last Updated:** 2026-10-30

---

## 1. Project Overview

This repository contains a **1v1 Authoritative UDP Multiplayer Top-Down Shooter** built with **Unity 6 (URP)** and a standalone **.NET 10 Authoritative UDP Server**, sharing a common binary serialization and deterministic machine-learning library (`MyExternalLibrary`).

### Repository Structure
| Path | Description |
| --- | --- |
| [`Both/data.cs`](Both/data.cs) | Shared binary packet definitions (`MovementClientPacket`, `ShootingClientPacket`, `PlayerData`) and `Marshal`-backed packet serializers (`DataPacketConvertor`). |
| [`Both/ChronosModel.cs`](Both/ChronosModel.cs) | Shared native C# **Project Chronos** engine: transpiled 10-tree Random Forest Classifier (Tier 1), Vector Steering Action Engine (Tier 2), deterministic ledger generator, and Retrospective Anti-Cheat verifier. |
| [`Server/Program.cs`](Server/Program.cs) | Authoritative 60 Hz UDP game server handling connection handshakes, movement validation, historical rewind hit-verification, and Project Chronos reconnection audits. |
| [`Client_TopDownShooter/Assets/Client.cs`](Client_TopDownShooter/Assets/Client.cs) | Unity client networking, input sampling, shooting, health UI updates, telemetry logging, and Quantum Splinter timeline simulation. |
| [`Client_TopDownShooter/Assets/LagCompensation.cs`](Client_TopDownShooter/Assets/LagCompensation.cs) | Client-side prediction, server reconciliation, and remote player linear interpolation. |
| [`Data/train_and_transpile_chronos.py`](Data/train_and_transpile_chronos.py) | End-to-end Python pipeline that cleans real CS:GO match snapshots, trains the deterministic Random Forest (`random_state=42`), evaluates metrics, and transpiles trees into C#. |

---

## 2. Completed Features

### A. Core Networking & Lag Compensation
- **UDP Handshake & Session Start**:
  - 3-way connection handshake (`REQ` $\rightarrow$ `ACK_<id>` $\rightarrow$ `CNF`) followed by synchronized `Start` broadcast for 2 players.
- **Client-Side Prediction & Server Reconciliation**:
  - Sequenced movement inputs (`MovementClientPacket`) are predicted immediately on the client and buffered in `LagCompensation.inputHistory`.
  - Server clamps input magnitude and duration, advances authoritative state at 60 Hz, and returns `lastProcessedInput` and `serverTick` in each `PlayerData` snapshot.
  - Client reconciles authoritative positions and replays unacknowledged inputs.
- **Remote Player Interpolation**:
  - Smoothly interpolates remote opponent positions between snapshots in `LagCompensation.LinearInterpolate`.
- **Authoritative Server-Rewind Shooting**:
  - Client fires rays from `PlayerObject/GunPos` and sends `ShootingClientPacket` with `observedServerTick` and ray direction.
  - Server rewinds historical player snapshots (up to 720 ticks / 12 seconds), validates cooldown and ray intersection, applies `25` damage per hit, and updates `health` and `killCount`.

### B. Scene Setup, Visuals & Health Bars
- **Aiming Ray (`GunPos` + `LineRenderer`)**:
  - Added `GunPos` child transform at the head area (`(0, 0.6, 0)`) of `Assets/PlayerObject.prefab`.
  - Configured both `Debug.DrawRay` and a runtime `LineRenderer` (`Sprites/Default` shader, red, `0.05` width) on the local player's `GunPos` so the aiming laser is visible in both **Play Mode** and **Standalone Builds**.
- **Dual Health Bar System**:
  - **Local Player UI Health Bar**: Top-left screen UI bar (`Canvas/PlayerHealthBar/Fill`) wired to `Client.playerHealthBarFill`.
  - **Opponent Overhead Health Bar**: World-space `HealthBar` (`PlayerObject/HealthBar/Fill`) positioned above the player (`(0, 1.4, 0)`). Automatically disabled on the local player instance at spawn so it only renders above the opponent.

### C. Project Chronos: Decentralized Latency Compensation Framework (Phases 1–7)
Implemented per [`Decentralized_Latency_Compensation_Framework_v3.pdf`](Decentralized_Latency_Compensation_Framework_v3.pdf):

1. **Phase 1 — Engine Telemetry Logging Instrumentation**:
   - Live combat telemetry (`Distance_To_Enemy`, `Enemy_Visible`, `Player_HP`, `Player_Ammo`, `In_Chokepoint`, `Intent_Label`, `Vector_X_To_Enemy`, `Vector_Y_To_Enemy`, `Move_Input_X`, `Move_Input_Y`, `Trigger_Shoot`) is recorded during matches to `Client_TopDownShooter/Assets/TelemetryLogs/live_telemetry.csv`.
2. **Phase 2 — Real Dataset Injection & Pre-Processing**:
   - Downloaded and cleaned `122,410` real competitive Counter-Strike snapshots (`Data/RawBehavior/csgo_round_snapshots.csv`), filtering `1,166` invalid post-round rows to retain `121,244` valid combat snapshots and extracting `12,000` behavioral vectors into `Data/RawBehavior/behavioral_cloning_baseline.csv` and `Assets/TelemetryLogs/baseline_telemetry.csv`.
3. **Phase 3 — Deterministic ML Pipeline Training (`random_state=42`)**:
   - Strictly split into **Train (`8,400`)**, **Validation (`1,800`)**, and **Test (`1,800`)** before fitting:
     - **Naive Majority Baseline**: `48.17%` Validation Accuracy (`0.2167` Macro-F1)
     - **Depth-2 Decision Stump Baseline**: `90.06%` Validation Accuracy (`0.8881` Macro-F1)
     - **Tier 1 Random Forest (`n_estimators=10`, `random_state=42`)**: **`97.89%`** Validation Accuracy, **`98.33%`** Test Accuracy (**`0.9818`** Macro-F1)
4. **Phase 4 — Native C# Model Transpilation**:
   - Transpiled all 10 trained trees (`EvaluateTree0`–`EvaluateTree9`) and the **Tier 2 Action Execution Engine** into [`Both/ChronosModel.cs`](Both/ChronosModel.cs) with zero external runtime dependencies.
5. **Phase 5 — "Quantum Splinter" Integration & Visual FX**:
   - During a 5-second packet dropout, the client enters a **Timeline Splinter** state where the opponent is smoothly driven by the compiled Tier 1 + Tier 2 behavioral brain instead of freezing.
   - Wired `Global Volume`, `Canvas/ChronosGlitchOverlay`, and `Canvas/ChronosAlertBanner` to display active splinter status and the **"REALITY REALIGNED — SYNC LINK RESTORED"** visual glitch upon reconnection.
6. **Phase 6 & 7 — Retrospective Anti-Cheat Handshake & Sandbox Validation**:
   - On reconnect, the client sends a `1,449-byte` (`< 2 KB`) 60-tick ledger payload (`ChronosReconnectionCommand`, packet type `2`).
   - Server reconstructs the deterministic ledger from its historical snapshot buffer and verifies `stateHash` and input drift (`error_tolerance = 0.01f`).
   - Verified both normal reconnection (`VERIFIED: Sync_Timeline_State`) and RAM coordinate drift injection (`BANNED: Input Drift Triggered -> Terminate_Session`).
7. **Server-Side Chronos AI Takeover on Client Disconnect**:
   - Connected clients send lightweight `HeartbeatClientPacket` (packet type `3`) every `0.1s` (`10 Hz`), paused during Quantum Splinter dropouts or actual disconnects.
   - When the server detects no packets from a client for `30` ticks (`~0.5s`), it transitions that player to server-side Chronos AI control (`ChronosModel.SimulateSplinterTick`), directly advancing the disconnected player's `(x, y)` coordinates and executing AI shooting (`triggerShoot`, `aiShotCooldownTicks = 30`, `25` damage) in authoritative `PlayerData[]` snapshots broadcast to connected peers.
   - Upon receiving packets again (and passing the Chronos anti-cheat audit if reconnecting from a splinter), the server seamlessly hands live control back to the client.
8. **Live Telemetry Recording, Pre-Processing & Per-Player Model Activation (Zero Network Overhead)**:
   - Recorded **3 live user-played 2-minute sessions** (`546` + `1,931` + `4,332` = **`6,809` raw samples** in [`player_0_telemetry.csv`](Client_TopDownShooter/Assets/TelemetryLogs/player_0_telemetry.csv)), filtered `1,994` brief key-transition idle frames to retain **`4,815` active moving & shooting frames** for Player 0, and preserved the **`720` static peer samples** in [`player_1_telemetry.csv`](Client_TopDownShooter/Assets/TelemetryLogs/player_1_telemetry.csv) untouched (`17,535` total merged training rows).
   - Trained and transpiled three local 10-tree Random Forest models (30 trees total in [`Both/ChronosModel.cs`](Both/ChronosModel.cs) and JSON artifacts in `Data/Models/`):
     - **`P0_ActiveMoverShooter_RF10` (Player 0 Model)**: **`100.00%` Val & Test Acc** (`1.0000` Macro-F1) — reproduces your recorded gameplay by positioning `~5.0m–6.8m` below the opponent, weaving `X` (`±0.26m`) and `Y` to keep the `+Z` aiming laser aligned on target, and shooting continuously.
     - **`P1_StationaryPeer_RF10` (Player 1 Model)**: **`100.00%` Val & Test Acc** (`1.0000` Macro-F1) — preserves the static second-window behavior (`PassiveStationary = 3`, `mx = 0`, `my = 0`, `triggerShoot = 0`).
     - **`Global_Baseline_RF10` (Default Fallback Model)**: `95.31%` Val Acc, **`94.83%` Test Acc** (`0.9239` Macro-F1).
   - Because all instances run on the same local machine, all player models reside locally in `ChronosModel` and are activated dynamically via `ChronosModel.ActivatePlayerModel(playerId)` with zero network model-transfer overhead.

---

## 3. Controls & Testing Guide

| Input / UI Control | Action |
| --- | --- |
| **`W` `A` `S` `D` / Arrow Keys** | Move local player (`Horizontal` / `Vertical` axes) |
| **Left Mouse Button (`Mouse0`)** | Fire raycast shot along `GunPos.forward` |
| **`L` Key / `5s Splinter (L)` Button** | Trigger a simulated 5-second **Quantum Splinter** packet dropout |
| **`C` Key / `Cheat Drift (C)` Button** | Toggle `+0.35f` RAM ledger coordinate drift injection for Anti-Cheat testing |

---

## 4. Current Limitations & Next Steps

- **Player / Camera Rotation**: Neither the player controller nor the Tier 2 AI model currently rotates `transform.rotation` (yaw/aim); movement is purely translational (`X`/`Z`) and shooting fires along `+Z`. Adding mouse-to-world aim rotation and an `Aim_Yaw` output to Tier 2 is a natural next step.
- **Diagonal Movement Normalization**: Input vector `(horizontal, vertical)` is clamped per axis rather than normalized, allowing faster diagonal movement.
