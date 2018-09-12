using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using HttpListener.Rx.Model;
using HttpMachine;

namespace HttpListener.Rx.Parser
{
    internal class HttpStreamParser : IDisposable
    {
        private readonly HttpCombinedParser _parserHandler;
        private readonly HttpParserDelegate _requestHandler;

        private IDisposable _disposableParserCompletion;

        internal HttpStreamParser(HttpParserDelegate requestHandler)
        {
            _requestHandler = requestHandler;
            _parserHandler = new HttpCombinedParser(requestHandler);
        }
        
        internal async Task<IHttpRequestResponse> ParseAsync(Stream stream, CancellationToken ct)
        {
            var isParsing = true;

            _disposableParserCompletion = _requestHandler.ParserCompletionObservable
                .Subscribe(parserState =>
                {
                    switch (parserState)
                    {
                        case ParserState.Start:
                            break;
                        case ParserState.Parsing:
                            break;
                        case ParserState.Completed:
                            isParsing = false;
                            break;
                        case ParserState.Failed:
                            isParsing = false;
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(parserState), parserState, null);
                    }
                },
                ex => throw ex,
                () =>
                {

                });

            await Observable.While(
                    () => isParsing,
                    Observable.FromAsync(() => ReadOneByteAtTheTimeAsync(stream, ct)))
                .Select(bArray => _parserHandler.Execute(new ArraySegment<byte>(bArray, 0, bArray.Length)) <= 0);

            _parserHandler.Execute(default);

            _requestHandler.RequestResponse.MajorVersion = _parserHandler.MajorVersion;
            _requestHandler.RequestResponse.MinorVersion = _parserHandler.MinorVersion;
            _requestHandler.RequestResponse.ShouldKeepAlive = _parserHandler.ShouldKeepAlive;


            return _requestHandler.RequestResponse;
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

        public void Dispose()
        {
            _disposableParserCompletion?.Dispose();
            _parserHandler?.Dispose();
        }
    }
}
