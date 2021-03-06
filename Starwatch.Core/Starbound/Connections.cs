/*
START LICENSE DISCLAIMER
Starwatch is a Starbound Server manager with player management, crash recovery and a REST and websocket (live) API. 
Copyright(C) 2020 Lachee

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU Affero General Public License as published
by the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
GNU Affero General Public License for more details.

You should have received a copy of the GNU Affero General Public License
along with this program. If not, see < https://www.gnu.org/licenses/ >.
END LICENSE DISCLAIMER
*/
using Starwatch.Entities;
using Starwatch.Logging;
using Starwatch.Util;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Collections.Concurrent;

namespace Starwatch.Starbound
{

    /// <summary>
    /// Handles a list of connections
    /// </summary>
    public class Connections : Monitoring.ConfigurableMonitor
    {
        public static readonly bool ENFORCE_STRICT_NAMES = true;
        private static readonly Regex regexLoggedMsg = new Regex(@"'(.*)' as player '(.*)' from address ([a-zA-Z0-9.:]*)", RegexOptions.Compiled);
        private static readonly Regex regexClientMsg = new Regex(@"'(.*)' <(\d+)> \(([a-zA-Z0-9.:]*)\) (connected|disconnected)( for reason: (.*))?", RegexOptions.Compiled);

        private SemaphoreSlim _playerSemaphore;

        public override int Priority => 0;

        public bool KickUnnoticedConnections => Configuration.GetBool("kick_unnoticed", false);

        /// <summary>
        /// The last ID to conenct
        /// </summary>
        public int LatestConnectedID { get; private set; }

        /// <summary>
        /// The last player to connect.
        /// </summary>
        public Player LastestPlayer => GetPlayer(LatestConnectedID);

        /// <summary>
        /// The count of connections on the server
        /// </summary>
        public int Count => _connections.Count;

        /// <summary>
        /// Gets a connection at the index, otherwise returns nunll.
        /// </summary>
        /// <param name="connection"></param>
        /// <returns></returns>
        public Player this[int connection] => GetPlayer(connection);

        /// <summary>
        /// The IPv4 Validator
        /// </summary>
        public VPNValidator Validator { get; }

        private ConcurrentDictionary<int, Player> _connections = new ConcurrentDictionary<int, Player>();
        private Dictionary<int, Session> _sessions = new Dictionary<int, Session>();
        private List<PendingPlayer> _pending = new List<PendingPlayer>();
        private struct PendingPlayer
        {
            public string account;
            public string character;
            public string address;
        }


        public delegate void OnPlayerUpdateEvent(Player player);
        public delegate void OnPlayerDisconnectEvent(Player player, string reason);

        public event OnPlayerUpdateEvent OnPlayerUpdate;
        public event OnPlayerUpdateEvent OnPlayerConnect;
        public event OnPlayerDisconnectEvent OnPlayerDisconnect;

        private System.Timers.Timer _uuidTimer;
        
        public Connections(Server server) : base (server, "Connection")
        {
            try
            {
                string filepath = Configuration.GetString("vpn_ipv4", "Resources/vpn-ipv4.txt");
                Configuration.Save();

                Validator = new VPNValidator();
                Validator.Load(filepath);
            }
            catch(Exception e)
            {
                Logger.LogError(e, "Failed to create VPN Validator: {0}");
                Validator = null;
            }

            _playerSemaphore = new SemaphoreSlim(1, 1);
        }
        public override void Dispose()
        {
            _uuidTimer.Dispose();
            _playerSemaphore.Dispose();
        }

        #region Events
        public override async Task<bool> HandleMessage(Message msg)
        {            
            //Only nicks are within chat messages so we should abort if it is.
            if (msg.Level == Message.LogLevel.Chat)
            {
                //Nickname has changed?
                if (msg.Content.StartsWith("/nick"))
                {
                    string content = msg.Content.Trim();
                    string nickname = content.Length <= 6 ? " " : msg.Content.Substring(6);
                    Player player = _connections.Values.Where(p => p.Username.Equals(msg.Author)).First();
                    if (player != null)
                    {
                        Logger.Log("Nickname set for " + player + " to " + nickname);
                        _connections[player.Connection].Nickname = nickname;

                        //Validate the nickname
                        if (!await EnforceCharacterName(player))
                        {
                            try
                            {
                                //Invoke the change
                                OnPlayerUpdate?.Invoke(_connections[player.Connection]);
                            }
                            catch (Exception e)
                            {
                                Logger.LogError(e, "OnPlayerUpdate Exception - Nickname: {0}");
                            }
                        }
                    }
                    else
                    {
                        Logger.LogError("Failed to set the nickname '{0}' on player '{1}' because they are not on the connection list!", nickname, msg.Author);
                    }
                }

                //Abort now since nicknames are the only chat evernts we care about
                return false;
            }

            //Only info logs contain useful information
            if (msg.Level != Message.LogLevel.Info) return false;

            //check if its a warp message
            if (msg.Content.StartsWith("UniverseServer: Warp") && !msg.Content.Contains("failed"))
            {
                int toIndex = msg.Content.IndexOf('t');
                string sub = msg.Content.Substring(31, toIndex - 1 - 31);
                int connection = int.Parse(sub);
                string location = msg.Content.Substring(toIndex + 3);
                
                if (_connections.ContainsKey(connection))
                {
                    Logger.Log("Player updated their location!");
                    _connections[connection].Whereami = location;

                    //Attempt to update the cache
                    //TODO: Make a WorldManager
                    if (_connections[connection].Location is CelestialWorld celesial && celesial.Details == null)
                        try { await celesial.GetDetailsAsync(Server); } catch (Exception) { }

                    try
                    {
                        //Tell the world the event occured
                        OnPlayerUpdate?.Invoke(_connections[connection]);
                    }
                    catch (Exception e)
                    {
                        Logger.LogError(e, "OnPlayerUpdate Exception: {0}");
                    }
                }
                else
                {
                    Logger.LogError("Attempted to warp a player that isn't in the connection list!");
                }

                //Abort now since only warp starts with UniverseServer: Warp
                return false;
            }

            //check if its a logged in message
            if (msg.Content.StartsWith("UniverseServer: Logged"))
            {
                //Match the regex
                var match = regexLoggedMsg.Match(msg.Content);
                if (match.Success)
                {
                    //Get the groups
                    string account = match.Groups[1].Value;
                    string character = match.Groups[2].Value;
                    string address = match.Groups[3].Value;

                    //Do some trimming of the accounts
                    if (account.Equals("<anonymous>")) account = null;
                    else account = account.Substring(1, account.Length - 2);

                    //Add to the pending connections
                    _pending.Add(new PendingPlayer() {
                        address = address,
                        account = account,
                        character = character
                    });
                }
                else
                {
                    Logger.LogWarning("Unable to match logged message: {0}", msg.Content);
                }

                //Abort now since only logged starts with UniverseServer: Logged
                return false;
            }

            //check if the message is a connected message
            if (msg.Content.StartsWith("UniverseServer: Client"))
            {
                //Match the regex
                var match = regexClientMsg.Match(msg.Content);
                if (match.Success)
                {
                    //Get the data
                    string character = match.Groups[1].Value;
                    int connection = int.Parse(match.Groups[2].Value);
                    string address = match.Groups[3].Value;
                    bool wasConnection = match.Groups[4].Value == "connected";
                    string reason = match.Groups[6].Success ? match.Groups[6].Value : null;

                    if (wasConnection)
                    {
                        //If we are connecting, look for the matching pending connection
                        var pending = _pending.Where(pp => pp.address.Equals(address) && pp.character.Equals(character)).FirstOrDefault();
                        if (pending.character.Equals(character))
                        {
                            //Remove the account from the pending and create a new session
                            _pending.Remove(pending);

                            //Try to check if the address is a VPN
                            bool isVPN = false;
                            try
                            {
                                if (Validator != null)
                                {
                                    Logger.Log("Checking IP for VPN: {0}", pending.address);
                                    isVPN = Validator.Check(pending.address);
                                }
                            }
                            catch (Exception e)
                            {
                                Logger.LogError(e, "Failed to check address: {0}");
                            }

                            //Create the player object
                            var player = new Player(Server, connection)
                            {
                                AccountName = pending.account,
                                Username = pending.character,
                                IP = pending.address,
                                IsVPN = isVPN
                            };

                            //Add to the connections
                            _connections.TryAdd(connection, player);
                            await CreateSessionAsync(player);

                            //Save the last seen date of the account.
                            if (!player.IsAnonymous)
                            {
                                var account = await player.GetAccountAsync();
                                if (account != null)
                                {
                                    player.IsAdmin = account.IsAdmin;
                                    account.UpdateLastSeen();
                                    await account.SaveAsync(Server.DbContext);
                                }
                            }
                            
                            //Send the event off
                            try
                            {
                                //Invoke the events. This shouldn't have to many listeners to this.
                                Logger.Log("Player Connected: {0}", player);
                                OnPlayerConnect?.Invoke(_connections[connection]);

                                //Make sure they have a valid username
                                await EnforceCharacterName(_connections[connection]);
                            }
                            catch (Exception e)
                            {
                                Logger.LogError(e, "OnPlayerConnect Exception: {0}", e);
                            }
                        }
                        else
                        {
                            Logger.LogError("Failed to find the pending request! " + address);
                        }
                    }
                    else
                    {
                        //It was a disconnection, so find them in the list of players
                        Player player;
                        if (_connections.TryGetValue(connection, out player))
                        {
                            try
                            {
                                //Invoke the events. This shouldn't have to many listeners to this.
                                OnPlayerDisconnect?.Invoke(player, reason ?? "Disconnected for unkown reasons");
                            }
                            catch (Exception e)
                            {
                                Logger.LogError(e, "OnPlayerDisconnect Exception: {0}", e);
                            }
                        }

                        //Remove the connection
                        _connections.TryRemove(connection, out _);
                        await RemoveSessionAsync(connection);
                    }
                }
                else
                {
                    Logger.LogWarning("Unable to match client message: {0}", msg.Content);
                }

                //Abort now since only client starts with UniverseServer: Client
                return false;
            }

            return false;
        }

        public override async Task OnServerStart()
        {
            if (Server.Rcon != null)
            {
                //Initialize the timer because its null
                if (_uuidTimer == null)
                {
                    _uuidTimer = new System.Timers.Timer(1 * 60 * 1000) { AutoReset = true };
                    _uuidTimer.Elapsed += async (sender, args) => await RefreshListing();
                }

                //Star tthe time
                _uuidTimer.Start();
            }

            //Clear all sessions
            await ClearSessionsAsync();
            await base.OnServerStart();
        }
        public override async Task OnServerExit(string reason)
        {
            //Clear all pending connections and existing connections.
            _pending.Clear();
            _connections.Clear();

            //Stop the time if we have it.
            if (_uuidTimer != null) _uuidTimer.Stop();

            //Clear all sessions
            await ClearSessionsAsync();

            //We do not send any events as what ever client that listens to this should expect a server exit to result in 0 connections.
            await base.OnServerExit(reason);
        }
        #endregion

        #region Getters
        /// <summary>
        /// Enumerates over all the players
        /// </summary>
        /// <returns></returns>
        public IEnumerable<Player> GetPlayersEnumerable() => _connections.Values;

        public IEnumerable<Session> GetSessionsEnumerator() => _sessions.Values;

        /// <summary>
        /// Gets a copy of the player's array, garuantee to be thread safe.
        /// </summary>
        /// <returns></returns>
        public IEnumerable<Player> GetCopiedPlayersEnumerable() => _connections.ToArray().Select(s => s.Value);

        public Session[] GetSessions() => GetSessionsEnumerator().ToArray();
                
        #endregion

        #region Helpers
        /// <summary>
        /// Gets the player at the given index
        /// </summary>
        /// <param name="connection"></param>
        /// <returns></returns>
        public Player GetPlayer(int connection)
        {
            Player player;
            if (_connections.TryGetValue(connection, out player)) return player;
            return null;
        }

        /// <summary>
        /// Checks if the connection still exists
        /// </summary>
        /// <param name="connection"></param>
        /// <returns></returns>
        public bool HasPlayer(int connection) => _connections.ContainsKey(connection);

        /// <summary>
        /// Checks if the player is still connected
        /// </summary>
        /// <param name="connection"></param>
        /// <returns></returns>
        public bool IsConnected(Player player)
        {
            if (_connections.TryGetValue(player.Connection, out var other))
            {
                if (player == other) return true;
                if (player.UUID != null && other.UUID != null) return player.UUID == other.UUID;
                return player.AccountName == other.AccountName && player.Username == other.Username;
            }

            return false;
        }

        /// <summary>
        /// Tries to get a session
        /// </summary>
        /// <param name="connection"></param>
        /// <returns></returns>
        public Session GetSession(int connection)
        {
            if (_sessions.TryGetValue(connection, out var session)) return session;
            return null;
        }

        public async Task<bool> RefreshListing()
        {
            //Make sure listing is enabled
            if (Server.Rcon == null) return false;

            Logger.Log("Performing Listing Refresh");
            if (_connections.Count == 0)
            {
                Logger.Log("Skipping refresh because we have no connections.");
                return false;
            }

            //Create a mapping of users and prepare some temporary array for additions and removals.
            var listedUsers = await Server.Rcon.ListAsync();
            List<int> removals = new List<int>();

            Logger.Log("Fetched " + listedUsers.Length);

            //Go through each listing adding elements and updating existing elements
            foreach (var lu in listedUsers)
            {
                Player player;
                if (_connections.TryGetValue(lu.Connection, out player))
                {
                    if (player.UUID != lu.UUID)
                    {
                        Player newp = new Player(player) { UUID = lu.UUID };
                        if (_connections.TryUpdate(lu.Connection, newp, player))
                        {
                            try
                            {
                                //Send the update event
                                Logger.Log("Player Updated from RCON LIST: " + newp);
                                OnPlayerUpdate?.Invoke(newp);
                            }
                            catch(Exception e)
                            {
                                Logger.LogError(e, "OnPlayerUpdate for RCON Exception: {0}");
                            }
                        }
                        else
                        {
                            Logger.LogError("Failed to update connection: " + newp);
                        }
                    }
                }
                else
                {
                    //Its a new connection.
                    player = new Player(Server, lu.Connection)
                    {
                        UUID = lu.UUID,
                        Username = lu.Name
                    };

                    //Skip the player if they are pending
                    if (_pending.ToArray().Any(pp => pp.character == lu.Name))
                    {
                        Logger.Log("Skipping user " + player + " because they are pending.");
                        continue;
                    }

                    //Add it
                    if (_connections.TryAdd(player.Connection, player))
                    {
                        try
                        {
                            Logger.Log("User " + player + " joined without us noticing.");

                            //Make sure they have a valid username
                            await EnforceCharacterName(player);
                            OnPlayerConnect?.Invoke(player);

                            //we should tell starwatch to restart because this only ever happens when the logs stop working
                            await Server.Terminate("User joined without notice, dead logs.");
                        }
                        catch (Exception e)
                        {
                            Logger.LogError(e, "OnPlayerConnect for Sneaky Exception: {0}");
                        }
                    }
                }

                //Update the sessions
                await UpdateSessionAsync(player);
            }

            return true;
        }

        public async Task<World> RefreshLocation(Player player) => await RefreshLocation(player.Connection);
        public async Task<World> RefreshLocation(int connection)
        {
            //Make sure listing is enabled and make sure the player exists
            if (Server.Rcon == null) return null;
            if (!_connections.ContainsKey(connection))
            {
                Logger.LogError("Attempted to refresh the location of a null connection: {0}", connection);
                return null;
            }
            
            //Perform a whereis
            var response = await Server.Rcon.WhereisAsync(connection);
            if (response.Success)
            {
                //Update the location and execute the event
                Logger.Log("Player updated their location via whereis!");
                _connections[connection].Whereami = response.Message;
                try
                {
                    OnPlayerUpdate?.Invoke(_connections[connection]);
                }
                catch (Exception e)
                {
                    Logger.LogError(e, "OnPlayerUpdate Exception: {0}");
                }

                //Return the new world
                return _connections[connection].Location;
            }
            else
            {
                Logger.LogWarning("Failed to refresh a players location: " + response.Message);
                return null;
            }
        }

        private async Task<bool> EnforceCharacterName(Player player)
        {
            if (!ENFORCE_STRICT_NAMES) return false;
            if (player == null)
            {
                Logger.LogWarning("Trying to enforce the name of a null player!");
                return false;
            }

            bool containsIllegalCharacters = false;
            if (!IsNameLegal(player.Username, player.IsAdmin))  containsIllegalCharacters = true;
            if (!IsNameLegal(player.Nickname, player.IsAdmin))  containsIllegalCharacters = true;
            if (player.Username.Contains("Lachee") && player.AccountName?.ToLowerInvariant() != "lachee") containsIllegalCharacters = true;

            //If they are illegal, t hen kick them.
            if (containsIllegalCharacters)
            {
                await Server.Kick(player.Connection, "Character name contains illegal characters or strings.");
                return true;
            }

            //Make sure no duplicate names
            if (_connections.Any(kp => kp.Value.Username.Equals(player.Username) && kp.Key != player.Connection))
            {
                await Server.Kick(player.Connection, "Character with the same name already exists on the server.");
                return true;
            }

            return false;
        }

        private static bool IsNameLegal(string name, bool allowEmptyName = false)
        {
            if (name == null) return true;

            name = name.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(name)) return allowEmptyName;
            if (name.Length == 0) return false;
            if (name.Contains('>')) return false;
            if (name.Contains('<')) return false;
            if (name.Contains(')')) return false;
            if (name.Contains('(')) return false;
            if (name.Contains(':')) return false;
            if (name.Contains('\n')) return false;

            if (name.StartsWith("server")) return false;
            if (name.StartsWith("chat")) return false;

            string[] forbidden = new string[] { "server", "chat", "universe", "info", "warn", "err", "admin", "staff" };
            return !forbidden.Contains(name);
        }

        #endregion

        /// <summary>
        /// Creates a new session
        /// </summary>
        /// <param name="player"></param>
        /// <returns></returns>
        private async Task<Session> CreateSessionAsync(Player player)
        {
            //Remove any previous session, just incase
            await RemoveSessionAsync(player.Connection);

            //Create the new session, save it, remember it, return it.
            Session session = new Session(player);
            await session.SaveAsync(Server.DbContext);
            _sessions.Add(player.Connection, session);
            return session;
        }

        /// <summary>
        /// Updates a session
        /// </summary>
        /// <param name="player"></param>
        /// <returns></returns>
        private async Task<Session> UpdateSessionAsync(Player player)
        {
            Session session = null;
            if (!_sessions.TryGetValue(player.Connection, out session))
                session = await CreateSessionAsync(player);

            session.Player = player;
            await session.SaveAsync(Server.DbContext);
            return session;
        }

        /// <summary>
        /// Removes a session for a player
        /// </summary>
        /// <param name="player"></param>
        /// <returns></returns>
        private async Task<bool> RemoveSessionAsync(int connection)
        {
            if (_sessions.TryGetValue(connection, out var session))
                return await RemoveSessionAsync(session);

            return false;
        }

        /// <summary>
        /// Removes a session
        /// </summary>
        /// <returns></returns>
        private async Task<bool> RemoveSessionAsync(Session session)
        {
            await session.FinishAsync();
            return _sessions.Remove(session.Player.Connection);
        }

        /// <summary>
        /// Clears all our sessions
        /// </summary>
        /// <returns></returns>
        private async Task ClearSessionsAsync()
        {
            await Session.FinishAllAsync(Server.DbContext);
            _sessions.Clear();
        }
    }
}

#if DUEOIFHBEO

[06:24:39.916] [Info] UniverseServer: Connection received from: 104.220.6.121:55214
[06:24:40.981] [Info] UniverseServer: Logged in account ''Kirisis'' as player '^cornflowerblue;Bane^reset;' from address 104.220.6.121
[06:24:40.998] [Info] UniverseServer: Client '^cornflowerblue;Bane^reset;' <8> (104.220.6.121) connected

#endif