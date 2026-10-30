// See https://aka.ms/new-console-template for more information

using System.Data;
using System.Data.SqlTypes;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Linq;
using MyExternalLibrary;
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;

internal class Program
{
    private static async Task Main(string[] args)
    {
        int maxClients = 2;
        int clientsConnected = 0;
        int confirmedClients = 0;
        bool confirmationProcessStarted=false;
        var cts = new CancellationTokenSource();
        float serverDelta = 1/60f;
        int serverTick = 0;
        Dictionary<IPEndPoint,int> clients = new Dictionary<IPEndPoint, int>();
        Dictionary<int, PlayerData> players = new Dictionary<int, PlayerData>();

        Dictionary<int, ConcurrentQueue<object>> playerDataBuffers = new Dictionary<int, ConcurrentQueue<object>>();
        UDP_Server server = new();
        Console.WriteLine($"IpAddress: {server.getIP()}");
        //PeriodicTimer timer = new PeriodicTimer(TimeSpan.FromMilliseconds(16.67f));
        while (true)
        {
            Received received = await server.Receive();
            if (!clients.ContainsKey(received.sender))// Connection Logic
            {
                Console.WriteLine("hi");
                if (received.message.Contains("REQ")&&clientsConnected<maxClients)// A client wants to connect
                {
                    
                    await server.Send("ACK_"+clientsConnected.ToString(), received.sender);
                    clients.Add(received.sender,clientsConnected++);
                    

                }
                if(clientsConnected==maxClients&&!confirmationProcessStarted){// this will be called when all clients get connected
                    confirmationProcessStarted=true;
                    Task confirmTimerTask = Task.Run(async () =>
                    {

                            var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
                            try{
                            while(await timer.WaitForNextTickAsync(cts.Token))
                            {
                                if(Volatile.Read(ref confirmedClients)<clientsConnected){
                                    Interlocked.Exchange(ref confirmedClients,0);
                                    foreach(IPEndPoint client in clients.Keys)
                                    {
                                        await server.Send("ACK_"+clients[client].ToString(),client);
                                    }
                                }else{
                                    timer.Dispose();
                                    break;
                                }


                            }
                        }
                        catch (OperationCanceledException)
                        {
                            timer.Dispose();
                        }

                            
                        
                    });

                }
            }else{
                if (received.message.Contains("CNF"))// Client got the acknowledgement 
                {
                    
                    Interlocked.Increment(ref confirmedClients);
                    // we will have time out if we didn't get confirmation from all clients we reset confirmedclients and send acknowledgement again
                    //playerDataBuffers.Add(received.sender, new List<PositionPacket>());
                    
                    // PlayerData player = new PlayerData();
                    // player.x = 0;
                    // player.y = 0;
                    // players.Add(received.sender, player);
                }



            }
            if (Volatile.Read(ref confirmedClients) == maxClients){
                    cts.Cancel();
                    break;
            }

        }
            //After this server will start the game by sending start message to all clients
            await StartGame();
            async Task StartGame()
            {
                cts= new CancellationTokenSource();
                Interlocked.Exchange(ref confirmedClients,0);
                foreach(IPEndPoint client in clients.Keys)
                {
                    await server.Send("Start",client);
                }
                Task startTimerTask = StartTimer();
                while(true)
                {
                    Received result=await server.Receive();
                    if(result.message.Contains("ACK"))
                    {
                        Interlocked.Increment(ref confirmedClients);
                    }
                    if(Volatile.Read(ref confirmedClients)==clientsConnected){
                        cts.Cancel();
                        for(int i=0;i<confirmedClients;i++)
                        {

                            playerDataBuffers.Add(i,new ConcurrentQueue<object>());//data which server receives
                            players.Add(i,new PlayerData { id = i, health = 100 });//data which server sends
                        }
                        break;
                    }
                }
                cts.Cancel();
                await startTimerTask;
            }
            async Task StartTimer()
            {
                var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
                try{
                while(await timer.WaitForNextTickAsync(cts.Token))
                {
                    if(Volatile.Read(ref confirmedClients)<clientsConnected){
                        Interlocked.Exchange(ref confirmedClients,0);
                        foreach(IPEndPoint client in clients.Keys)
                        {
                            await server.Send("Start",client);
                        }
                    }else
                    {
                        timer.Dispose();
                        break;
                    }
                }
                }catch(OperationCanceledException)
                {
                    timer.Dispose();
                }

                
            }
        await Game();
        async Task Game()
        {
            const float maxShootRange = 20f;
            const float maxHitDistanceFromRay = 0.75f;
            const int shotDamage = 25;
            const int historyLength = 720;
            const int maxRewindTicks = 720;
            const int shotCooldownTicks = 6;
            const int aiShotCooldownTicks = 30;
            const int disconnectTimeoutTicks = 30; // ~0.5s at 60 Hz
            var snapshotHistory = new Queue<KeyValuePair<int, PlayerData[]>>();
            var pendingInputs = new Dictionary<int, SortedDictionary<int, MovementClientPacket>>();
            var nextInputSequence = new Dictionary<int, int>();
            var lastShotTick = new Dictionary<int, int>();
            var lastSeenTick = new Dictionary<int, int>();
            var isDisconnected = new Dictionary<int, bool>();
            var bannedClients = new HashSet<IPEndPoint>();
            for (int playerId = 0; playerId < clientsConnected; playerId++)
            {
                pendingInputs[playerId] = new SortedDictionary<int, MovementClientPacket>();
                nextInputSequence[playerId] = 1;
                lastShotTick[playerId] = -shotCooldownTicks;
                lastSeenTick[playerId] = 0;
                isDisconnected[playerId] = false;
            }

            Task listeningTask = Task.Run(async () =>
            {
                while (true)
                {
                    ReceivedPlayerData received;
                    try
                    {
                        received = await server.ReceivePlayerData();
                    }
                    catch (SocketException exception)
                    {
                        Console.WriteLine($"[Network] Receive socket warning (client may have disconnected): {exception.Message}");
                        continue;
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                    
                    if (received.data != null && clients.ContainsKey(received.sender))
                    {
                        playerDataBuffers[clients[received.sender]].Enqueue(received.data);
                    }
                }
            }
            );


                //do computation of buffer values of clients at 60 tick rate
            var timer = new PeriodicTimer(TimeSpan.FromSeconds(serverDelta));
            while (await timer.WaitForNextTickAsync())
            {
            
                //Console.WriteLine("Tick");
                //logic 
                foreach (IPEndPoint client in clients.Keys)
                {
                    if (bannedClients.Contains(client))
                        continue;

                    int playerId = clients[client];
                    PlayerData player = players[playerId];

                    while (playerDataBuffers[playerId].TryDequeue(out object? data))
                    {
                        lastSeenTick[playerId] = serverTick;
                        if (isDisconnected[playerId])
                        {
                            isDisconnected[playerId] = false;
                            Console.WriteLine($"[Chronos Server AI] player_{playerId} reconnected at serverTick={serverTick}. Resuming live client control.");
                        }

                        if(data is MovementClientPacket mov)
                        {
                            const float maxInputDuration = 0.1f;
                            const float maxInputMagnitude = 1f;
                            float duration = Math.Clamp(mov.inputDuration, 0f, maxInputDuration);
                            float horizontal = Math.Clamp(mov.deltaX, -maxInputMagnitude, maxInputMagnitude);
                            float vertical = Math.Clamp(mov.deltaY, -maxInputMagnitude, maxInputMagnitude);

                            if (mov.sequence >= nextInputSequence[playerId] &&
                                !pendingInputs[playerId].ContainsKey(mov.sequence))
                            {
                                pendingInputs[playerId][mov.sequence] = new MovementClientPacket
                                {
                                    sequence = mov.sequence,
                                    deltaX = horizontal,
                                    deltaY = vertical,
                                    inputDuration = duration
                                };
                            }
                        }
                        else if(data is ShootingCommand shotCommand)
                        {
                            if (VerifyShot(playerId, shotCommand, out int targetPlayerId))
                            {
                                lastShotTick[playerId] = serverTick;
                                PlayerData target = players[targetPlayerId];
                                target.health = Math.Max(0, target.health - shotDamage);
                                players[targetPlayerId] = target;
                                if (target.health == 0)
                                    player.killCount++;
                                Console.WriteLine($"[Shoot] player_{playerId} HIT player_{targetPlayerId} (targetHealth={target.health}, kills={player.killCount})");
                            }
                            else
                            {
                                Console.WriteLine($"[Shoot] player_{playerId} MISS/REJECTED (observedTick={shotCommand.shot.observedServerTick}, serverTick={serverTick})");
                            }
                        }
                        else if(data is ChronosReconnectionCommand reconCommand)
                        {
                            int opponentId = playerId == 0 ? 1 : 0;
                            if (!TryGetHistoricalSnapshot(reconCommand.header.startServerTick, opponentId, out PlayerData histOpponent))
                                histOpponent = players[opponentId];
                            if (!TryGetHistoricalSnapshot(reconCommand.header.startServerTick, playerId, out PlayerData histPlayer))
                                histPlayer = players[playerId];

                            TimelineLedgerTick[] serverLedger = ChronosModel.GenerateDeterministicLedger(
                                reconCommand.header.startServerTick,
                                reconCommand.header.tickCount,
                                histOpponent.x,
                                histOpponent.y,
                                histOpponent.health,
                                histPlayer.x,
                                histPlayer.y);

                            VerificationResult audit = ChronosModel.VerifyReconnectionPayload(
                                serverLedger,
                                reconCommand.ledger,
                                ChronosModel.DefaultErrorTolerance);

                            Console.WriteLine($"[Chronos Audit] player_{playerId} Reconnection Verdict={audit.verdict} Reason={audit.reason} Action={audit.action} (ticks={reconCommand.header.tickCount})");
                            await server.Send($"CHRONOS:{audit.verdict}:{audit.reason}:{audit.action}", client);

                            if (!audit.verified)
                            {
                                bannedClients.Add(client);
                                Console.WriteLine($"[Chronos Audit] Connection terminated for player_{playerId} ({client}) due to {audit.reason}.");
                            }
                        }
                        else if(data is HeartbeatClientPacket)
                        {
                            // Heartbeat processed above via lastSeenTick update
                        }
                    }

                    if (bannedClients.Contains(client))
                        continue;

                    if (!isDisconnected[playerId] && (serverTick - lastSeenTick[playerId]) >= disconnectTimeoutTicks)
                    {
                        isDisconnected[playerId] = true;
                        pendingInputs[playerId].Clear();
                        Console.WriteLine($"[Chronos Server AI] player_{playerId} disconnected (no packets for {serverTick - lastSeenTick[playerId]} ticks). Generating snapshots via Chronos Model on server.");
                    }

                    if (isDisconnected[playerId])
                    {
                        int opponentId = playerId == 0 ? 1 : 0;
                        PlayerData opponent = players[opponentId];
                        float aiHp = player.health > 0 ? player.health : 100f;

                        TimelineLedgerTick aiTick = ChronosModel.SimulateSplinterTick(
                            serverTick,
                            player.x,
                            player.y,
                            aiHp,
                            opponent.x,
                            opponent.y);

                        player.x = MathF.Round((player.x + aiTick.mx * serverDelta * 10f) * 100f) / 100f;
                        player.y = MathF.Round((player.y + aiTick.my * serverDelta * 10f) * 100f) / 100f;

                        if (aiTick.triggerShoot == 1 &&
                            player.health > 0 &&
                            opponent.health > 0 &&
                            serverTick - lastShotTick[playerId] >= aiShotCooldownTicks)
                        {
                            float toTargetX = opponent.x - player.x;
                            float toTargetY = opponent.y - player.y;
                            float targetDistance = MathF.Sqrt(toTargetX * toTargetX + toTargetY * toTargetY);
                            if (targetDistance > 0.0001f && targetDistance <= maxShootRange)
                            {
                                lastShotTick[playerId] = serverTick;
                                opponent.health = Math.Max(0, opponent.health - shotDamage);
                                players[opponentId] = opponent;
                                if (opponent.health == 0)
                                    player.killCount++;
                                Console.WriteLine($"[Chronos Server AI Shoot] AI player_{playerId} (intent={(MacroIntent)aiTick.intent}) HIT player_{opponentId} (targetHealth={opponent.health}, kills={player.killCount})");
                            }
                        }
                    }
                    else
                    {
                        while (pendingInputs[playerId].Count > 0)
                        {
                            int seq = pendingInputs[playerId].Keys.First();
                            MovementClientPacket input = pendingInputs[playerId][seq];
                            pendingInputs[playerId].Remove(seq);
                            player.x += input.deltaX * input.inputDuration * 10f;
                            player.y += input.deltaY * input.inputDuration * 10f;
                            player.lastProcessedInput = input.sequence;
                            nextInputSequence[playerId] = input.sequence + 1;
                        }
                    }

                    player.id = playerId;
                    player.serverTick = serverTick;
                    players[playerId] = player;
                    Console.WriteLine($"pl_{player.id}, x: {player.x} y: {player.y}");
                }
                
                await server.SendPlayerData(clients, players.Values.ToArray(), bannedClients);
                snapshotHistory.Enqueue(
                    new KeyValuePair<int, PlayerData[]>(
                        serverTick,
                        players.Values.ToArray()));
                while (snapshotHistory.Count > historyLength)
                    snapshotHistory.Dequeue();
                serverTick++;

            }
            Console.WriteLine("Tick Done");

            bool VerifyShot(
                int shooterId,
                ShootingCommand command,
                out int targetPlayerId)
            {
                ShootingClientPacket shot = command.shot;
                targetPlayerId = -1;
                if (shot.inputCount != 0 ||
                    command.inputs.Length != 0 ||
                    serverTick - shot.observedServerTick < 0 ||
                    serverTick - shot.observedServerTick > maxRewindTicks ||
                    serverTick - lastShotTick[shooterId] < shotCooldownTicks)
                    return false;

                if (!TryGetHistoricalSnapshot(
                        shot.observedServerTick,
                        shooterId,
                        out PlayerData historicalShooter))
                    return false;

                PlayerData currentShooter = players[shooterId];
                if (currentShooter.health <= 0 || historicalShooter.health <= 0)
                    return false;

                float shotDirX = shot.x;
                float shotDirY = shot.z;
                float shotDirMagnitude = MathF.Sqrt(shotDirX * shotDirX + shotDirY * shotDirY);
                if (shotDirMagnitude < 0.0001f)
                    return false;
                shotDirX /= shotDirMagnitude;
                shotDirY /= shotDirMagnitude;

                float closestDistance = maxShootRange;
                for (int candidateId = 0; candidateId < clientsConnected; candidateId++)
                {
                    if (candidateId == shooterId ||
                        !TryGetHistoricalSnapshot(
                            shot.observedServerTick,
                            candidateId,
                            out PlayerData historicalTarget) ||
                        historicalTarget.health <= 0 ||
                        players[candidateId].health <= 0)
                    {
                        continue;
                    }

                    float toTargetX = historicalTarget.x - historicalShooter.x;
                    float toTargetY = historicalTarget.y - historicalShooter.y;
                    float targetDistance = MathF.Sqrt(
                        toTargetX * toTargetX + toTargetY * toTargetY);
                    if (targetDistance < 0.0001f || targetDistance > maxShootRange)
                        continue;

                    float perpendicularDistance = MathF.Abs(
                        toTargetX * shotDirY - toTargetY * shotDirX);
                    if (perpendicularDistance <= maxHitDistanceFromRay &&
                        targetDistance < closestDistance)
                    {
                        closestDistance = targetDistance;
                        targetPlayerId = candidateId;
                    }
                }

                return targetPlayerId >= 0;
            }

            bool TryGetHistoricalSnapshot(
                int requestedTick,
                int playerId,
                out PlayerData player)
            {
                foreach (KeyValuePair<int, PlayerData[]> snapshot in snapshotHistory)
                {
                    if (snapshot.Key != requestedTick)
                        continue;

                    foreach (PlayerData candidate in snapshot.Value)
                    {
                        if (candidate.id == playerId)
                        {
                            player = candidate;
                            return true;
                        }
                    }
                }

                player = default;
                return false;
            }
            


        

        }
    }
}

public struct Received
{
    public IPEndPoint sender;
    public string message;
}
public struct ReceivedPlayerData
{
    public IPEndPoint sender;
    public object? data;
}

public class UDP_Server
{
    private UdpClient udpListener;

    public UDP_Server()
    {
        udpListener = new UdpClient(11_000);

    } 
    public string getIP(){return udpListener.Client.LocalEndPoint?.ToString() ?? "unknown";}
    public async Task<Received> Receive()
    {
        var result = await udpListener.ReceiveAsync();
        return new Received()
        {
            message= Encoding.UTF8.GetString(result.Buffer,0,result.Buffer.Length),
            sender = result.RemoteEndPoint
        };
    }
    public async Task<ReceivedPlayerData> ReceivePlayerData()
    {
        var result = await udpListener.ReceiveAsync();
        if (result.Buffer.Length < 1)
            return new ReceivedPlayerData { sender = result.RemoteEndPoint, data = null };

        int type = result.Buffer[0];
        object? packet = null;
        switch(type)
        {
            case 0:
                if (result.Buffer.Length == 1 + Marshal.SizeOf<MovementClientPacket>())
                    packet = DataPacketConvertor.fromBytes<MovementClientPacket>(result.Buffer,off:1);
            break;
            case 1:
                int headerSize = Marshal.SizeOf<ShootingClientPacket>();
                int inputSize = Marshal.SizeOf<MovementClientPacket>();
                if (result.Buffer.Length >= 1 + headerSize)
                {
                    ShootingClientPacket shot =
                        DataPacketConvertor.fromBytes<ShootingClientPacket>(
                            result.Buffer,
                            off: 1);
                    const int maxShotInputs = 64;
                    int remainingBytes = result.Buffer.Length - 1 - headerSize;
                    if (shot.inputCount >= 0 &&
                        shot.inputCount <= maxShotInputs &&
                        remainingBytes == shot.inputCount * inputSize)
                    {
                        MovementClientPacket[] inputs = new MovementClientPacket[shot.inputCount];
                        for (int i = 0; i < inputs.Length; i++)
                        {
                            inputs[i] = DataPacketConvertor.fromBytes<MovementClientPacket>(
                                result.Buffer,
                                i,
                                1 + headerSize);
                        }

                        packet = new ShootingCommand
                        {
                            shot = shot,
                            inputs = inputs
                        };
                    }
                }
            break;
            case 2:
                int chronosHeaderSize = Marshal.SizeOf<ChronosReconnectionHeader>();
                int ledgerTickSize = Marshal.SizeOf<TimelineLedgerTick>();
                if (result.Buffer.Length >= 1 + chronosHeaderSize)
                {
                    ChronosReconnectionHeader reconHeader =
                        DataPacketConvertor.fromBytes<ChronosReconnectionHeader>(
                            result.Buffer,
                            off: 1);
                    const int maxLedgerTicks = 600;
                    int remainingLedgerBytes = result.Buffer.Length - 1 - chronosHeaderSize;
                    if (reconHeader.tickCount >= 0 &&
                        reconHeader.tickCount <= maxLedgerTicks &&
                        remainingLedgerBytes == reconHeader.tickCount * ledgerTickSize)
                    {
                        TimelineLedgerTick[] ledger = new TimelineLedgerTick[reconHeader.tickCount];
                        for (int i = 0; i < ledger.Length; i++)
                        {
                            ledger[i] = DataPacketConvertor.fromBytes<TimelineLedgerTick>(
                                result.Buffer,
                                i,
                                1 + chronosHeaderSize);
                        }

                        packet = new ChronosReconnectionCommand
                        {
                            header = reconHeader,
                            ledger = ledger
                        };
                    }
                }
            break;
            case 3:
                if (result.Buffer.Length == 1 + Marshal.SizeOf<HeartbeatClientPacket>())
                    packet = DataPacketConvertor.fromBytes<HeartbeatClientPacket>(result.Buffer, off: 1);
            break;
        }
        return new ReceivedPlayerData()
        {
            sender = result.RemoteEndPoint,
            
            data = packet
        };
    }
    public async Task Send(string message,IPEndPoint client)
    {
        byte[]  bytes = Encoding.UTF8.GetBytes(message);
        try
        {
            await udpListener.SendAsync(bytes,bytes.Length,client);
        }
        catch (SocketException)
        {
            // Ignore transient send errors when a client socket is disconnected
        }
    }
    public async Task SendPlayerData(Dictionary<IPEndPoint,int> clients, PlayerData[] players, HashSet<IPEndPoint>? bannedClients = null)
    {
        byte[] bytes = DataPacketConvertor.getBytes<PlayerData>(players);
        foreach (IPEndPoint remoteIP in clients.Keys)
        {
            if (bannedClients != null && bannedClients.Contains(remoteIP))
                continue;
            try
            {
                await udpListener.SendAsync(bytes,bytes.Length,remoteIP);
            }
            catch (SocketException)
            {
                // Ignore transient send errors when a client socket is disconnected
            }
        }
    }


    
}
