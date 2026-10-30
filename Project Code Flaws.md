# Edge cases of client-server connection which I will handle later

2026-10-17

These are the networking edge cases in the server and Unity client that are knowingly left unhandled while testing on one PC or a LAN.

| Edge case | What happens | Why it is set aside |
| --- | --- | --- |
| Duplicate CNF in one round | A client's CNF arriving twice raises confirmedClients to maxClients while another client has not confirmed, so the handshake ends early. | confirmedClients is a counter, not per-client tracking. Duplicates do not occur on a LAN. |
| Late CNF from the previous round | After the 5-second reset, a delayed CNF from the old round is counted again on top of the client's reply to the resent ACK. | A CNF delayed beyond 5 seconds does not occur on a LAN. |
| Duplicate or late ACK in the Start phase | StartGame counts every ACK message the same way, so a duplicated or late ACK can end the Start phase before all clients are ready. | Same counter logic and same LAN assumption as the handshake. |
| First game packet lost on the client | The Task.Run loop reads a message that is neither Start nor ACK, then breaks, so that packet is never processed. | Assumes that message is player data and that the next ones keep coming. |
| Silent exceptions in ConnectToServer | It is called without await, so an exception inside it does not show in the Unity console. | Exception handling is noted in a comment for later. |
| Client socket never closed | The pending ReceiveAsync keeps running after leaving Play mode because nothing closes the UdpClient in OnDestroy. | Not handled yet. |

## Gameplay code flaws

These issues affect the active game simulation after the clients have connected. They are currently acceptable for a small local prototype, but should be fixed for smooth, reliable, and cheat-resistant multiplayer gameplay.

### Server simulation and authority

| Flaw | What happens | Recommended direction |
| --- | --- | --- |
| ~~Client-controlled movement deltas~~ **Partially fixed** | Movement is now represented as sequenced input commands and the server clamps input values and duration, but the server still uses client-provided movement samples and has no collision validation. | Keep the command model, then validate movement against the authoritative world and server timing. |
| ~~Movement depends on client frame rate~~ **Partially fixed** | Input is sampled in `Update()`, so clients send different numbers of variable-duration commands. The server applies each validated command duration, preventing ordinary frame-rate-dependent speed, but packet loss can delay ordered processing. | Add bounded gap recovery or a reliable input channel when production behavior is required. |
| ~~No explicit movement speed or delta time~~ **Fixed for prototype** | Movement now uses an explicit speed (`10f`) and each input's `inputDuration` on both client and server. | Move the speed into shared configuration and make timing authoritative when hardening the protocol. |
| Diagonal movement is faster | Horizontal and vertical input can both be `1`, producing a vector length of about `1.414`. | Normalize the input vector before applying speed. |
| No server-side collision or bounds validation | The server accepts every resulting position, allowing movement outside the map, through walls, or through other gameplay constraints. | Validate movement against the authoritative world and reject or correct invalid movement. |
| ~~Client-provided player ID is trusted~~ **Fixed for movement identity** | The server assigns the authoritative ID from the sender endpoint mapping rather than accepting an ID from movement packets. | Continue using endpoint/session identity for all gameplay commands. |
| Incomplete authoritative state | `health` exists in `PlayerData` but is not initialized or updated. Other gameplay state is also not represented on the server. | Keep health, death, weapons, ammunition, cooldowns, score, respawn state, and other important state on the server. |
| No disconnect detection during gameplay | A disconnected player's state remains active indefinitely because there is no heartbeat, timeout, or last-seen check. | Track last activity and remove, pause, or respawn players after a timeout. |

### Threading and data ownership

| Flaw | What happens | Recommended direction |
| --- | --- | --- |
| ~~`List<PositionPacket>` is accessed from multiple threads~~ **Fixed** | The receive task added to a list while the simulation loop enumerated and cleared it. `List<T>` is not thread-safe, causing races, lost input, or enumeration errors. | Replaced the per-player list with a `ConcurrentQueue<PositionPacket>` and process packets with `TryDequeue`. |
| The receive task runs forever | `listeningTask` has an infinite loop and is never cancelled, awaited, or disposed. It can continue using state after the game stops. | Add a `CancellationToken`, stop the loop during shutdown, and observe task failures. |
| Receive-task failures are not surfaced | The task is started without awaiting it, so socket or parsing failures can terminate the task without the main loop knowing. | Handle and report errors explicitly, then transition the server to a safe state. |
| ~~Client network data is shared with Unity callbacks without complete synchronization~~ **Fixed for snapshot handoff** | Network code now queues complete snapshots and Unity consumes them in `FixedUpdate()`, including the latest local authoritative state. | Keep all Unity object access on the main thread. |
| ~~Snapshots can be discarded~~ **Fixed for current snapshot queue** | Complete decoded snapshots are retained in a thread-safe queue until the Unity main thread processes them. | Add a bounded queue policy if a client falls behind for a long period. |

### UDP protocol and packet reliability

| Flaw | What happens | Recommended direction |
| --- | --- | --- |
| ~~Movement packets have no sequence number~~ **Fixed** | Movement commands now carry a client `sequence`, and snapshots carry `lastProcessedInput` for reconciliation. | Add a separate sequence or command identifier for other reliable gameplay commands. |
| UDP packet loss is not handled | Lost movement or snapshot packets are silently discarded. | Use sequence numbers, resend only data that requires reliability, and make snapshots self-contained so the next snapshot can recover state. |
| ~~Duplicate and out-of-order packets are only partially handled~~ **Partially fixed** | The server now buffers movement commands by sequence and acknowledges only contiguous commands. A missing packet can still stall later movement indefinitely. | Add bounded gap recovery, timeout behavior, or a reliable input channel. |
| No packet type or protocol version | The client assumes received data is player data, making future message types and malformed packets unsafe to handle. | Add a message header containing type, version, payload length, and sequence/tick information. |
| Packet lengths are not validated | Short, malformed, or unexpected packets can cause deserialization failures or invalid state. | Validate packet length before deserializing `PositionPacket` or `PlayerData`. |
| Raw struct serialization is fragile | `Marshal.StructureToPtr` depends on struct layout and runtime assumptions and has no explicit protocol compatibility or byte-order definition. | Use explicit field serialization or a versioned binary protocol with defined endianness and layout. |
| ~~Fire-and-forget server sends hide errors~~ **Fixed for server snapshot/control sends** | Server sends are now awaited, so send failures are observable by the calling task. Client sends still use the synchronous `UdpClient.Send` overload. | Add a controlled outgoing queue if throughput requires it. |

### Client movement and rendering

| Flaw | What happens | Recommended direction |
| --- | --- | --- |
| ~~No client-side prediction~~ **Fixed for the current prototype** | The client now applies movement immediately using the input's duration and stores the command for later replay. | Use shared movement simulation and validate input timing on the server. |
| ~~No reconciliation~~ **Fixed for the current prototype** | The client starts from the authoritative position, removes acknowledged inputs, and replays unacknowledged inputs using their stored durations. | Add an explicit snapshot-available flag and perform the snapshot handoff on the Unity main thread. |
| ~~No interpolation for remote players~~ **Fixed** | The client previously applied each received position directly, so irregular packet arrival caused jitter, pauses, or small teleports. | Remote players now interpolate between the two most recent snapshots using elapsed time. |
| Remote interpolation has no snapshot buffer | Interpolation uses the latest snapshots immediately. Variable UDP delay, packet loss, or out-of-order delivery can still cause jitter because network arrival timing affects the rendered state. | Keep a short, timestamped snapshot buffer and render slightly behind real time so interpolation can continue through normal network variation. |
| ~~Complete snapshots are flattened into player records~~ **Fixed for current handoff** | The client now queues and processes one complete `PlayerData[]` snapshot per datagram. | Add snapshot timestamps and a bounded interpolation buffer. |
| ~~Reconciliation is gated by `lastProcessedInput > 0`~~ **Fixed** | Reconciliation now uses an explicit snapshot-availability flag, so acknowledgment zero is valid. | Reject stale snapshots using `serverTick`. |
| Shooting consumes the movement sequence | Shooting increments `inputSequence`, but `ShootingClientPacket` has no sequence and is not included in `lastProcessedInput`, creating gaps in movement sequences. | Use separate movement and shooting sequence spaces, or include both commands in one acknowledged command stream. |
| Direct transform writes can conflict with physics | Assigning `transform.position` can fight Unity physics when player objects use rigidbodies or colliders. | Move physics-controlled objects through the appropriate Rigidbody API on the Unity main thread. |
| Player IDs are used as array indexes without validation | A malformed or unauthorized ID can cause an `IndexOutOfRangeException` or update the wrong object. | Check IDs against the player and object array bounds before indexing. |
| Snapshot shape is hard-coded to two players | Both the client loop and buffer allocation assume exactly two players. | Include a snapshot count or use a protocol that supports dynamic player/entity counts. |

### Serialization and memory

| Flaw | What happens | Recommended direction |
| --- | --- | --- |
| ~~Unmanaged memory is not always freed~~ **Fixed** | Shared serializers now release every `AllocHGlobal()` allocation in `finally` blocks. | Replace raw marshalling with an explicit versioned protocol when compatibility becomes important. |
| ~~Serialization catches exceptions and returns `null`~~ **Fixed** | Serialization now throws invalid-input errors instead of returning a success-shaped `null` payload. | Add structured protocol error reporting at the transport boundary. |
| ~~Deserialization has no bounds checks~~ **Fixed** | Shared deserialization validates offsets and packet alignment before copying. | Add a versioned packet envelope and reject unsupported versions. |
| Repeated allocations occur at the server tick rate | Every snapshot allocates arrays and unmanaged buffers, increasing garbage collection and allocation pressure. | Reuse buffers where safe, use pooled memory, and measure before optimizing. |

### Bandwidth and scalability

| Flaw | What happens | Recommended direction |
| --- | --- | --- |
| A full snapshot is sent every server tick | The server sends data at about 60 Hz even when no state changed. This wastes bandwidth and work. | Use a suitable snapshot rate, send only changed state where appropriate, and measure the required update frequency. |
| Every client receives every player | Bandwidth and serialization work grow approximately with the square of player count. | Add interest management, visibility/proximity filtering, and per-client snapshots. |
| ~~No server tick or snapshot identifier~~ **Fixed for snapshot ordering metadata** | `PlayerData` now includes an authoritative `serverTick` alongside `lastProcessedInput`. | Add a dedicated snapshot envelope and reject stale snapshots on the client. |
| ~~Client-provided input duration is trusted~~ **Partially fixed** | The server clamps duration and input magnitude before buffering commands, but duration remains client-reported. | Derive or reconcile elapsed simulation time from server-controlled timing. |
| ~~Client-selected shooting target~~ **Fixed for prototype** | The client now sends a fire command without selecting a target. The server rewinds eligible players, selects the nearest historical intersection, validates the shooter and target state, and applies damage authoritatively. | Add historical obstacle/collider state and a server-side weapon model for production accuracy. |
| ~~Client replay inputs determine shot position~~ **Fixed for prototype** | Shooting no longer accepts client-supplied movement history. The server uses its own historical shooter snapshot and rejects shots outside the configured rewind window. | Improve historical simulation and use an authoritative weapon/muzzle representation. |
| Missing shooting cooldown validation | Without a server-owned cooldown, a client could fire unlimited shots. | The server now enforces a prototype cooldown; add weapon-specific ammo, reload, and fire-state validation. |

### Priority order for gameplay improvements

1. Add bounded gap recovery or reliability for ordered inputs.
2. Stop trusting client-provided durations and add collision/bounds validation.
3. Separate shooting and movement command sequence spaces.
4. Add packet type/version headers and reject stale snapshots using `serverTick`.
6. Fix serializer cleanup and remove silent `null` failure behavior.
7. Add collision validation, disconnect timeouts, and authoritative gameplay state.
8. Optimize snapshot frequency and add interest management when the player count grows.
