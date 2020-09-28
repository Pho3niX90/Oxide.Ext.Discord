namespace Oxide.Ext.Discord.WebSockets
{
    using System;
    using System.Linq;
    using System.Timers;
    using Newtonsoft.Json;
    using Oxide.Core;
    using Oxide.Ext.Discord.DiscordEvents;
    using Oxide.Ext.Discord.DiscordObjects;
    using Oxide.Ext.Discord.Exceptions;
    using Oxide.Ext.Discord.Gateway;
    using WebSocketSharp;

    public class SocketListner
    {
        private DiscordClient client;

        private Socket webSocket;

        private int retries;

        public SocketListner(DiscordClient client, Socket socket)
        {
            this.client = client;
            this.webSocket = socket;
            retries = 0;
        }

        public void SocketOpened(object sender, EventArgs e)
        {

            if (client.Settings.Debugging)
            {
                Interface.Oxide.LogDebug($"Discord WebSocket opened.");
            }
            Interface.Oxide.LogWarning("[Discord Extension] Discord socket opened!");

            client.CallHook("DiscordSocket_WebSocketOpened");
        }

        public void SocketClosed(object sender, CloseEventArgs e)
        {
            if (e.Code == 4004)
            {
                Interface.Oxide.LogError("[Discord Extension] Given Bot token is invalid!");
                throw new APIKeyException();
            }

            if (client.Settings.Debugging)
            {
                Interface.Oxide.LogDebug($"Discord WebSocket closed. Code: {e.Code}, reason: {e.Reason}");
            }

            if (client.requestReconnect)
            {
                client.requestReconnect = false;
                webSocket.Connect(client.WSSURL);
                return;
            }

            if (e.Code == 4006)
            {
                webSocket.hasConnectedOnce = false;
                Interface.Oxide.LogWarning("[Discord Extension] Discord session no longer valid... Reconnecting...");
                client.REST.Shutdown(); // Clean up buckets
                webSocket.Connect(client.WSSURL);
                client.CallHook("DiscordSocket_WebSocketClosed", null, e.Reason, e.Code, e.WasClean);
                return;
            }

            if (!e.WasClean)
            {
                Interface.Oxide.LogWarning($"[Discord Extension] Discord connection closed uncleanly: code {e.Code}, Reason: {e.Reason}");

                if(retries >= 5)
                {
                    Interface.Oxide.LogError("[Discord Extension] Exceeded number of retries... Attempting in 15 seconds.");
                    Timer reconnecttimer = new Timer() { Interval = 15000f, AutoReset = false };
                    reconnecttimer.Elapsed += (object a, ElapsedEventArgs b) =>
                    {
                        if (client == null) return;
                        retries = 0;
                        Interface.Oxide.LogWarning($"[Discord Extension] Attempting to reconnect to Discord...");
                        client.REST.Shutdown(); // Clean up buckets
                        webSocket.Connect(client.WSSURL);
                    };
                    reconnecttimer.Start();
                    return;
                }
                retries++;

                Interface.Oxide.LogWarning($"[Discord Extension] Attempting to reconnect to Discord...");
                client.REST.Shutdown(); // Clean up buckets
                webSocket.Connect(client.WSSURL);
            }
            else
            {
                Discord.CloseClient(client);
            }
            
            client.CallHook("DiscordSocket_WebSocketClosed", null, e.Reason, e.Code, e.WasClean);
        }

        public void SocketErrored(object sender, ErrorEventArgs e)
        {
            if (e.Exception is APIKeyException)
                return;
            if(e.Exception is NoURLException)
            {
                Interface.Oxide.LogError("[Discord Extension] Error: WSSURL not present! Retrying..");
                DiscordObjects.Gateway.GetGateway(client, (gateway) =>
                {
                    // Example: wss://gateway.discord.gg/?v=6&encoding=json
                    string fullURL = $"{gateway.URL}/?{Connect.Serialize()}";

                    if (client.Settings.Debugging)
                    {
                        Interface.Oxide.LogDebug($"Got Gateway url: {fullURL}");
                    }

                    client.UpdateWSSURL(fullURL);
                    webSocket.Connect(client.WSSURL);
                });
                return;
            }
            Interface.Oxide.LogWarning($"[Discord Extension] An error has occured: Response: {e.Message}");

            client.CallHook("DiscordSocket_WebSocketErrored", null, e.Exception, e.Message);

            if (client == null) return;
            if (retries > 0) return; // Retry timer is already triggered
            Interface.Oxide.LogWarning($"[Discord Extension] Attempting to reconnect to Discord...");
            client.REST.Shutdown(); // Clean up buckets
            webSocket.Connect(client.WSSURL);
        }

        public void SocketMessage(object sender, MessageEventArgs e)
        {
            RPayload payload = JsonConvert.DeserializeObject<RPayload>(e.Data);

            if (payload.Sequence.HasValue)
            {
                client.Sequence = payload.Sequence.Value;
            }

            if (client.Settings.Debugging)
            {
                Interface.Oxide.LogDebug($"Recieved socket message, OpCode: {payload.OpCode}");
            }

            switch (payload.OpCode)
            {
                // Dispatch (dispatches an event)
                case OpCodes.Dispatch:
                {
                    if (client.Settings.Debugging)
                    {
                        Interface.Oxide.LogDebug($"Recieved OpCode 0, event: {payload.EventName}");
                    }

                    // Listed here: https://discordapp.com/developers/docs/topics/gateway#commands-and-events-gateway-events
                    switch (payload.EventName)
                    {
                        case "READY":
                        {
                            /*
                            Moved to DiscordClient.Initialized -> Not at all cases will READY be called.
                            client.UpdatePluginReference();
                            client.CallHook("DiscordSocket_Initialized");
                            */

                            Ready ready = payload.EventData.ToObject<Ready>();

                            if (ready.Guilds.Count != 0)
                            {
                                Interface.Oxide.LogWarning($"[Discord Extension] Your bot was found in {ready.Guilds.Count} Guilds!");
                            }

                            if (ready.Guilds.Count == 0 && client.Settings.Debugging)
                            {
                                Interface.Oxide.LogDebug($"[Discord Extension] Ready event but no Guilds sent.");
                            }

                            client.DiscordServers = ready.Guilds;
                            client.SessionID = ready.SessionID;
                            
                            client.CallHook("Discord_Ready", null, ready);
                            break;
                        }

                        case "RESUMED":
                        {
                            Resumed resumed = payload.EventData.ToObject<Resumed>();
                            Interface.Oxide.LogWarning("[Discord Extension] Session resumed!");
                            client.CallHook("Discord_Resumed", null, resumed);
                            break;
                        }

                        case "CHANNEL_CREATE":
                        {
                            Channel channelCreate = payload.EventData.ToObject<Channel>();
                            if (channelCreate.type == ChannelType.DM || channelCreate.type == ChannelType.GROUP_DM)
                                client.DMs.Add(channelCreate);
                            else
                                client.GetGuild(channelCreate.guild_id).channels.Add(channelCreate);
                            client.CallHook("Discord_ChannelCreate", null, channelCreate);
                            break;
                        }

                        case "CHANNEL_UPDATE":
                        {
                            Channel channelUpdated = payload.EventData.ToObject<Channel>();
                            Channel channelPrevious = (channelUpdated.type == ChannelType.DM || channelUpdated.type == ChannelType.GROUP_DM)
                                ? client.DMs?.FirstOrDefault(x => x.id == channelUpdated.id)
                                : client.GetGuild(channelUpdated.guild_id).channels.FirstOrDefault(x => x.id == channelUpdated.id);

                            if (channelPrevious != null)
                            {
                                if (channelUpdated.type == ChannelType.DM || channelUpdated.type == ChannelType.GROUP_DM)
                                    client.DMs.Remove(channelPrevious);
                                else
                                    client.GetGuild(channelUpdated.guild_id).channels.Remove(channelPrevious);
                            }

                            if (channelUpdated.type == ChannelType.DM || channelUpdated.type == ChannelType.GROUP_DM)
                                client.DMs.Add(channelUpdated);
                            else
                                client.GetGuild(channelUpdated.guild_id).channels.Add(channelUpdated);

                            client.CallHook("Discord_ChannelUpdate", null, channelUpdated, channelPrevious);
                            break;
                        }

                        case "CHANNEL_DELETE":
                        {
                            Channel channelDelete = payload.EventData.ToObject<Channel>();

                            client.GetGuild(channelDelete.guild_id).channels.Remove(channelDelete);

                            client.CallHook("Discord_ChannelDelete", null, channelDelete);
                            break;
                        }

                        case "CHANNEL_PINS_UPDATE":
                        {
                            ChannelPinsUpdate channelPinsUpdate = payload.EventData.ToObject<ChannelPinsUpdate>();
                            client.CallHook("Discord_ChannelPinsUpdate", null, channelPinsUpdate);
                            break;
                        }

                        // NOTE: Some elements of Guild object is only sent with GUILD_CREATE
                        case "GUILD_CREATE":
                        {
                            Guild guildCreate = payload.EventData.ToObject<Guild>();
                            string g_id = guildCreate.id;
                            bool g_unavail = guildCreate.unavailable ?? false;
                            if(client.GetGuild(g_id) == null)
                            {
                                client.DiscordServers.Add(guildCreate);
                                if (client.Settings.Debugging)
                                    Interface.Oxide.LogDebug($"[Discord Extension] Guild ID ({g_id}) added to list.");
                            }
                            else if(g_unavail == false && (client.GetGuild(g_id)?.unavailable ?? false) == true)
                            {
                                client.UpdateGuild(g_id, guildCreate);
                                if (client.Settings.Debugging)
                                    Interface.Oxide.LogDebug($"[Discord Extension] Guild ID ({g_id}) updated to list.");
                            }
                            client.CallHook("Discord_GuildCreate", null, guildCreate);
                            break;
                        }

                        case "GUILD_UPDATE":
                        {
                            Guild guildUpdate = payload.EventData.ToObject<Guild>();
                            //client.UpdateGuild(guildUpdate.id, guildUpdate); // <-- DON'T REPLACE GUILD REFERENCE!!!!
                            client.GetGuild(guildUpdate.id).Update(guildUpdate);
                            client.CallHook("Discord_GuildUpdate", null, guildUpdate);
                            break;
                        }

                        case "GUILD_DELETE":
                        {
                            Guild guildDelete = payload.EventData.ToObject<Guild>();
                            if(guildDelete.unavailable ?? false == true) // outage
                            {
                                Interface.Oxide.LogDebug($"[DEBUG] Guild ID {guildDelete.id} outage!");
                                client.UpdateGuild(guildDelete.id, guildDelete);
                            }
                            else
                            {
                                Interface.Oxide.LogDebug($"[DEBUG] Guild ID {guildDelete.id} removed from list");
                                client.DiscordServers.Remove(client.GetGuild(guildDelete.id)); // guildDelete may not be same reference
                            }
                            client.CallHook("Discord_GuildDelete", null, guildDelete);
                            break;
                        }

                        case "GUILD_BAN_ADD":
                        {
                            User bannedUser = payload.EventData.ToObject<BanObject>().user;
                            client.CallHook("Discord_GuildBanAdd", null, bannedUser);
                            break;
                        }

                        case "GUILD_BAN_REMOVE":
                        {
                            User unbannedUser = payload.EventData.ToObject<BanObject>().user;
                            client.CallHook("Discord_GuildBanRemove", null, unbannedUser);
                            break;
                        }

                        case "GUILD_EMOJIS_UPDATE":
                        {
                            GuildEmojisUpdate guildEmojisUpdate = payload.EventData.ToObject<GuildEmojisUpdate>();
                            client.CallHook("Discord_GuildEmojisUpdate", null, guildEmojisUpdate);
                            break;
                        }

                        case "GUILD_INTEGRATIONS_UPDATE":
                        {
                            GuildIntergrationsUpdate guildIntergrationsUpdate = payload.EventData.ToObject<GuildIntergrationsUpdate>();
                            client.CallHook("Discord_GuildIntergrationsUpdate", null, guildIntergrationsUpdate);
                            break;
                        }

                        case "GUILD_MEMBER_ADD":
                        {
                            GuildMemberAdd memberAdded = payload.EventData.ToObject<GuildMemberAdd>();
                            GuildMember guildMember = memberAdded as GuildMember;

                            client.GetGuild(memberAdded.guild_id)?.members.Add(guildMember);

                            client.CallHook("Discord_MemberAdded", null, guildMember);
                            break;
                        }

                        case "GUILD_MEMBER_REMOVE":
                        {
                            GuildMemberRemove memberRemoved = payload.EventData.ToObject<GuildMemberRemove>();

                            GuildMember member = client.GetGuild(memberRemoved.guild_id)?.members.FirstOrDefault(x => x.user.id == memberRemoved.user.id);
                            if (member != null)
                            {
                                client.GetGuild(memberRemoved.guild_id)?.members.Remove(member);
                            }

                            client.CallHook("Discord_MemberRemoved", null, member);
                            break;
                        }

                        case "GUILD_MEMBER_UPDATE":
                        {
                            GuildMemberUpdate memberUpdated = payload.EventData.ToObject<GuildMemberUpdate>();

                            GuildMember newMember = client.GetGuild(memberUpdated.guild_id)?.members.FirstOrDefault(x => x.user.id == memberUpdated.user.id);
                            GuildMember oldMember = Newtonsoft.Json.Linq.JObject.FromObject(newMember).ToObject<GuildMember>(); // lazy way to copy the object
                            if (newMember != null)
                            {
                                if (memberUpdated.user != null)
                                    newMember.user = memberUpdated.user;
                                if (memberUpdated.nick != null)
                                    newMember.nick = memberUpdated.nick;
                                if (memberUpdated.roles != null)
                                    newMember.roles = memberUpdated.roles;
                             }

                            client.CallHook("Discord_GuildMemberUpdate", null, memberUpdated, oldMember);
                            break;
                        }

                        case "GUILD_MEMBERS_CHUNK":
                        {
                            GuildMembersChunk guildMembersChunk = payload.EventData.ToObject<GuildMembersChunk>();
                            client.CallHook("Discord_GuildMembersChunk", null, guildMembersChunk);
                            break;
                        }

                        case "GUILD_ROLE_CREATE":
                        {
                            GuildRoleCreate guildRoleCreate = payload.EventData.ToObject<GuildRoleCreate>();

                            client.GetGuild(guildRoleCreate.guild_id)?.roles.Add(guildRoleCreate.role);

                            client.CallHook("Discord_GuildRoleCreate", null, guildRoleCreate.role);
                            break;
                        }

                        case "GUILD_ROLE_UPDATE":
                        {
                            GuildRoleUpdate guildRoleUpdate = payload.EventData.ToObject<GuildRoleUpdate>();
                            Role newRole = guildRoleUpdate.role;

                            Role oldRole = client.GetGuild(guildRoleUpdate.guild_id).roles.FirstOrDefault(x => x.id == newRole.id);
                            if (oldRole != null)
                            {
                                client.GetGuild(guildRoleUpdate.guild_id).roles.Remove(oldRole);
                            }

                            client.GetGuild(guildRoleUpdate.guild_id).roles.Add(newRole);

                            client.CallHook("Discord_GuildRoleUpdate", null, newRole, oldRole);
                            break;
                        }

                        case "GUILD_ROLE_DELETE":
                        {
                            GuildRoleDelete guildRoleDelete = payload.EventData.ToObject<GuildRoleDelete>();

                            Role deletedRole = client.GetGuild(guildRoleDelete.guild_id)?.roles.FirstOrDefault(x => x.id == guildRoleDelete.role_id);
                            if (deletedRole != null)
                            {
                                client.GetGuild(guildRoleDelete.guild_id).roles.Remove(deletedRole);
                            }

                            client.CallHook("Discord_GuildRoleDelete", null, deletedRole);
                            break;
                        }

                        case "MESSAGE_CREATE":
                        {
                            Message messageCreate = payload.EventData.ToObject<Message>();
                            Channel c;
                            if (messageCreate.guild_id != null)
                                c = client.GetGuild(messageCreate.guild_id)?.channels.FirstOrDefault(x => x.id == messageCreate.channel_id);
                            else
                                c = client.DMs.FirstOrDefault(x => x.id == messageCreate.channel_id);
                            if(c != null)
                                c.last_message_id = messageCreate.id;
                            client.CallHook("Discord_MessageCreate", null, messageCreate);
                            break;
                        }

                        case "MESSAGE_UPDATE":
                        {
                            Message messageUpdate = payload.EventData.ToObject<Message>();
                            client.CallHook("Discord_MessageUpdate", null, messageUpdate);
                            break;
                        }

                        case "MESSAGE_DELETE":
                        {
                            MessageDelete messageDelete = payload.EventData.ToObject<MessageDelete>();
                            client.CallHook("Discord_MessageDelete", null, messageDelete);
                            break;
                        }

                        case "MESSAGE_DELETE_BULK":
                        {
                            MessageDeleteBulk messageDeleteBulk = payload.EventData.ToObject<MessageDeleteBulk>();
                            client.CallHook("Discord_MessageDeleteBulk", null, messageDeleteBulk);
                            break;
                        }

                        case "MESSAGE_REACTION_ADD":
                        {
                            MessageReactionUpdate messageReactionAdd = payload.EventData.ToObject<MessageReactionUpdate>();
                            client.CallHook("Discord_MessageReactionAdd", null, messageReactionAdd);
                            break;
                        }

                        case "MESSAGE_REACTION_REMOVE":
                        {
                            MessageReactionUpdate messageReactionRemove = payload.EventData.ToObject<MessageReactionUpdate>();
                            client.CallHook("Discord_MessageReactionRemove", null, messageReactionRemove);
                            break;
                        }

                        case "MESSAGE_REACTION_REMOVE_ALL":
                        {
                            MessageReactionRemoveAll messageReactionRemoveAll = payload.EventData.ToObject<MessageReactionRemoveAll>();
                            client.CallHook("Discord_MessageReactionRemoveAll", null, messageReactionRemoveAll);
                            break;
                        }

                        /*
                         * From Discord API docs:
                         * The user object within this event can be partial, the only field which must be sent is the id field, everything else is optional.
                         * Along with this limitation, no fields are required, and the types of the fields are not validated.
                         * Your client should expect any combination of fields and types within this event.
                        */

                        case "PRESENCE_UPDATE":
                        {
                            PresenceUpdate presenceUpdate = payload.EventData.ToObject<PresenceUpdate>();

                            User updatedPresence = presenceUpdate?.user;

                            if (updatedPresence != null)
                            {
                                var updatedMember = client.GetGuild(presenceUpdate.guild_id)?.members.FirstOrDefault(x => x.user.id == updatedPresence.id);

                                if (updatedMember != null)
                                {
                                    //updatedMember.user = updatedPresence;
                                    updatedMember.user.Update(updatedPresence);
                                }
                            }

                            client.CallHook("Discord_PresenceUpdate", null, updatedPresence);
                            break;
                        }

                        // Bots should ignore this
                        case "PRESENCES_REPLACE":
                            break;

                        case "TYPING_START":
                        {
                            TypingStart typingStart = payload.EventData.ToObject<TypingStart>();
                            client.CallHook("Discord_TypingStart", null, typingStart);
                            break;
                        }

                        case "USER_UPDATE":
                        {
                            User userUpdate = payload.EventData.ToObject<User>();

                            //GuildMember memberUpdate = client.DiscordServer.members.FirstOrDefault(x => x.user.id == userUpdate.id);

                            //memberUpdate.user = userUpdate;

                            var guilds = client.DiscordServers.Where(x => x.members.FirstOrDefault(y => y.user.id == userUpdate.id) != null).ToList();
                            foreach(Guild g in guilds)
                            {
                                GuildMember memberUpdate = g.members.FirstOrDefault(x => x.user.id == userUpdate.id);
                                memberUpdate.user = userUpdate;
                            }

                            client.CallHook("Discord_UserUpdate", null, userUpdate);
                            break;
                        }

                        case "VOICE_STATE_UPDATE":
                        {
                            VoiceState voiceStateUpdate = payload.EventData.ToObject<VoiceState>();
                            client.CallHook("Discord_VoiceStateUpdate", null, voiceStateUpdate);
                            break;
                        }

                        case "VOICE_SERVER_UPDATE":
                        {
                            VoiceServerUpdate voiceServerUpdate = payload.EventData.ToObject<VoiceServerUpdate>();
                            client.CallHook("Discord_VoiceServerUpdate", null, voiceServerUpdate);
                            break;
                        }

                        case "WEBHOOKS_UPDATE":
                        {
                            WebhooksUpdate webhooksUpdate = payload.EventData.ToObject<WebhooksUpdate>();
                            client.CallHook("Discord_WebhooksUpdate", null, webhooksUpdate);
                            break;
                        }

                        case "INVITE_CREATE":
                        {
                            InviteCreated invitecreatedUpdate = payload.EventData.ToObject<InviteCreated>();
                            client.CallHook("Discord_InviteCreated", null, invitecreatedUpdate);
                            break;
                        }

                        case "INVITE_DELETE":
                        {
                            InviteDeleted invitedeletedUpdate = payload.EventData.ToObject<InviteDeleted>();
                            client.CallHook("Discord_InviteDeleted", null, invitedeletedUpdate);
                            break;
                        }

                        default:
                        {
                            client.CallHook("Discord_UnhandledEvent", null, payload);
                            Interface.Oxide.LogWarning($"[Discord Extension] [Debug] Unhandled event: {payload.EventName}");
                            break;
                        }
                    }

                    break;
                }

                // Heartbeat
                // https://discordapp.com/developers/docs/topics/gateway#gateway-heartbeat
                case OpCodes.Heartbeat:
                {
                    Interface.Oxide.LogInfo($"[Discord Extension] Manully sent heartbeat (received opcode 1)");
                    client.SendHeartbeat();
                    break;
                }

                // Reconnect (used to tell clients to reconnect to the gateway)
                // we should immediately reconnect here
                case OpCodes.Reconnect:
                {
                    Interface.Oxide.LogInfo($"[Discord Extension] Reconnect has been called (opcode 7)! Reconnecting...");
                    webSocket.hasConnectedOnce = true; // attempt resume opcode
                    webSocket.Connect(client.WSSURL);
                    break;
                }

                // Invalid Session (used to notify client they have an invalid session ID)
                case OpCodes.InvalidSession:
                {
                    Interface.Oxide.LogInfo($"[Discord Extension] Invalid Session ID opcode recieved!");
                    client.requestReconnect = true;
                    webSocket.hasConnectedOnce = false;
                    webSocket.Disconnect(false);
                    break;
                }

                // Hello (sent immediately after connecting, contains heartbeat and server debug information)
                case OpCodes.Hello:
                {
                    Hello hello = payload.EventData.ToObject<Hello>();
                    client.CreateHeartbeat(hello.HeartbeatInterval);
                    // Client should now perform identification
                    //client.Identify();
                    if (webSocket.hasConnectedOnce)
                    {
                        Interface.Oxide.LogWarning("[Discord Extension] Attempting resume opcode...");
                        client.Resume();
                    }
                    else
                    {
                        client.Identify();
                        webSocket.hasConnectedOnce = true;
                    }
                    break;
                }

                // Heartbeat ACK (sent immediately following a client heartbeat
                // that was received)
                // (See 'zombied or failed connections')
                case OpCodes.HeartbeatACK:
                {
                    client.HeartbeatACK = true;
                    break;
                }

                default:
                {
                    Interface.Oxide.LogInfo($"[Discord Extension] Unhandled OP code: code {payload.OpCode}");
                    break;
                }
            }
        }
    }
}
