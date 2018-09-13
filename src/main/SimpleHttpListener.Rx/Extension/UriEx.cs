using System.Linq;
using System.Net;

namespace SimpleHttpListener.Rx.Extension
{
    public static class UriEx
    {

        public static IPAddress GetIPv4Address(this string ipAddress)
        {
            if (IPAddress.TryParse(ipAddress, out var address))
            {
                return address.MapToIPv4();
            }
            else
            {
                return Dns.GetHostAddresses(ipAddress).FirstOrDefault()?.MapToIPv4();
            }
            
        }

        public static IPAddress GetIPv6Address(this string ipAddress)
        {
            if (IPAddress.TryParse(ipAddress, out var address))
            {
                return address.MapToIPv6();
            }
            else
            {
                return Dns.GetHostAddresses(ipAddress).FirstOrDefault()?.MapToIPv6();
            }

        }

    }
}
