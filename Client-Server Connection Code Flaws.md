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
