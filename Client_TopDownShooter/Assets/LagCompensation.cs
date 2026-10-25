using MyExternalLibrary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Playables;

public class LagCompensation : MonoBehaviour
{
    static readonly object stateLock = new object();
    static readonly List<PlayerData> previousState = new List<PlayerData>();
    static readonly List<PlayerData> currentState = new List<PlayerData>();//data of remote players which were received
    static readonly List<PlayerData> nextState = new List<PlayerData>();//data of remote players just received;

    static readonly float snapshotInterval = 1f / 60f;
    static float interpolationTime;
    public static bool canInterpolate=true;
    public static Queue<MovementClientPacket> inputHistory = new Queue<MovementClientPacket>();
    public static void LinearInterpolate(GameObject[] remotePlayers,int clientID)

    {
        canInterpolate=previousState.Count>0;

        if(canInterpolate)
        {
            lock (stateLock)
            {
                int playerCount = Mathf.Min(currentState.Count, previousState.Count);
                interpolationTime = Mathf.Min(interpolationTime + Time.fixedDeltaTime, snapshotInterval);
                float t = interpolationTime / snapshotInterval;
                for(int i=0;i<Mathf.Min(playerCount, remotePlayers.Length);i++)
                {
                    if(i!=clientID)
                    {
                        Vector3 currVec = new Vector3(previousState.ElementAt(i).x,remotePlayers[i].transform.position.y,previousState.ElementAt(i).y);
                        Vector3 nextVec = new Vector3(currentState.ElementAt(i).x,remotePlayers[i].transform.position.y,currentState.ElementAt(i).y);
                        remotePlayers[i].transform.position =Vector3.Lerp(currVec,nextVec,t);
                    }
                }
            }
        }
        else
        {
            lock (stateLock)
            {
                for(int i=0;i<Mathf.Min(currentState.Count, remotePlayers.Length);i++)
                {
                    Vector3 nextVec = new Vector3(currentState.ElementAt(i).x,remotePlayers[i].transform.position.y,currentState.ElementAt(i).y);
                    remotePlayers[i].transform.position = nextVec;
                }
            }
            
        }
    }
    public static void ClientSidePrediction(MovementClientPacket input,GameObject player)
    {
        Vector3 result = new Vector3(input.deltaX,0f,input.deltaY)*Time.deltaTime*10f;
        player.transform.position += result;
        inputHistory.Enqueue(input);

    }
    public static void ServerReconciliation(PlayerData serverData,GameObject player)
    {
        Vector3 serverUpdate= new Vector3(serverData.x,player.transform.position.y,serverData.y);
        if(inputHistory.Count>0)
        {
            while(inputHistory.TryPeek(out MovementClientPacket result))
            {
            
                if(result.sequence>serverData.lastProcessedInput)
                    break;
                else
                    inputHistory.Dequeue();
                
            }
            for(int i=0;i<inputHistory.Count;i++)
            {
                serverUpdate+=new Vector3(inputHistory.ElementAt(i).deltaX,0f,inputHistory.ElementAt(i).deltaY)*inputHistory.ElementAt(i).inputDuration*10f;
               
            }
            
        }
        player.transform.position=serverUpdate;

    }   

    public static void UpdateStates(ConcurrentQueue<PlayerData> playerDatas,ref PlayerData latestSnapShot,int clientID)
    {
        List<PlayerData> receivedState = new List<PlayerData>();
            while(playerDatas.TryDequeue(out PlayerData player))
            {
                if(player.id==clientID)
                    latestSnapShot=player;
             receivedState.Add(player);
            }

        if (receivedState.Count == 0)
            return;

        lock (stateLock)
        {
            interpolationTime = 0f;
            previousState.Clear();
            previousState.AddRange(currentState);
            currentState.Clear();
            currentState.AddRange(nextState);
            nextState.Clear();
            nextState.AddRange(receivedState);
            
        }
    }

}