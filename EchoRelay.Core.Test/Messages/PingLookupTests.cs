using System.Net;
using EchoRelay.Core.Server.Messages.Matching;
using EchoRelay.Core.Server.Services.Matching;
using EchoRelay.Core.Utils;

namespace EchoRelay.Core.Test.Messages
{
    public class PingLookupTests
    {
        static LobbyPingResponse.EndpointPingResult R(string intl, string ext, uint ms)
            => new LobbyPingResponse.EndpointPingResult { InternalAddress = IPAddress.Parse(intl), ExternalAddress = IPAddress.Parse(ext), PingMilliseconds = ms };

        [Fact]
        public void DuplicateAddressPair_KeepsMinimum()
        {
            // Two game servers on one host: same (internal, external) pair, different ports; the response has no port.
            var lookup = MatchingService.BuildPingLookup(new[]
            {
                R("192.168.1.10", "203.0.113.5", 40),
                R("192.168.1.10", "203.0.113.5", 25),
                R("192.168.1.11", "203.0.113.5", 60),
            });

            Assert.Equal(2, lookup.Count);
            Assert.Equal(25u, lookup[(IPAddress.Parse("192.168.1.10").ToUInt32(), IPAddress.Parse("203.0.113.5").ToUInt32())]);
            Assert.Equal(60u, lookup[(IPAddress.Parse("192.168.1.11").ToUInt32(), IPAddress.Parse("203.0.113.5").ToUInt32())]);
        }
    }
}
