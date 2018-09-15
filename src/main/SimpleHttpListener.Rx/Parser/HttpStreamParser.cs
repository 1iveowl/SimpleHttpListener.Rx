using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using HttpMachine;
using ISimpleHttpListener.Rx.Enum;
using SimpleHttpListener.Rx.Helper;
using SimpleHttpListener.Rx.Model;

namespace SimpleHttpListener.Rx.Parser
{
    internal class HttpStreamParser : IDisposable
    {
        private readonly HttpCombinedParser _parser;
        private readonly HttpParserDelegate _parserDelegate;
        private readonly ErrorCorrection[] _errorCorrections;


        private readonly byte[] _correctLast4BytesReversed = {0x0a, 0x0d, 0x0a, 0x0d};
        private readonly CircularBuffer<byte> _last4BytesCircularBuffer;

        private IDisposable _disposableParserCompletion;

        private bool IsDone { get; set; }

        internal bool HasParsingError { get; private set; }
        
        internal HttpStreamParser(HttpParserDelegate parserDelegate, params ErrorCorrection[] errorCorrections)
        {
            _errorCorrections = errorCorrections;
            _parserDelegate = parserDelegate;
            _parser = new HttpCombinedParser(parserDelegate);
            _last4BytesCircularBuffer = new CircularBuffer<byte>(4);
        }
        
        internal async Task<IHttpRequestResponse> ParseAsync(Stream stream, CancellationToken ct)
        {
            _disposableParserCompletion = _parserDelegate.ParserCompletionObservable
                .Subscribe(parserState =>
                {
                    switch (parserState)
                    {
                        case ParserState.Start:
                            break;
                        case ParserState.Parsing:
                            break;
                        case ParserState.Completed:
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
                    IsDone = true;
                });
            
            await Observable.While(
                    () => !HasParsingError && !IsDone,
                    Observable.FromAsync(() => ReadBytesAsync(stream, ct)))
                .Catch<byte[], SimpleHttpListenerException>(ex =>
                {
                    HasParsingError = true;
                    return Observable.Return(Enumerable.Empty<byte>().ToArray());
                })
                .Where(b => b != Enumerable.Empty<byte>().ToArray())
                .Where(bSegment => bSegment.Length > 0)
                .Select(b => new ArraySegment<byte>(b, 0, b.Length))
                .Select(bSegment => _parser.Execute(bSegment));

            _parser.Execute(default);

            _parserDelegate.RequestResponse.MajorVersion = _parser.MajorVersion;
            _parserDelegate.RequestResponse.MinorVersion = _parser.MinorVersion;
            _parserDelegate.RequestResponse.ShouldKeepAlive = _parser.ShouldKeepAlive;

            return _parserDelegate.RequestResponse;
        }

        private async Task<byte[]> ReadBytesAsync(Stream stream, CancellationToken ct)
        {
            if (_parserDelegate.RequestResponse.IsEndOfRequest || HasParsingError)
            {
                IsDone = true;
                return Enumerable.Empty<byte>().ToArray();
            }

            if (ct.IsCancellationRequested)
            {
                return Enumerable.Empty<byte>().ToArray();
            }

            var b = new byte[1];
            
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
                //Debug.WriteLine("Reading byte.");
                bytesRead = await stream.ReadAsync(b, 0, 1, ct).ConfigureAwait(false);
                //Debug.WriteLine("Done reading byte.");
            }
            catch (Exception ex)
            {
                HasParsingError = true;
                throw new SimpleHttpListenerException("Unable to read network stream.", ex);
            }

            if (bytesRead < b.Length)
            {
                IsDone = true;
            }

            return _errorCorrections.Contains(ErrorCorrection.HeaderCompletionError) 
                ? ResilientHeader(b) 
                : b;
        }


        // Sometimes the HTTP does not end with \r\n\r\n, in which case it is added here.
        private byte[] ResilientHeader(byte[] b)
        {
            if (!IsDone)
            {
                _last4BytesCircularBuffer.Enqueue(b[0]);
            }
            else
            {
                if (!_parserDelegate.IsHeaderDone)
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
            }

            return b;
        }

        public void Dispose()
        {
            _disposableParserCompletion?.Dispose();
            _parser?.Dispose();
        }
    }
}
