using EchoRelay.Core.Utils;
using System.Net;

namespace EchoRelay.Core.Server.Messages.ServerDB
{
    /// <summary>
    /// A message from game server to server, requesting game server registration so clients can match with it.
    /// NOTE: This is an unofficial message created for Echo Relay.
    /// </summary>
    public class ERGameServerRegistrationRequest : Message
    {
        #region Fields
        /// <summary>
        /// The unique 64-bit symbol denoting the type of message.
        /// </summary>
        public override long MessageTypeSymbol => 0x7777777777777777;

        /// <summary>
        /// The identifier of the game server.
        /// </summary>
        public ulong ServerId;
        /// <summary>
        /// The internal/private IP address of the game server.
        /// </summary>
        public IPAddress InternalAddress;
        /// <summary>
        /// The UDP port that the game server is broadcasting on.
        /// </summary>
        public ushort Port;
        /// <summary>
        /// A symbol indicating the region of the server.
        /// </summary>
        public long RegionSymbol;
        /// <summary>
        /// The version of the server, prevents mismatches in matching.
        /// </summary>
        public long VersionLock;
        #endregion

        #region Constructor
        /// <summary>
        /// Initializes a new <see cref="ERGameServerRegistrationRequest"/> message.
        /// </summary>
        public ERGameServerRegistrationRequest()
        {
            InternalAddress = new IPAddress(0);
        }
        #endregion

        #region Functions
        /// <summary>
        /// Streams the message data in/out based on the streaming mode set.
        /// </summary>
        /// <param name="io">The stream to read/write data from/to.</param>
        public override void Stream(StreamIO io)
        {
            io.Stream(ref ServerId);
            io.Stream(ref InternalAddress, ByteOrder.BigEndian);
            io.Stream(ref Port);
            // EchoRelay.GameServer sends its naturally aligned C++ struct ERLobbyRegistrationRequest
            // {u64 serverId; u32 internalIp; u16 port; BYTE padding[4]; SymbolId regionId; SymbolId versionLock;}
            // (40 bytes), so regionId sits at +24, not +16. Reading at +16 put padding bytes in RegionSymbol and the
            // region (CSymbol64("default") 0x2fd38a995f615b9e when unset) in VersionLock.
            ushort pad2 = 0; // padding[0..1]
            io.Stream(ref pad2);
            ulong pad8 = 0; // padding[2..3] + 6 bytes of alignment before the u64 regionId
            io.Stream(ref pad8);
            io.Stream(ref RegionSymbol);
            io.Stream(ref VersionLock);
        }

        public override string ToString()
        {
            return $"{GetType().Name}(" +
                $"server_id={ServerId}, " +
                $"internal_ip={InternalAddress}, " +
                $"port={Port}, " +
                $"region={RegionSymbol}, " +
                $"version_lock={VersionLock}" +
                $")";
        }
        #endregion
    }
}
