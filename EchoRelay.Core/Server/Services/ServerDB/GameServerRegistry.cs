using EchoRelay.Core.Game;
using EchoRelay.Core.Server.Messages.ServerDB;
using EchoRelay.Core.Utils;
using System.Collections.Concurrent;
using static EchoRelay.Core.Server.Messages.ServerDB.ERGameServerStartSession;

namespace EchoRelay.Core.Server.Services.ServerDB
{
    public class GameServerRegistry
    {
        #region Properties
        public static readonly Guid ZeroGuid = new Guid("00000000-0000-0000-0000-000000000000");
        public ConcurrentDictionary<ulong, RegisteredGameServer> RegisteredGameServers { get; }
        public ConcurrentDictionary<Guid, RegisteredGameServer> RegisteredGameServersBySessionId { get; }
        /// <summary>
        /// Host-defined aliases (e.g. an invite or link GUID) that a join by id resolves to a game server, across that
        /// server's sessions and re-registrations under the same server id. Kept apart from the session ids so that ending a
        /// session can remove its id without touching an alias. See <see cref="AddSessionAlias"/>.
        /// </summary>
        public ConcurrentDictionary<Guid, SessionAlias> SessionAliases { get; }
        #endregion

        #region Types
        /// <summary>
        /// The session a join by alias starts on an idle game server (instead of one built from the join's own, usually
        /// empty, gametype and level).
        /// </summary>
        public sealed record SessionStartParameters(LobbyType LobbyType, Guid Channel, long? GameTypeSymbol, long? LevelSymbol,
            SessionSettings? Settings, int? PlayerLimit = null);
        /// <summary>
        /// An alias entry. <paramref name="IdleStart"/> is asked for start parameters when a join by this alias reaches the
        /// server while it is idle; null (or a null result) refuses that join.
        /// </summary>
        public sealed record SessionAlias(ulong ServerId, Func<SessionStartParameters?>? IdleStart);
        #endregion

        #region Events
        /// <summary>
        /// Event of a game server being registered/unregistered with the central server.
        /// </summary>
        /// <param name="gameServer">The <see cref="RegisteredGameServer"/> which was registered/unregistered with the central server.</param>
        public delegate void GameServerRegistrationChangedEventHandler(RegisteredGameServer gameServer);
        /// <summary>
        /// Event of a game server being registered.
        /// </summary>
        public event GameServerRegistrationChangedEventHandler? OnGameServerRegistered;
        /// <summary>
        /// Event of a game server being unregistered.
        /// </summary>
        public event GameServerRegistrationChangedEventHandler? OnGameServerUnregistered;
        #endregion

        #region Constructor
        public GameServerRegistry()
        {
            RegisteredGameServers = new ConcurrentDictionary<ulong, RegisteredGameServer>();
            RegisteredGameServersBySessionId = new ConcurrentDictionary<Guid, RegisteredGameServer>();
            SessionAliases = new ConcurrentDictionary<Guid, SessionAlias>();
        }
        #endregion

        #region Functions
        public RegisteredGameServer AddGameServer(RegisteredGameServer registeredGameServer)
        {
            // Add the game server to our lookup
            RegisteredGameServers[registeredGameServer.ServerId] = registeredGameServer;

            // Fire the relevant event for the game server being registered.
            OnGameServerRegistered?.Invoke(registeredGameServer);
            return registeredGameServer;
        }

        public RegisteredGameServer? GetGameServer(ulong serverId)
        {
            RegisteredGameServers.TryGetValue(serverId, out RegisteredGameServer? registeredGameServer);
            return registeredGameServer;
        }

        /// <summary>
        /// Resolves a session id, or else a session alias, to a game server.
        /// </summary>
        public RegisteredGameServer? GetGameServer(Guid sessionId)
        {
            if (RegisteredGameServersBySessionId.TryGetValue(sessionId, out RegisteredGameServer? registeredGameServer))
                return registeredGameServer;
            if (SessionAliases.TryGetValue(sessionId, out SessionAlias? alias))
                return GetGameServer(alias.ServerId);
            return null;
        }

        /// <summary>
        /// Adds or replaces an alias for a game server (by server id, so it follows a re-registration). Fails if the GUID
        /// is a live session id of another game server.
        /// </summary>
        /// <param name="idleStart">Start parameters for a join by this alias that finds the server idle; null refuses such joins.</param>
        public bool AddSessionAlias(Guid alias, ulong serverId, Func<SessionStartParameters?>? idleStart = null)
        {
            if (RegisteredGameServersBySessionId.TryGetValue(alias, out RegisteredGameServer? owner) && owner.ServerId != serverId)
                return false;
            SessionAliases[alias] = new SessionAlias(serverId, idleStart);
            return true;
        }
        public bool RemoveSessionAlias(Guid alias) => SessionAliases.TryRemove(alias, out _);
        public SessionAlias? GetSessionAlias(Guid alias) => SessionAliases.TryGetValue(alias, out SessionAlias? a) ? a : null;

        /// <summary>
        /// Removes the registry's session-id key for this game server's current session, if it still points at this object.
        /// </summary>
        internal void RemoveSessionKey(RegisteredGameServer registeredGameServer)
        {
            if (registeredGameServer.SessionId is Guid sessionId)
                RegisteredGameServersBySessionId.TryRemove(new KeyValuePair<Guid, RegisteredGameServer>(sessionId, registeredGameServer));
        }

        public void RemoveGameServer(RegisteredGameServer registeredGameServer)
        {
            RemoveGameServer(registeredGameServer.ServerId);
        }
        public void RemoveGameServer(ulong serverId)
        {
            // Try to remove any registered game server with this server identifier.
            RegisteredGameServers.Remove(serverId, out var unregisteredGameServer);

            // Fire the relevant event for the game server being registered.
            if (unregisteredGameServer != null)
            {
                // Its current session id no longer routes joins (a send to its dead peer would throw).
                RemoveSessionKey(unregisteredGameServer);
                OnGameServerUnregistered?.Invoke(unregisteredGameServer);
            }
        }

        public IEnumerable<RegisteredGameServer> FilterGameServers(int? findMax = null, ulong? serverId = null, Guid? sessionId = null,
            HashSet<(uint InternalAddr, uint ExternalAddr)>? addresses = null, ushort? port = null,
            long? gameTypeSymbol = null, long? levelSymbol = null, Guid? channel = null, bool? locked = null, LobbyType[]? lobbyTypes = null, TeamIndex? requestedTeam = null, bool unfilledServerOnly = true)
        {
            // Filter through all game servers
            List<RegisteredGameServer> filteredGameServers = new List<RegisteredGameServer>();
            foreach(RegisteredGameServer gameServer in RegisteredGameServers.Values)
            {
                // If we hit any set limit for game servers found, stop.
                if (findMax != null && filteredGameServers.Count >= findMax)
                    break;

                // Filter for each field supplied, skip to the next game server if any filter doesn't match.
                if (serverId != null && gameServer.ServerId != serverId)
                    continue;
                else if (addresses != null && !addresses.Contains((gameServer.InternalAddress.ToUInt32(), gameServer.ExternalAddress.ToUInt32())))
                    continue;
                else if (port != null && gameServer.Peer.Port != port)
                    continue;

                // If the session is started, filter on that criteria.
                if (gameServer.SessionStarted)
                {
                    if (gameTypeSymbol != null && gameTypeSymbol != gameServer.SessionGameTypeSymbol)
                        continue;
                    else if (levelSymbol != null && levelSymbol != gameServer.SessionLevelSymbol)
                        continue;
                    else if (channel != null && channel != ZeroGuid && gameServer.SessionChannel != ZeroGuid && channel != gameServer.SessionChannel) // zero guid means it is not a social lobby, etc, so we do not filter.
                        continue;
                    else if (locked != null && locked != gameServer.SessionLocked)
                        continue;
                    else if (lobbyTypes != null && !lobbyTypes.Contains(gameServer.SessionLobbyType))
                        continue;
                    else if (unfilledServerOnly && gameServer.SessionPlayerCount >= gameServer.SessionPlayerLimits.TotalPlayerLimit)
                        continue;
                    else if (unfilledServerOnly && requestedTeam != null && !gameServer.CheckTeamAvailability(requestedTeam.Value))
                        continue;
                }

                // The game server matched all filters, add it to the list
                filteredGameServers.Add(gameServer);
            }
            return filteredGameServers;
        }
        #endregion
    }
}
