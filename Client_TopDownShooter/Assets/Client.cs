using UnityEngine;
using System.Net;
using System.Net.Sockets;
using UnityEngine.UI;
using System.Text;
using System.Threading.Tasks;
using MyExternalLibrary;
using System;
using NUnit.Framework;



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

    public PlayerData[] players;
     IPEndPoint remoteIP;
    int clientID;
    bool connectedToServer =false;
    public GameObject[] playerObjects;
    void Start()
    {
        
        remoteIP= new IPEndPoint(IPAddress.Loopback,11_000);
        client = new UdpClient();
        ConnectToServer();//exception handling may be needed
        Debug.Log("This is executed just after Connect To Server");


    }   
    void Update()
    {
        if(connectedToServer){
        
        float horizontal = Input.GetAxis("Horizontal");
        float vertical = Input.GetAxis("Vertical");
        if (Mathf.Abs(horizontal) > 0.01f || Mathf.Abs(vertical) > 0.01f)
        {
            PositionPacket positionPacket = new PositionPacket();
            positionPacket.deltaX=horizontal;
            positionPacket.deltaY=vertical;
            positionPacket.id=clientID;
            byte[] data = DataPacketConvertor.getBytes(positionPacket);
            client.Send(data,data.Length,remoteIP);
        }
        }

    }
    void FixedUpdate()
    {
        try{
        if(playerObjects.Length>0&&players!=null){
            for(int i=0;i<2;i++)
            {
                playerObjects[players[i].id].transform.position=new Vector3(players[i].x,playerObjects[i].transform.position.y,players[i].y); 
                
            }
        players=null;
        }
        }catch(Exception e)
        {
            Debug.Log(e);
            Debug.Log(e.Source);  
        }
    }
    public void SendCNFMessage(string message)
    {

            byte[] data = Encoding.UTF8.GetBytes(message);
            client.Send(data,data.Length,remoteIP);
    }

    async Task ConnectToServer()
    {
       
        Debug.Log(remoteIP.Address);

        
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

            }else if(acknowledgement.Contains("Start"))
            {
                connectionState=ConnectionState.Connected;
                messageBox.SetActive(true);
                playerObjects= new GameObject[2];
                //Instantiate Players
                for(int i=0;i<2;i++)
                {
                   GameObject obj= Instantiate(playerObject,spawnLocations[i].position,Quaternion.identity);
                   playerObjects[i]=obj;
                }
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
        while(true){
            var message = await client.ReceiveAsync();
            if(players==null)
            {
                players = new PlayerData[2];
            }
            for(int i=0;i<players.Length;i++)
            {
                PlayerData pl=DataPacketConvertor.fromBytes<PlayerData>(message.Buffer,i);
                if(clientID==pl.id)
                    Debug.Log($"pl_{pl.id} x:{pl.x} y:{pl.y}");
                players[pl.id]=pl;
            }            

          
    
        }
    }
}
