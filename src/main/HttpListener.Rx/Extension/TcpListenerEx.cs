using System;
using System.Net.Sockets;
using System.Reactive.Linq;
using System.Threading;

namespace HttpListener.Rx.Extension
{
    static class TcpListenerEx
    {
        public static IObservable<TcpClient> ToObservable(this TcpListener tcpListener, CancellationToken ct)
        {
            return Observable.While(
                () => !ct.IsCancellationRequested,
                Observable.FromAsync(tcpListener.AcceptTcpClientAsync));
        }
    }
}
