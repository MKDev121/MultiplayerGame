using System;
using System.Collections;
using System.Drawing;
using System.Runtime.InteropServices;
namespace MyExternalLibrary{
public struct PositionPacket
{
   public float deltaX,deltaY; //position 
    
};

public struct PlayerData
{
    public float x,y;
    public int health;

    
};

public static class DataPacketConvertor
{

    public static T fromBytes<T>(byte[] arr)
    {
        
        T str = default;
        int size = Marshal.SizeOf(str);
        IntPtr ptr = Marshal.AllocHGlobal(size);

        Marshal.Copy(arr,0,ptr,size);
        
        str = (T)Marshal.PtrToStructure(ptr,str.GetType());
        Marshal.FreeHGlobal(ptr);
        return str;
        
    }
    public static T[] getAllPlayersData<T>(byte[] arr)
    {
        
        int size = Marshal.SizeOf(arr);
        
        T val = default;
        T[] values = new T[size/Marshal.SizeOf(val)];
        for(int i=0;i<values.Length;i++)
        {
        
            IntPtr ptr=Marshal.AllocHGlobal(Marshal.SizeOf(val));
            Marshal.Copy(arr,i*Marshal.SizeOf(val),ptr,Marshal.SizeOf(val));
            values[i]=(T)Marshal.PtrToStructure(ptr,val.GetType());

        }
        
        return values;
    }
    public static byte[] getBytes<T>(T[] data)
    {
    
        byte[] byteDatas = new byte[Marshal.SizeOf(data)];
        int i=0;
        foreach(T val in data)
        {
            int size = Marshal.SizeOf(val);
            IntPtr ptr = Marshal.AllocHGlobal(size);
            
            Marshal.StructureToPtr(val,ptr,true);
            Marshal.Copy(ptr,byteDatas,i*size,size);
        }

        return byteDatas;
    }
}
}