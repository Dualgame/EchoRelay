using EchoRelay.Core.Game;
using EchoRelay.Core.Server.Services;
using EchoRelay.Core.Server.Services.ServerDB;
using static EchoRelay.Core.Server.Messages.ServerDB.ERGameServerStartSession;
using static EchoRelay.Core.Test.ServerDB.GameServerRegistryTests;

namespace EchoRelay.Core.Test.ServerDB
{
    public class JoinByIdTests
    {
        static readonly GameServerRegistry.SessionStartParameters InviteStart =
            new(LobbyType.Private, Guid.Empty, 0x1234, 0x5678, null, 8);

        static void FillPlayers(RegisteredGameServer g, int count)
        {
            var field = typeof(RegisteredGameServer).GetField("_playerSessions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var players = (Dictionary<Guid, (Peer peer, TeamIndex requestedTeam)>)field.GetValue(g)!;
            for (int i = 0; i < count; i++)
                players[Guid.NewGuid()] = (null!, TeamIndex.Any);
        }

        [Fact]
        public async Task StaleSessionId_IsRefused()
        {
            var reg = new GameServerRegistry();
            var g = reg.AddGameServer(NewServer(reg, 1));
            Guid oldSid = FakeStart(reg, g);
            await g.EndSession();
            FakeStart(reg, g);   // the server's next session

            Assert.Equal(RegisteredGameServer.JoinByIdCheck.Stale, g.CheckJoinById(oldSid).Check);
        }

        [Fact]
        public void IdleServer_OwnIdOrPlainAlias_IsRefused_InviteAlias_StartsItsSession()
        {
            var reg = new GameServerRegistry();
            var g = reg.AddGameServer(NewServer(reg, 1));
            Guid link = Guid.NewGuid(), invite = Guid.NewGuid();
            reg.AddSessionAlias(link, 1);
            reg.AddSessionAlias(invite, 1, () => InviteStart);

            Assert.Equal(RegisteredGameServer.JoinByIdCheck.IdleNoStart, g.CheckJoinById(link).Check);
            var (check, start) = g.CheckJoinById(invite);
            Assert.Equal(RegisteredGameServer.JoinByIdCheck.StartIdle, check);
            Assert.Same(InviteStart, start);
        }

        [Fact]
        public void AliasOfAnotherServer_IsStale()
        {
            var reg = new GameServerRegistry();
            var a = reg.AddGameServer(NewServer(reg, 1));
            reg.AddGameServer(NewServer(reg, 2));
            Guid link = Guid.NewGuid();
            reg.AddSessionAlias(link, 2, () => InviteStart);

            Assert.Equal(RegisteredGameServer.JoinByIdCheck.Stale, a.CheckJoinById(link).Check);
        }

        [Fact]
        public void FullLobby_IsRefused_PrivateAndLockedAreNot()
        {
            var reg = new GameServerRegistry();
            var g = reg.AddGameServer(NewServer(reg, 1));
            Guid sid = FakeStart(reg, g);
            g.SessionPlayerLimits = new GameTypePlayerLimits.PlayerLimits(2);
            g.SetLockedStatus(true);   // the 0-player pseudo-lock is ignored

            Assert.Equal(RegisteredGameServer.JoinByIdCheck.Accept, g.CheckJoinById(sid).Check);
            FillPlayers(g, 2);
            Assert.Equal(RegisteredGameServer.JoinByIdCheck.Full, g.CheckJoinById(sid).Check);
        }
    }
}
