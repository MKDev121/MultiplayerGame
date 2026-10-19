using System;
using System.Collections;
using System.Drawing;
using System.Runtime.InteropServices;
namespace MyExternalLibrary{

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct PositionPacket
{
    public int id;
   public float deltaX,deltaY; //position 
    
};

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct PlayerData
{
    public int id;
    public float x,y;
    public int health;

    
};

public static class DataPacketConvertor
{

    public static T fromBytes<T>(byte[] arr, int i=0)
    {
        
        T str = default;
        int size = Marshal.SizeOf(str);
        IntPtr ptr = Marshal.AllocHGlobal(size);

        Marshal.Copy(arr,i*size,ptr,size);
        
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
    
        try{
      
        byte[] byteDatas = new byte[Marshal.SizeOf(data[0])*data.Length];
        int i=0;
        foreach(T val in data)
        {
            int size = Marshal.SizeOf(val);
            IntPtr ptr = Marshal.AllocHGlobal(size);
            
            Marshal.StructureToPtr(val,ptr,true);
            Marshal.Copy(ptr,byteDatas,i*size,size);
            i++;
        }

        return byteDatas;
        }catch(Exception e)
        {
                Console.WriteLine(e);
                return null;
        }
    }
    public static byte[] getBytes<T>(T data)
    {
        int size = Marshal.SizeOf(data);
        byte[] byteStream = new byte[size];
        
        IntPtr ptr =Marshal.AllocHGlobal(size);
        Marshal.StructureToPtr(data,ptr,true);
        Marshal.Copy(ptr,byteStream,0,size);
        return byteStream;
    }
}
}