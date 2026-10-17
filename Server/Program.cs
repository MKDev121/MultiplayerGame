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

internal class Program
{
    private static async Task Main(string[] args)
    {
        int port = 11_000;
        int maxClients = 2;
        int clientsConnected = 0;
        int confirmedClients = 0;
        bool confirmationProcessStarted=false;
        var cts = new CancellationTokenSource();
        Dictionary<IPEndPoint,int> clients = new Dictionary<IPEndPoint, int>();
        Dictionary<IPEndPoint, PlayerData> players = new Dictionary<IPEndPoint, PlayerData>();

        Dictionary<IPEndPoint, List<PositionPacket>> playerDataBuffers = new Dictionary<IPEndPoint, List<PositionPacket>>();
        UDP_Server server = new();
        //PeriodicTimer timer = new PeriodicTimer(TimeSpan.FromMilliseconds(16.67f));
        while (true)
        {
            Received received = await server.Receive();
            if (!clients.ContainsKey(received.sender))// Connection Logic
            {
                Console.WriteLine("hi");
                if (received.message.Contains("REQ")&&clientsConnected<maxClients)// A client wants to connect
                {
                    
                    server.Send("ACK_"+clientsConnected.ToString(), received.sender);
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
                                        server.Send("ACK_"+clients[client].ToString(),client);
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
                    server.Send("Start",client);
                }
                StartTimer();
                while(true)
                {
                    Received result=await server.Receive();
                    if(result.message.Contains("ACK"))
                    {
                        Interlocked.Increment(ref confirmedClients);
                    }
                    if(Volatile.Read(ref confirmedClients)==clientsConnected){
                        cts.Cancel();
                        break;
                    }
                    
                }
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
                            server.Send("Start",client);
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

            // Task listeningTask = Task.Run(async () =>
            // {
            //     while (true)
            //     {
            //         ReceivedPlayerData received = await server.ReceivePlayerData();
            //         if (clients.ContainsKey(received.sender))
            //         {
            //             clients[received.sender].Add(received.data);
            //         }
            //     }
            // }
            // );

            // Task computingTask = Task.Run(async () =>
            // {
            //     //do computation of buffer values of clients at 60 tick rate
            //     var timer = new PeriodicTimer(TimeSpan.FromSeconds(1 / 60f));
            //     while (await timer.WaitForNextTickAsync())
            //     {
            //         //logic 
            //         foreach (IPEndPoint ipEndPoint in players.Keys)
            //         {

            //             PlayerData player = new PlayerData();
            //             foreach (PositionPacket data in clients[ipEndPoint])
            //             {
            //                 player.x += data.deltaX;
            //                 player.y += data.deltaY;

            //             }
            //             clients[ipEndPoint].Clear();
            //             players[ipEndPoint] = player;
            //         }
            //         server.SendPlayerData(players);

            //     }
            // }
            // );

            // await Task.WhenAll(listeningTask, computingTask);

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
    public PositionPacket data;
}

public class UDP_Server
{
    private UdpClient udpListener;

    public UDP_Server()
    {
        udpListener = new UdpClient(11_000);

    } 
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
        return new ReceivedPlayerData()
        {
            sender = result.RemoteEndPoint,
            data = DataPacketConvertor.fromBytes<PositionPacket>(result.Buffer)
        };
    }
    public void Send(string message,IPEndPoint client)
    {
        byte[]  bytes = Encoding.UTF8.GetBytes(message);
        udpListener.SendAsync(bytes,bytes.Length,client);

    }
    public void SendPlayerData(Dictionary<IPEndPoint,PlayerData> players)
    {
        byte[] bytes = DataPacketConvertor.getBytes<PlayerData>(players.Values.ToArray());
        foreach (IPEndPoint remoteIP in players.Keys)
        {
            udpListener.SendAsync(bytes,bytes.Length,remoteIP);
        }
    }


    
}





