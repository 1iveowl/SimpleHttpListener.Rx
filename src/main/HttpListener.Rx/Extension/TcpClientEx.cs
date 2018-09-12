using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HttpListener.Rx.Extension
{
    public static class TcpClientEx
    {
        public static async Task ConnectTcpIPv4Async(this TcpClient tcpClient, Uri uri)
        {

          var ip = uri.Host.GetIPv4Address();

          await tcpClient.ConnectAsync(ip, uri.Port)
                .ToObservable()
                .Timeout(TimeSpan.FromSeconds(5));
        }

        public static async Task ConnectTcpIPv6Async(this TcpClient tcpClient, Uri uri)
        {
            var ip = uri.Host.GetIPv6Address();

            await tcpClient.ConnectAsync(ip, uri.Port)
                .ToObservable()
                .Timeout(TimeSpan.FromSeconds(5));
        }

        public static IObservable<byte[]> ToByteStreamObservable(this Stream networkStream, CancellationToken ct)
        {
            return CreateByteStreamObservable(networkStream, ct);

        }

        public static async Task SendDatagramAsync(this Stream stream, byte[] byteArray, CancellationToken ct)
        {
            await stream.WriteAsync(byteArray, 0, byteArray.Length, ct);
        }

        public static async Task SendStringAsync(this Stream stream, string text, CancellationToken ct)
        {
            var frame = Encoding.UTF8.GetBytes(text);
            await stream.WriteAsync(frame, 0, frame.Length, ct);
        }

        public static async Task SendStringLineAsync(this Stream stream, string text, CancellationToken ct)
        {
            var frame = Encoding.UTF8.GetBytes($"{text}\r\n");
            await stream.WriteAsync(frame, 0, frame.Length, ct);
        }

        private static IObservable<byte[]> CreateByteStreamObservable(Stream stream,  CancellationToken ct)
        {
            return Observable.While(
                () => !ct.IsCancellationRequested,
                Observable.FromAsync(() => ReadOneByteAtTheTimeAsync(stream, ct)));
        }

        private static IObservable<byte[]> CreateByteStreamObservable2(Stream stream, TcpClient tcpClient, CancellationToken ct)
        {
            var observableBytes = Observable.Create<byte[]>(obs =>
            {
                while (!ct.IsCancellationRequested)
                {
                    if (ct.IsCancellationRequested || !tcpClient.Connected)
                    {
                        obs.OnNext(Enumerable.Empty<byte>().ToArray());
                    }

                    var oneByteArray = new byte[1];

                    try
                    {
                        if (stream == null)
                        {
                            throw new Exception("Read stream cannot be null.");
                        }

                        if (!stream.CanRead)
                        {
                            throw new Exception("Stream connection have been closed.");
                        }

                        var bytesRead = stream.ReadByte();

;                        //var bytesRead = await stream.ReadAsync(oneByteArray, 0, 1, ct).ConfigureAwait(false);

                        if (bytesRead < oneByteArray.Length)
                        {
                            throw new Exception("Stream connection aborted unexpectedly. Check connection and socket security version/TLS version).");
                        }
                    }
                    catch (ObjectDisposedException)
                    {
                        Debug.WriteLine("Ignoring Object Disposed Exception - This is an expected exception.");
                        obs.OnNext(Enumerable.Empty<byte>().ToArray());
                    }
                    catch (IOException)
                    {
                        obs.OnNext(Enumerable.Empty<byte>().ToArray());
                    }

                    obs.OnNext(oneByteArray);
                }

                obs.OnCompleted();

                return Disposable.Empty;
            });

            return observableBytes.SubscribeOn(Scheduler.Default);
        }

        private static async Task<byte[]> ReadOneByteAtTheTimeAsync(Stream stream, CancellationToken ct)
        {
            if (ct.IsCancellationRequested)
            {
                return Enumerable.Empty<byte>().ToArray();
            }

            var oneByteArray = new byte[1];

            try
            {
                if (stream == null)
                {
                    throw new Exception("Read stream cannot be null.");
                }

                if (!stream.CanRead)
                {
                    throw new Exception("Stream connection have been closed.");
                }

                var bytesRead = await stream.ReadAsync(oneByteArray, 0, 1, ct).ConfigureAwait(false);

                if (bytesRead < oneByteArray.Length)
                {
                    throw new Exception("Stream connection aborted unexpectedly. Check connection and socket security version/TLS version).");
                }
            }
            catch (ObjectDisposedException)
            {
                Debug.WriteLine("Ignoring Object Disposed Exception - This is an expected exception.");
                return Enumerable.Empty<byte>().ToArray();
            }
            catch (IOException)
            {
                return Enumerable.Empty<byte>().ToArray();
            }

            return oneByteArray;
        }
    }
}
