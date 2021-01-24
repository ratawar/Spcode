﻿using System;
using System.IO;
using System.IO.Pipes;
using System.Text;

namespace SPCode.Interop
{
    public static class PipeInteropClient
    {
        public static void ConnectToMasterPipeAndSendData(string data)
        {
            byte[] stringData = Encoding.UTF8.GetBytes(data);
            int stringLength = stringData.Length;
            byte[] array = new byte[sizeof(int) + stringLength];
            using (MemoryStream stream = new MemoryStream(array))
            {
                byte[] stringLengthData = BitConverter.GetBytes(stringLength);
                stream.Write(stringLengthData, 0, stringLengthData.Length);
                stream.Write(stringData, 0, stringData.Length);
            }
            using NamedPipeClientStream pipeClient = new NamedPipeClientStream(".", "SPCodeNamedPipeServer", PipeDirection.Out, PipeOptions.Asynchronous);
            pipeClient.Connect(5000);
            pipeClient.Write(array, 0, array.Length);
            pipeClient.Flush();
        }
    }
}
