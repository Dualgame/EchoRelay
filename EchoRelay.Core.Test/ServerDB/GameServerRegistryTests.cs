using EchoRelay.Core.Server.Messages.ServerDB;
using EchoRelay.Core.Server.Services.ServerDB;

namespace EchoRelay.Core.Test.ServerDB
{
    /// <summary>
    /// Registry bookkeeping without a network: game servers are built with no peer, and a started session is simulated
    /// by setting SessionId and the registry key the way StartSessionInternal does.
    /// </summary>
    public class GameServerRegistryTests
    {
        internal static RegisteredGameServer NewServer(GameServerRegistry registry, ulong serverId)
            => new RegisteredGameServer(registry, null!, new ERGameServerRegistrationRequest { ServerId = serverId });

        internal static Guid FakeStart(GameServerRegistry registry, RegisteredGameServer g)
        {
            Guid sid = Guid.NewGuid();
            typeof(RegisteredGameServer).GetProperty(nameof(RegisteredGameServer.SessionId))!.SetValue(g, (Guid?)sid);
            registry.RegisteredGameServersBySessionId[sid] = g;
            return sid;
        }

        [Fact]
        public async Task EndSession_RemovesItsSessionId_KeepsAlias()
        {
            var reg = new GameServerRegistry();
            var g = reg.AddGameServer(NewServer(reg, 1));
            Guid sid = FakeStart(reg, g);
            Guid link = Guid.NewGuid();
            Assert.True(reg.AddSessionAlias(link, g.ServerId));
            Assert.Same(g, reg.GetGameServer(sid));

            await g.EndSession();

            Assert.Null(reg.GetGameServer(sid));          // a stale session id no longer routes
            Assert.Same(g, reg.GetGameServer(link));      // the alias does, across sessions
            FakeStart(reg, g);
            Assert.Same(g, reg.GetGameServer(link));
        }

        [Fact]
        public async Task EndSession_LeavesAKeyNowOwnedByAnotherServer()
        {
            var reg = new GameServerRegistry();
            var a = reg.AddGameServer(NewServer(reg, 1));
            var b = reg.AddGameServer(NewServer(reg, 2));
            Guid sid = FakeStart(reg, a);
            reg.RegisteredGameServersBySessionId[sid] = b;   // the key was reassigned elsewhere

            await a.EndSession();

            Assert.Same(b, reg.GetGameServer(sid));
        }

        [Fact]
        public void RemoveGameServer_RemovesItsSessionId()
        {
            var reg = new GameServerRegistry();
            var g = reg.AddGameServer(NewServer(reg, 1));
            Guid sid = FakeStart(reg, g);

            reg.RemoveGameServer(g);

            Assert.Null(reg.GetGameServer(sid));
            Assert.Null(reg.GetGameServer(1UL));
        }

        [Fact]
        public void AddSessionAlias_RefusesAnotherServersLiveSessionId()
        {
            var reg = new GameServerRegistry();
            var a = reg.AddGameServer(NewServer(reg, 1));
            var b = reg.AddGameServer(NewServer(reg, 2));
            Guid sid = FakeStart(reg, a);

            Assert.False(reg.AddSessionAlias(sid, b.ServerId));
            Assert.True(reg.AddSessionAlias(sid, a.ServerId));
            Assert.True(reg.RemoveSessionAlias(sid));
            Assert.False(reg.RemoveSessionAlias(sid));
        }
    }
}
