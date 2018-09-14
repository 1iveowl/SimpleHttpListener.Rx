using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using HttpMachine;
using SimpleHttpListener.Rx.Helper;
using SimpleHttpListener.Rx.Model;

namespace SimpleHttpListener.Rx.Parser
{
    internal class HttpStreamParser : IDisposable
    {
        private readonly HttpCombinedParser _parserHandler;
        private readonly HttpParserDelegate _requestHandler;

        private readonly byte[] _correctLast4BytesReversed = {0x0a, 0x0d, 0x0a, 0x0d};
        private readonly CircularBuffer<byte> _last4BytesCircularBuffer;

        private IDisposable _disposableParserCompletion;

        private bool IsDone { get; set; }

        internal bool HasParsingError { get; private set; }
        
        internal HttpStreamParser(HttpParserDelegate requestHandler)
        {
            _requestHandler = requestHandler;
            _parserHandler = new HttpCombinedParser(requestHandler);
            _last4BytesCircularBuffer = new CircularBuffer<byte>(4);
        }
        
        internal async Task<IHttpRequestResponse> ParseAsync(Stream stream, CancellationToken ct)
        {
            _disposableParserCompletion = _requestHandler.ParserCompletionObservable
                .Subscribe(parserState =>
                {
                    switch (parserState)
                    {
                        case ParserState.Start:
                            //HasParsingError = false;
                            break;
                        case ParserState.Parsing:
                            //HasParsingError = false;
                            break;
                        case ParserState.Completed:
                            //HasParsingError = false;
                            break;
                        case ParserState.Failed:
                            HasParsingError = true;
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
                    () => !HasParsingError && !IsDone,
                    Observable.FromAsync(() => ReadOneByteAtTheTimeAsync(stream, ct)))
                .Catch<byte[], SimpleHttpListenerException>(ex =>
                {
                    HasParsingError = true;
                    return Observable.Return(Enumerable.Empty<byte>().ToArray());
                })
                .Where(b => b != Enumerable.Empty<byte>().ToArray())
                .Where(bSegment => bSegment.Length > 0)
                .Do(b => _last4BytesCircularBuffer.Enqueue(b[0]))
                .Select(FixIncompleteHttpError)
                .Select(b => new ArraySegment<byte>(b, 0, b.Length))
                .Select(bSegment => _parserHandler.Execute(bSegment) <= 0);

            _parserHandler.Execute(default);

            _requestHandler.RequestResponse.MajorVersion = _parserHandler.MajorVersion;
            _requestHandler.RequestResponse.MinorVersion = _parserHandler.MinorVersion;
            _requestHandler.RequestResponse.ShouldKeepAlive = _parserHandler.ShouldKeepAlive;

            return _requestHandler.RequestResponse;
        }

        private async Task<byte[]> ReadOneByteAtTheTimeAsync(Stream stream, CancellationToken ct)
        {
            if (ct.IsCancellationRequested)
            {
                return Enumerable.Empty<byte>().ToArray();
            }

            var oneByteArray = new byte[1];
            
            if (stream == null)
            {
                throw new Exception("Read stream cannot be null.");
            }

            if (!stream.CanRead)
            {
                throw new Exception("Stream connection have been closed.");
            }

            var bytesRead = 0;

            try
            {
                bytesRead = await stream.ReadAsync(oneByteArray, 0, 1, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                HasParsingError = true;
                throw new SimpleHttpListenerException("Unable to read network stream.", ex);
            }

            if (bytesRead < oneByteArray.Length)
            {
                IsDone = true;
            }

            return oneByteArray;
        }


        // Sometimes the HTTP does not end with \r\n\r\n, in which case it is added here.
        private byte[] FixIncompleteHttpError(byte[] b)
        {
            if (IsDone)
            {
                var last4Byte = _last4BytesCircularBuffer.ToArray();

                if (last4Byte != _correctLast4BytesReversed)
                {
                    byte[] returnNewLine = { 0x0d, 0x0a };

                    var correctionList = new List<byte>();

                    if (last4Byte[0] != _correctLast4BytesReversed[0] || last4Byte[1] != _correctLast4BytesReversed[1])
                    {
                        correctionList.Add(returnNewLine[0]);
                        correctionList.Add(returnNewLine[1]);
                    }

                    if (last4Byte[2] != _correctLast4BytesReversed[2] || last4Byte[3] != _correctLast4BytesReversed[3])
                    {
                        correctionList.Add(returnNewLine[0]);
                        correctionList.Add(returnNewLine[1]);
                    }

                    if (correctionList.Any())
                    {
                        return correctionList.Concat(correctionList.Select(x => x)).ToArray();
                    }
                }
            }

            return b;
        }

        public void Dispose()
        {
            _disposableParserCompletion?.Dispose();
            _parserHandler?.Dispose();
        }
    }
}
