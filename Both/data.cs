using System;
using System.Runtime.InteropServices;
namespace MyExternalLibrary{




[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct MovementClientPacket
{
  
    public int sequence;
    public float deltaX,deltaY; //position 
    public float inputDuration;
};

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct HeartbeatClientPacket
{
    public int clientTick;
};

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct ShootingClientPacket
{
    public int targetPlayerId;
    public int observedServerTick;
    public int baseInputSequence;
    public int inputCount;
    public float x,y,z; // gun direction        
};

public sealed class ShootingCommand
{
    public ShootingClientPacket shot;
    public MovementClientPacket[] inputs = Array.Empty<MovementClientPacket>();
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct PlayerData
{
    public int id;
    public int lastProcessedInput;
    public int serverTick;
    public float x,y;
    public int health;
    public int killCount;
};

public static class DataPacketConvertor
{

    public static T fromBytes<T>(byte[] arr, int i=0,int off=0) where T : struct
    {
        int size = Marshal.SizeOf<T>();
        int sourceOffset = checked(off + i * size);
        if (sourceOffset < 0 || sourceOffset > arr.Length - size)
            throw new ArgumentException("The packet does not contain a complete value.", nameof(arr));

        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(arr, sourceOffset, ptr, size);
            return Marshal.PtrToStructure<T>(ptr);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
        
    }
    public static T[] getAllPlayersData<T>(byte[] arr) where T : struct
    {
        int valueSize = Marshal.SizeOf<T>();
        if (arr.Length % valueSize != 0)
            throw new ArgumentException("The packet length is not aligned to the value size.", nameof(arr));

        T[] values = new T[arr.Length / valueSize];
        for(int i=0;i<values.Length;i++)
            values[i] = fromBytes<T>(arr, i);

        return values;
    }
    public static byte[] getBytes<T>(T[] data) where T : struct
    {
    
            if (data == null || data.Length == 0)
                throw new ArgumentException("At least one value is required.", nameof(data));

            byte[] byteDatas = new byte[Marshal.SizeOf<T>()*data.Length];
            int i=0;
            foreach(T val in data)
            {
                int size = Marshal.SizeOf<T>();
                IntPtr ptr = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(val,ptr,true);
                    Marshal.Copy(ptr,byteDatas,i*size,size);
                    i++;
                }
                finally
                {
                    Marshal.FreeHGlobal(ptr);
                }
        }

            return byteDatas;
    }
    public static byte[] getBytes<T>(T data,int type=0) where T : struct
    {
        int size = Marshal.SizeOf<T>();
        byte[] byteStream = new byte[size+1];
        byteStream[0]=(byte)type;
        IntPtr ptr =Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(data,ptr,true);
            Marshal.Copy(ptr,byteStream,1,size);
            return byteStream;
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    public static byte[] getChronosReconnectionBytes(ChronosReconnectionHeader header, TimelineLedgerTick[] ledger, int type = 2)
    {
        if (ledger == null)
            ledger = Array.Empty<TimelineLedgerTick>();

        header.tickCount = ledger.Length;
        int headerSize = Marshal.SizeOf<ChronosReconnectionHeader>();
        int tickSize = Marshal.SizeOf<TimelineLedgerTick>();
        byte[] byteStream = new byte[1 + headerSize + tickSize * ledger.Length];
        byteStream[0] = (byte)type;

        IntPtr headerPtr = Marshal.AllocHGlobal(headerSize);
        try
        {
            Marshal.StructureToPtr(header, headerPtr, true);
            Marshal.Copy(headerPtr, byteStream, 1, headerSize);
        }
        finally
        {
            Marshal.FreeHGlobal(headerPtr);
        }

        if (ledger.Length > 0)
        {
            byte[] ledgerBytes = getBytes(ledger);
            Buffer.BlockCopy(ledgerBytes, 0, byteStream, 1 + headerSize, ledgerBytes.Length);
        }

        return byteStream;
    }
}
}