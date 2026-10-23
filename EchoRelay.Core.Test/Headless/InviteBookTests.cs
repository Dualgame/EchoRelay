using System.Net;
using Newtonsoft.Json.Linq;

namespace EchoRelay.Core.Test.Headless
{
    // EchoRelay.Headless InviteBook.cs: the persistent invite table (H-1), creator-only revoke with kick (H-2) and the
    // caller binding of a services user_id (H-4). No Server: a fake clock, a temp store, fake login peers.
    public class InviteBookTests : IDisposable
    {
        static readonly DateTime T0 = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);
        readonly string dir = Path.Combine(Path.GetTempPath(), "invitebook-" + Guid.NewGuid().ToString("N"));
        DateTime now = T0;
        string Store => Path.Combine(dir, "invites.json");
        InviteBook Book() => new(Store, () => now);

        static Invite Inv(string code, string creator, DateTime created, int expiresS, byte[]? pw = null, ulong? server = null) =>
            new(code, Guid.NewGuid(), creator, "echo_arena_private", "mpl_arena_a", "usw", 11, 22, 33,
                new EchoRelay.Core.Server.Messages.ServerDB.ERGameServerStartSession.SessionSettings { AppId = "1369078409873402" },
                8, pw, created, created.AddSeconds(expiresS)) { ServerId = server };

        public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { } }

        [Fact]
        public void StoreRoundTripKeepsEverythingButTheLease()
        {
            var a = Book();
            var pw = new byte[] { 1, 2, 3, 4 };
            var x = Inv("ABCDE", "OVR-ORG-1", T0, 3600, pw, server: 7);
            a.Add(x);
            a.Add(Inv("FGHJK", "OVR-ORG-2", T0, 600));
            Assert.True(File.Exists(Store));
            Assert.False(File.Exists(Store + ".tmp"));
            Assert.Null(JObject.Parse(File.ReadAllText(Store))["invites"]![0]!["ServerId"]);

            now = T0.AddSeconds(60);
            var b = Book();
            Assert.Equal(2, b.Load());
            var y = b.ByCode["ABCDE"];
            Assert.Equal(x.Link, y.Link);
            Assert.Equal(x.Creator, y.Creator);
            Assert.Equal(x.Expires, y.Expires);
            Assert.Equal(DateTimeKind.Utc, y.Expires.Kind);
            Assert.Equal(pw, y.PasswordHash);
            Assert.Equal((byte)8, y.PlayerLimit);
            Assert.Equal(22L, y.LevelSym);
            Assert.Equal("usw", y.Region);
            Assert.Equal("1369078409873402", y.Settings?.AppId);
            Assert.Null(y.ServerId);   // restored = unleased
        }

        [Fact]
        public void LoadDropsExpiredAndRemembersThemAsGone()
        {
            var a = Book();
            a.Add(Inv("LIVE2", "u", T0, 7200));
            a.Add(Inv("OLD22", "u", T0, 600));
            now = T0.AddSeconds(900);                    // OLD22 expired 5 min ago
            var b = Book();
            Assert.Equal(1, b.Load());
            Assert.True(b.ByCode.ContainsKey("LIVE2"));
            Assert.False(b.ByCode.ContainsKey("OLD22"));
            var e = Assert.Throws<ControlApi.HttpError>(() => b.Lookup("old22"));
            Assert.Equal(410, e.Code);
            // the expired one is gone from the file too
            Assert.DoesNotContain("\"OLD22\"", JObject.Parse(File.ReadAllText(Store))["invites"]!.ToString());
        }

        [Fact]
        public async Task RevokedCodeStays410AcrossRestartForAnHour()
        {
            var a = Book();
            a.Add(Inv("RVKD2", "OVR-ORG-1", T0, 3600));
            await a.Revoke("RVKD2", "OVR-ORG-1");
            var b = Book(); b.Load();
            Assert.Equal(410, Assert.Throws<ControlApi.HttpError>(() => b.Lookup("RVKD2")).Code);
            Assert.True(b.Taken("RVKD2"));
            now = T0.AddHours(2);
            var c = Book(); c.Load();
            Assert.Equal(404, Assert.Throws<ControlApi.HttpError>(() => c.Lookup("RVKD2")).Code);
        }

        [Fact]
        public void UnreadableStoreIsMovedAside()
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Store, "{not json");
            var b = Book();
            Assert.Equal(0, b.Load());
            Assert.True(File.Exists(Store + ".bad"));
        }

        [Fact]
        public void NoStorePathMeansMemoryOnly()
        {
            var b = new InviteBook(null, () => now);
            b.Add(Inv("MEMRY", "u", T0, 60));
            Assert.Equal(0, b.Load());
            Assert.False(Directory.Exists(dir));
        }

        [Fact]
        public async Task RevokeByCreatorKicksTheLeasedSession()
        {
            var b = Book();
            var kicked = new List<Invite>();
            var dropped = new List<string>();
            b.Kick = inv => { kicked.Add(inv); return Task.FromResult(3); };
            b.OnDropped = (inv, why) => dropped.Add($"{inv.Code} {why}");
            b.Add(Inv("KICK2", "OVR-ORG-1", T0, 3600, server: 42));
            var (inv, n) = await b.Revoke("kick2", "OVR-ORG-1");
            Assert.Equal("KICK2", inv.Code);
            Assert.Equal(3, n);
            Assert.Equal(42UL, Assert.Single(kicked).ServerId);
            Assert.Equal("KICK2 revoked", Assert.Single(dropped));
            Assert.False(b.ByCode.ContainsKey("KICK2"));
        }

        [Fact]
        public async Task RevokeByAnotherUserIs403AndKicksNobody()
        {
            var b = Book();
            int kicks = 0, drops = 0;
            b.Kick = _ => { kicks++; return Task.FromResult(1); };
            b.OnDropped = (_, _) => drops++;
            b.Add(Inv("MINE2", "OVR-ORG-1", T0, 3600, server: 42));
            var e = await Assert.ThrowsAsync<ControlApi.HttpError>(() => b.Revoke("MINE2", "OVR-ORG-2"));
            Assert.Equal(403, e.Code);
            Assert.Equal(0, kicks);
            Assert.Equal(0, drops);
            Assert.True(b.ByCode.ContainsKey("MINE2"));
            // the operator (user null) may revoke anyone's
            var (_, n) = await b.Revoke("MINE2", null);
            Assert.Equal(1, n);
        }

        [Fact]
        public async Task RevokeOfUnleasedInviteKicksNobody()
        {
            var b = Book();
            int kicks = 0;
            b.Kick = _ => { kicks++; return Task.FromResult(1); };
            b.Add(Inv("IDLE2", "OVR-ORG-1", T0, 3600));
            var (_, n) = await b.Revoke("IDLE2", "OVR-ORG-1");
            Assert.Equal(0, n);
            Assert.Equal(0, kicks);
        }

        [Fact]
        public void SweepExpiresAndLeaseIsRuntimeOnly()
        {
            var b = Book();
            b.Add(Inv("SWEP2", "u", T0, 30));
            Assert.Equal(9UL, b.SetLease("SWEP2", 9)!.ServerId);
            Assert.Null(JObject.Parse(File.ReadAllText(Store))["invites"]![0]!["ServerId"]);
            now = T0.AddSeconds(31);
            b.Sweep();
            Assert.Empty(b.ByCode);
            Assert.Equal("expired", b.Gone["SWEP2"].Why);
        }

        // --- H-4 caller binding ---
        static readonly IPAddress Pc = IPAddress.Parse("10.0.0.20"), Quest = IPAddress.Parse("10.0.0.31"),
                                  Other = IPAddress.Parse("10.0.0.99");
        static readonly (string?, IPAddress)[] Logins =
            { ("OVR-ORG-1", Pc), ("OVR-ORG-2", Quest), (null, Other) };   // a peer that has not logged in yet

        [Fact]
        public void AuthAcceptsTheLoginPeersOwnAddress()
        {
            Assert.Null(InviteAuth.Refusal("OVR-ORG-1", Pc, Logins));
            Assert.Null(InviteAuth.Refusal("OVR-ORG-2", Quest.MapToIPv6(), Logins));   // ::ffff:a.b.c.d == a.b.c.d
        }

        [Fact]
        public void AuthRefusesAForeignUserId()
        {
            // the caller is logged in itself (OVR-ORG-1 on Pc) but names someone else's id
            Assert.Equal("OVR-ORG-2 is not logged in from this address", InviteAuth.Refusal("OVR-ORG-2", Pc, Logins));
        }

        [Fact]
        public void AuthRefusesTheRightIdFromTheWrongAddress()
        {
            Assert.Equal("OVR-ORG-1 is not logged in from this address", InviteAuth.Refusal("OVR-ORG-1", Other, Logins));
        }

        [Fact]
        public void AuthRefusesALoggedOutId()
        {
            Assert.Equal("OVR-ORG-3 is not logged in", InviteAuth.Refusal("OVR-ORG-3", Pc, Logins));
            Assert.Equal("OVR-ORG-1 is not logged in", InviteAuth.Refusal("OVR-ORG-1", Pc, Array.Empty<(string?, IPAddress)>()));
        }

        [Fact]
        public void AuthLoopbackMatchesLoopbackAndAnySecondLogin()
        {
            var local = new (string?, IPAddress)[] { ("OVR-ORG-1", IPAddress.Loopback), ("OVR-ORG-1", Pc) };
            Assert.Null(InviteAuth.Refusal("OVR-ORG-1", IPAddress.IPv6Loopback, local));
            Assert.Null(InviteAuth.Refusal("OVR-ORG-1", Pc, local));
            Assert.NotNull(InviteAuth.Refusal("OVR-ORG-1", Other, local));
        }
    }
}
