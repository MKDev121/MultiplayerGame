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
| Client-controlled movement deltas | The client sends `deltaX` and `deltaY`, and the server adds them directly to the player position. A modified client can send arbitrarily large movement or move through obstacles. | Send input commands to the server. The server should calculate movement, speed, timing, collision, and gameplay validity authoritatively. |
| Movement depends on client frame rate | Input is sent from `Update()`, so clients with different frame rates send different numbers of movement packets and can move at different speeds. | Process movement on a fixed server tick using server-side time. |
| No explicit movement speed or delta time | The server adds raw input values without speed, acceleration, or a time step. Movement speed depends on packet frequency and batching. | Define movement speed and apply `direction * speed * serverTickDelta`. |
| Diagonal movement is faster | Horizontal and vertical input can both be `1`, producing a vector length of about `1.414`. | Normalize the input vector before applying speed. |
| No server-side collision or bounds validation | The server accepts every resulting position, allowing movement outside the map, through walls, or through other gameplay constraints. | Validate movement against the authoritative world and reject or correct invalid movement. |
| Client-provided player ID is trusted | The server copies `data.id` into the authoritative player state even though it already knows the sender's player ID from its endpoint. A client can impersonate another player. | Ignore the packet ID for identity and use the server-side client-to-player mapping. |
| Incomplete authoritative state | `health` exists in `PlayerData` but is not initialized or updated. Other gameplay state is also not represented on the server. | Keep health, death, weapons, ammunition, cooldowns, score, respawn state, and other important state on the server. |
| No disconnect detection during gameplay | A disconnected player's state remains active indefinitely because there is no heartbeat, timeout, or last-seen check. | Track last activity and remove, pause, or respawn players after a timeout. |

### Threading and data ownership

| Flaw | What happens | Recommended direction |
| --- | --- | --- |
| `List<PositionPacket>` is accessed from multiple threads | The receive task adds to a list while the simulation loop enumerates and clears it. `List<T>` is not thread-safe, causing races, lost input, or enumeration errors. | Use `ConcurrentQueue<PositionPacket>` or synchronized input-buffer swapping at the start of each server tick. |
| The receive task runs forever | `listeningTask` has an infinite loop and is never cancelled, awaited, or disposed. It can continue using state after the game stops. | Add a `CancellationToken`, stop the loop during shutdown, and observe task failures. |
| Receive-task failures are not surfaced | The task is started without awaiting it, so socket or parsing failures can terminate the task without the main loop knowing. | Handle and report errors explicitly, then transition the server to a safe state. |
| Client network data is shared with Unity callbacks without synchronization | `ListenToServer()` writes `players` while `FixedUpdate()` reads and sets it to `null`. A snapshot can be read while it is being replaced. | Receive a complete snapshot on the network side and hand it to the Unity main thread through a thread-safe queue or latest-snapshot slot. |
| Snapshots can be discarded | Setting `players = null` can lose snapshots that arrive before the next `FixedUpdate()`. | Keep complete snapshots and deliberately choose whether to render the latest snapshot or interpolate between snapshots. |

### UDP protocol and packet reliability

| Flaw | What happens | Recommended direction |
| --- | --- | --- |
| Gameplay packets have no sequence number | The client cannot identify an old packet, a duplicate packet, or an out-of-order packet. | Add input sequence numbers and server snapshot/tick numbers. |
| UDP packet loss is not handled | Lost movement or snapshot packets are silently discarded. | Use sequence numbers, resend only data that requires reliability, and make snapshots self-contained so the next snapshot can recover state. |
| Duplicate and out-of-order packets are not handled | A duplicated movement packet can move a player twice; an old packet can be applied after a newer packet. | Reject stale input or snapshots and make packet processing sequence-aware. |
| No packet type or protocol version | The client assumes received data is player data, making future message types and malformed packets unsafe to handle. | Add a message header containing type, version, payload length, and sequence/tick information. |
| Packet lengths are not validated | Short, malformed, or unexpected packets can cause deserialization failures or invalid state. | Validate packet length before deserializing `PositionPacket` or `PlayerData`. |
| Raw struct serialization is fragile | `Marshal.StructureToPtr` depends on struct layout and runtime assumptions and has no explicit protocol compatibility or byte-order definition. | Use explicit field serialization or a versioned binary protocol with defined endianness and layout. |
| Fire-and-forget sends hide errors | `SendAsync()` is called without awaiting or observing its task, so send failures are invisible and outstanding sends can accumulate. | Await sends or use a controlled outgoing queue with error handling. |

### Client movement and rendering

| Flaw | What happens | Recommended direction |
| --- | --- | --- |
| No client-side prediction | The local player moves only after a full input-to-server-to-client round trip, which creates visible input latency. | Predict local movement immediately, then reconcile with authoritative server snapshots. |
| No reconciliation | There is no mechanism to compare predicted state with server state or replay inputs that the server has not acknowledged. | Include the last processed input sequence in snapshots and replay pending inputs after correction. |
| No interpolation for remote players | The client directly applies each received position, so irregular packet arrival causes jitter, pauses, or small teleports. | Buffer snapshots briefly and interpolate remote players between server states. |
| Direct transform writes can conflict with physics | Assigning `transform.position` can fight Unity physics when player objects use rigidbodies or colliders. | Move physics-controlled objects through the appropriate Rigidbody API on the Unity main thread. |
| Player IDs are used as array indexes without validation | A malformed or unauthorized ID can cause an `IndexOutOfRangeException` or update the wrong object. | Check IDs against the player and object array bounds before indexing. |
| Snapshot shape is hard-coded to two players | Both the client loop and buffer allocation assume exactly two players. | Include a snapshot count or use a protocol that supports dynamic player/entity counts. |

### Serialization and memory

| Flaw | What happens | Recommended direction |
| --- | --- | --- |
| Unmanaged memory is not always freed | `AllocHGlobal()` is used in the serializers without guaranteed `FreeHGlobal()`. Repeated snapshots can leak unmanaged memory. | Put every allocation in a `try/finally`, or replace the serializer with a safer explicit serializer. |
| Serialization catches exceptions and returns `null` | A serialization failure becomes a success-shaped value that can later cause a send or parsing failure with less useful context. | Let the error propagate or report it using the project's standard error handling; do not silently return `null`. |
| Deserialization has no bounds checks | `fromBytes<T>()` copies from an offset without verifying that the input contains a complete value. | Validate the offset and required size before allocating and copying. |
| Repeated allocations occur at the server tick rate | Every snapshot allocates arrays and unmanaged buffers, increasing garbage collection and allocation pressure. | Reuse buffers where safe, use pooled memory, and measure before optimizing. |

### Bandwidth and scalability

| Flaw | What happens | Recommended direction |
| --- | --- | --- |
| A full snapshot is sent every server tick | The server sends data at about 60 Hz even when no state changed. This wastes bandwidth and work. | Use a suitable snapshot rate, send only changed state where appropriate, and measure the required update frequency. |
| Every client receives every player | Bandwidth and serialization work grow approximately with the square of player count. | Add interest management, visibility/proximity filtering, and per-client snapshots. |
| No server tick or snapshot identifier | Clients cannot determine the age or ordering of world state and cannot associate a snapshot with processed input. | Include authoritative server tick, snapshot sequence, and last processed input sequence. |

### Priority order for gameplay improvements

1. Make server input processing and player buffers thread-safe.
2. Stop trusting client-provided player IDs and validate every packet.
3. Replace raw client movement deltas with validated input and fixed server-tick movement.
4. Add packet sequence numbers, server tick numbers, and packet type/version headers.
5. Fix serializer cleanup and remove silent `null` failure behavior.
6. Add client snapshot handoff, remote-player interpolation, and local-player prediction/reconciliation.
7. Add collision validation, disconnect timeouts, and authoritative gameplay state.
8. Optimize snapshot frequency and add interest management when the player count grows.
