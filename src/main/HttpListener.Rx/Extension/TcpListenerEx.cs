using System;
using System.Diagnostics;
using System.Net.Sockets;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HttpListener.Rx.Extension
{
    static class TcpListenerEx
    {
        public static IObservable<TcpClient> ToObservable(this TcpListener tcpListener, CancellationToken ct)
        {
            tcpListener.Start();

            return Observable.While(
                () => !ct.IsCancellationRequested,
                Observable.FromAsync(x => WaitForNextRequestAsync(tcpListener)));
        }

        private static async Task<TcpClient> WaitForNextRequestAsync(TcpListener tcpListener)
        {
            Debug.WriteLine("Ready for next Tcp Connection.");

            return await tcpListener.AcceptTcpClientAsync();
        }
    }
}
