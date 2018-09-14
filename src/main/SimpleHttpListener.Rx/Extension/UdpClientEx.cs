
using System;
using System.IO;
using System.Net.Sockets;
using System.Reactive.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;

namespace SimpleHttpListener.Rx.Extension
{
    public static class UdpClientEx
    {
        public static IObservable<UdpReceiveResult> ToObservable(this UdpClient udpClient, CancellationToken ct)
        {
            return Observable.While(
                () => !ct.IsCancellationRequested,
                Observable.FromAsync(x => udpClient.ReceiveAsync()));
        }

        public static void WriteToConsole(this MemoryStream stream)
        {
            var temporary = stream.Position;
            stream.Position = 0;

            using (var reader = new StreamReader(stream, Encoding.UTF8, false, 0x1000, true))
            {
                var text = reader.ReadToEnd();
                if (!string.IsNullOrEmpty(text))
                {
                    Console.WriteLine(text);
                }
            }

            stream.Position = temporary;
        }
    }
}
