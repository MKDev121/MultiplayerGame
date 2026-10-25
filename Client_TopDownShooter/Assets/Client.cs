using UnityEngine;
using System.Net;
using System.Net.Sockets;
using UnityEngine.UI;
using System.Text;
using System.Threading.Tasks;
using MyExternalLibrary;
using System;
using NUnit.Framework;
using System.Collections.Concurrent;
using System.ComponentModel.Design.Serialization;
using System.Linq;
using Unity.VisualScripting;



enum ConnectionState
{
    Connecting,
    Connected
}

public class Client : MonoBehaviour
{
    UdpClient client;
    public string remoteIPAddress;
    ConnectionState connectionState;
    public GameObject messageBox;
    public GameObject playerObject;
    public InputField inputField;
    public Transform[] spawnLocations;

    public ConcurrentQueue<PlayerData> players;
     IPEndPoint remoteIP;
    int clientID;
    bool connectedToServer =false;
    public GameObject[] playerObjects;
    bool moving;
    public Text interpolationStatus;    
    int tick =0;

    int inputSequence=1;
    public PlayerData latestPlayerSnapshot;
    void Start()
    {
        
        remoteIP= new IPEndPoint(IPAddress.Loopback,11_000);
        client = new UdpClient();
        //ConnectToServer();//exception handling may be needed
        Debug.Log("This is executed just after Connect To Server");


    }   
    void Update()
    {
        interpolationStatus.text="Interpolation: "+ LagCompensation.canInterpolate.ToString();
        if(Input.GetMouseButtonDown(0))
        {
            ShootingClientPacket packet = new ShootingClientPacket();
            packet.moving=moving;
            packet.x=transform.forward.x;
            packet.y=transform.forward.y;
            packet.z=transform.forward.z;
            

            byte[] data = DataPacketConvertor.getBytes(packet,1);
            client.Send(data,data.Length,remoteIP);
            inputSequence++;
        }

        if(connectedToServer)
        {
        
            float horizontal = Input.GetAxis("Horizontal");
            float vertical = Input.GetAxis("Vertical");
            if (Mathf.Abs(horizontal) > 0.01f || Mathf.Abs(vertical) > 0.01f)
            {
                MovementClientPacket packet = new MovementClientPacket();
                packet.deltaX=horizontal;
                packet.deltaY=vertical;
                packet.inputDuration=Time.deltaTime;
                packet.sequence=inputSequence;

                byte[] data = DataPacketConvertor.getBytes(packet,0);
                client.Send(data,data.Length,remoteIP);
                moving=true;

            
                
                LagCompensation.ClientSidePrediction(packet,playerObjects[clientID]);
                inputSequence++;
            }
            else
            {
                moving=false;
            }
        }
    }
    void FixedUpdate()
    {

        if (latestPlayerSnapshot.lastProcessedInput>0)
        {
            LagCompensation.ServerReconciliation(latestPlayerSnapshot,playerObjects[clientID]);
        }
        LagCompensation.LinearInterpolate(playerObjects,clientID);
        tick++;
    }
    public void SendCNFMessage(string message)
    {

            byte[] data = Encoding.UTF8.GetBytes(message);
            client.Send(data,data.Length,remoteIP);
    }
    public void JoinServer(string ip)
    {
        remoteIP.Address=IPAddress.Parse(ip);
        ConnectToServer();
    }
    async Task ConnectToServer()
    {
       
        Debug.Log(remoteIP.ToString());

        
        while(true)
        {
            string request = "REQ";
            byte[] data = Encoding.UTF8.GetBytes(request);
            client.Send(data,data.Length,remoteIP);
            var result = await client.ReceiveAsync();
            string acknowledgement =  Encoding.UTF8.GetString(result.Buffer,0,result.Buffer.Length);
            if(acknowledgement.Contains("ACK"))
            {
                clientID=acknowledgement[acknowledgement.Length-1]-'0';
                SendCNFMessage("CNF");

            }
            else if(acknowledgement.Contains("Start"))
            {
                connectionState=ConnectionState.Connected;
                messageBox.SetActive(true);
                playerObjects= new GameObject[2];
                players = new ConcurrentQueue<PlayerData>();
                
                //Instantiate Players
                for(int i=0;i<2;i++)
                {
                   GameObject obj= Instantiate(playerObject,spawnLocations[i].position,Quaternion.identity);
                   playerObjects[i]=obj;
                   playerObjects[i].GetComponentInChildren<Camera>().enabled=false;
                }
                GetComponent<Camera>().enabled=false;
                playerObjects[clientID].GetComponentInChildren<Camera>().enabled=true;
                SendCNFMessage("ACK");
                connectedToServer=true;
                Task confirmationTask= Task.Run(async () =>
                {
                    while(true)
                    {
                        var result= await client.ReceiveAsync();
                        string message = Encoding.UTF8.GetString(result.Buffer,0,result.Buffer.Length);
                        if(message.Contains("Start"))
                        {
                            SendCNFMessage("ACK");
                        }else if(!message.Contains("ACK"))
                            Debug.Log("Server has also started the game");
                            
                            break;//asumming that server has sent player data
                    }
                }

                );
                break;
            }

        }
        ListenToServer();

    }
    async Task ListenToServer()
    {
        while(true)
        {

            var message = await client.ReceiveAsync();
            for(int i=0;i<2;i++)
            {
                PlayerData pl=DataPacketConvertor.fromBytes<PlayerData>(message.Buffer,i);
                if(clientID==pl.id)
                    Debug.Log($"pl_{pl.id} x:{pl.x} y:{pl.y}");
                players.Enqueue(pl);
            }            
            
            LagCompensation.UpdateStates(players,ref latestPlayerSnapshot,clientID);

    
        }
    }
}
