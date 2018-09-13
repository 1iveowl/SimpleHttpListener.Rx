using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ISimpleHttpListener.Rx.Enum;
using ISimpleHttpListener.Rx.Model;

namespace SimpleHttpListener.Rx.Service
{
    public class HttpSender
    {
        public async Task SendResponseAsync(IHttpRequest request, IHttpResponse response)
        {
            if (request.RequestType == RequestType.TCP)
            {
                var bArray = ComposeResponse(request, response);
                try
                {
                    var writeStream = request?.ResponseStream;

                    if (writeStream?.CanWrite ?? false)
                    {
                        await writeStream.WriteAsync(bArray, 0, bArray.Length);
                        request.TcpClient.Close();
                    }
                }
                catch (Exception ex)
                {
                    throw ex;
                }
            }
        }

        private byte[] ComposeResponse(IHttpRequest request, IHttpResponse response)
        {
            var stringBuilder = new StringBuilder();

            stringBuilder.Append(
                $"HTTP/{request.MajorVersion}.{request.MinorVersion} {(int)response.StatusCode} {response.ResponseReason}\r\n");

            if (response.Headers != null)
            {
                if (response.Headers.Any())
                {
                    foreach (var header in response.Headers)
                    {
                        stringBuilder.Append($"{header.Key}: {header.Value}\r\n");
                    }
                }
            }

            if (response.Body?.Length > 0)
            {
                stringBuilder.Append($"Content-Length: {response?.Body?.Length}");
            }

            stringBuilder.Append("\r\n\r\n");

            var datagram = Encoding.UTF8.GetBytes(stringBuilder.ToString());


            if (response.Body?.Length > 0)
            {
                datagram = datagram.Concat(response?.Body?.ToArray()).ToArray();
            }

            Debug.WriteLine(Encoding.UTF8.GetString(datagram, 0, datagram.Length));
            return datagram;
        }
    }
}
