using UnityEngine;
using System.Net;
using System.Net.Sockets;
using UnityEngine.UI;
using System.Text;
using System.Threading.Tasks;
using MyExternalLibrary;



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
    void Start()
    {
        
        remoteIP= new IPEndPoint(IPAddress.Loopback,11_000);
        client = new UdpClient();
        ConnectToServer();//exception handling may be needed
        Debug.Log("This is executed just after Connect To Server");


    }   
    void Update()
    {
        
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
                //Instantiate Players
                for(int i=0;i<2;i++)
                {
                    Instantiate(playerObject,spawnLocations[i].position,Quaternion.identity);
                }
                SendCNFMessage("ACK");
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
                            break;//asumming that server has sent player data
                    }
                }

                );
                break;
            }

        }
        //ListenToServer();
    }
    async Task ListenToServer()
    {
        while(true){
            var message = await client.ReceiveAsync();
            if(players==null)
            {
                
            }            

            Debug.Log(message);
    
        }
    }
}
