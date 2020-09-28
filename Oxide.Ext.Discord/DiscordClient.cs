namespace Oxide.Ext.Discord
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using System.Timers;
    using Newtonsoft.Json;
    using Oxide.Core;
    using Oxide.Core.Plugins;
    using Oxide.Ext.Discord.Attributes;
    using Oxide.Ext.Discord.DiscordEvents;
    using Oxide.Ext.Discord.DiscordObjects;
    using Oxide.Ext.Discord.Exceptions;
    using Oxide.Ext.Discord.Gateway;
    using Oxide.Ext.Discord.Helpers;
    using Oxide.Ext.Discord.REST;
    using Oxide.Ext.Discord.WebSockets;

    public class DiscordClient
    {
        public List<Plugin> Plugins { get; private set; } = new List<Plugin>();

        public RESTHandler REST { get; private set; }

        public string WSSURL { get; private set; }

        public DiscordSettings Settings { get; set; } = new DiscordSettings();

        public List<Guild> DiscordServers { get; set; } = new List<Guild>();
        public List<Channel> DMs { get; set; } = new List<Channel>();
        
        public Guild DiscordServer
        {
            get
            {
                return this.DiscordServers?.FirstOrDefault();
            }
        }

        public int Sequence;

        public string SessionID;

        private Socket _webSocket;

        private Timer _timer;

        private double _lastHeartbeat;

        public bool HeartbeatACK = false;

        public bool requestReconnect = false;

        public void Initialize(Plugin plugin, DiscordSettings settings)
        {
            if (plugin == null)
            {
                throw new PluginNullException();
            }

            if (settings == null)
            {
                throw new SettingsNullException();
            }

            if (string.IsNullOrEmpty(settings.ApiToken))
            {
                throw new APIKeyException();
            }

            /*if(Discord.PendingTokens.Contains(settings.ApiToken)) // Not efficient, will re-do later
            {
                Interface.Oxide.LogWarning($"[Discord Extension] Connection with same token in short period.. Connection delayed for {plugin.Name}");
                Timer t = new Timer() { AutoReset = false, Interval = 5000f, Enabled = true};
                t.Elapsed += (object sender, ElapsedEventArgs e) =>
                {
                    // TODO: Check if the connection still persists or cancelled
                    Interface.Oxide.LogWarning($"[Discord Extension] Delayed connection for {plugin.Name} is being resumed..");
                    Initialize(plugin, settings);
                };
                return;
            }*/

            RegisterPlugin(plugin);
            UpdatePluginReference(plugin);
            CallHook("DiscordSocket_Initialized");

            Settings = settings;

            REST = new RESTHandler(Settings.ApiToken);
            _webSocket = new Socket(this);

            if (!string.IsNullOrEmpty(WSSURL))
            {
                _webSocket.Connect(WSSURL);
                return;
            }

            this.GetURL(url =>
            {
                UpdateWSSURL(url);

                _webSocket.Connect(WSSURL);
            });

            /*Discord.PendingTokens.Add(settings.ApiToken); // Not efficient, will re-do later
            Timer t2 = new Timer() { AutoReset = false, Interval = 5000f, Enabled = true };
            t2.Elapsed += (object sender, ElapsedEventArgs e) =>
            {
                if (Discord.PendingTokens.Contains(settings.ApiToken))
                    Discord.PendingTokens.Remove(settings.ApiToken);
            };*/
        }

        public void Disconnect()
        {
            _webSocket?.Disconnect();
            DestroyHeartbeat();
            _webSocket?.Dispose();
            _webSocket = null;

            WSSURL = string.Empty;

            REST?.Shutdown();
        }

        public void UpdatePluginReference(Plugin plugin = null)
        {
            List<Plugin> affectedPlugins = (plugin == null) ? Plugins : new List<Plugin>() { plugin };

            foreach (var pluginItem in affectedPlugins)
            {
                foreach (var field in pluginItem.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                {
                    if (field.GetCustomAttributes(typeof(DiscordClientAttribute), true).Any())
                    {
                        field.SetValue(pluginItem, this);
                    }
                }
            }
        }

        public void RegisterPlugin(Plugin plugin)
        {
            var search = Plugins.Where(x => x.Title == plugin.Title);
            search.ToList().ForEach(x => Plugins.Remove(x));

            Plugins.Add(plugin);
        }

        public object CallHook(string hookname, Plugin specificPlugin = null, params object[] args)
        {
            if (specificPlugin != null)
            {
                if (!specificPlugin.IsLoaded) return null;

                return specificPlugin.CallHook(hookname, args);
            }

            Dictionary<string, object> returnValues = new Dictionary<string, object>();

            foreach (var plugin in Plugins.Where(x => x.IsLoaded))
            {
                var retVal = plugin.CallHook(hookname, args);
                returnValues.Add(plugin.Title, retVal);
            }

            if (returnValues.Count(x => x.Value != null) > 1)
            {
                string conflicts = string.Join("\n", returnValues.Select(x => $"Plugin {x.Key} - {x.Value}").ToArray());
                Interface.Oxide.LogWarning($"[Discord Extension] A hook conflict was triggered on {hookname} between:\n{conflicts}");
                return null;
            }

            return returnValues.FirstOrDefault(x => x.Value != null).Value;
        }

        public string GetPluginNames(string delimiter = ", ") => string.Join(delimiter, Plugins.Select(x => x.Name).ToArray());

        public void CreateHeartbeat(float heartbeatInterval)
        {
            if (_timer != null)
            {
                Interface.Oxide.LogWarning($"[Discord Extension] Warning: tried to create a heartbeat when one is already registered.");
                return;
            }

            _lastHeartbeat = Time.TimeSinceEpoch();
            HeartbeatACK = true;

            _timer = new Timer()
            {
                Interval = heartbeatInterval
            };
            _timer.Elapsed += HeartbeatElapsed;
            _timer.Start();
        }

        public void DestroyHeartbeat()
        {
            if(_timer != null)
            {
                _timer.Dispose();
                _timer = null;
            }
            return;
        }

        private void HeartbeatElapsed(object sender, ElapsedEventArgs e)
        {
            if (!_webSocket.IsAlive() ||
                _webSocket.IsClosing() ||
                _webSocket.IsClosed())
            {
                _timer.Dispose();
                _timer = null;
                return;
            }

            SendHeartbeat();
        }

        private void GetURL(Action<string> callback)
        {
            DiscordObjects.Gateway.GetGateway(this, (gateway) =>
            {
                // Example: wss://gateway.discord.gg/?v=6&encoding=json
                string fullURL = $"{gateway.URL}/?{Connect.Serialize()}";

                if (Settings.Debugging)
                {
                    Interface.Oxide.LogDebug($"Got Gateway url: {fullURL}");
                }

                callback.Invoke(fullURL);
            });
        }

        public void UpdateWSSURL(string fullURL)
        {
            WSSURL = fullURL;
        }

        #region Discord Events
        
        public void Identify()
        {
            // Sent immediately after connecting. Opcode 2: Identify
            // Ref: https://discordapp.com/developers/docs/topics/gateway#identifying

            Identify identify = new Identify()
            {
                Token = this.Settings.ApiToken,
                Properties = new Properties()
                {
                    OS = "Oxide.Ext.Discord",
                    Browser = "Oxide.Ext.Discord",
                    Device = "Oxide.Ext.Discord"
                },
                Compress = false,
                LargeThreshold = 50,
                Shard = new List<int>() { 0, 1 }
            };

            var opcode = new SPayload()
            {
                OP = OpCodes.Identify,
                Payload = identify
            };
            var payload = JsonConvert.SerializeObject(opcode);

            _webSocket.Send(payload);
        }
        
        public void Resume()
        {
            var resume = new Resume()
            {
                Sequence = this.Sequence,
                SessionID = this.SessionID,
                Token = Settings.ApiToken
            };

            var packet = new RPayload()
            {
                OpCode = OpCodes.Resume,
                Data = resume
            };

            string payload = JsonConvert.SerializeObject(packet);
            _webSocket.Send(payload);
        }
        
        public void SendHeartbeat()
        {
            if(!HeartbeatACK)
            {
                // Didn't receive an ACK, thus connection can be considered zombie, thus destructing.
                Interface.Oxide.LogError("[Discord Extension] Discord did not respond to Heartbeat! Disconnecting..");
                requestReconnect = true;
                _webSocket.Disconnect(false);
                return;
            }
            var packet = new RPayload()
            {
                OpCode = OpCodes.Heartbeat,
                Data = this.Sequence
            };

            HeartbeatACK = false;
            string message = JsonConvert.SerializeObject(packet);
            _webSocket.Send(message);

            _lastHeartbeat = Time.TimeSinceEpoch();

            this.CallHook("DiscordSocket_HeartbeatSent");

            if (Settings.Debugging)
            {
                Interface.Oxide.LogDebug($"[Discord Extension] Heartbeat sent - {_timer.Interval}ms interval.");
            }
        }
        
        public void RequestGuildMembers(string guild_id, string query = "", int limit = 0)
        {
            var requestGuildMembers = new GuildMembersRequest()
            {
                GuildID = guild_id,
                Query = query,
                Limit = limit
            };

            var packet = new RPayload()
            {
                OpCode = OpCodes.RequestGuildMembers,
                Data = requestGuildMembers
            };

            string payload = JsonConvert.SerializeObject(packet);
            _webSocket.Send(payload);
        }

        public void RequestGuildMembers(Guild guild, string query = "", int limit = 0)
        {
            RequestGuildMembers(guild.id, query, limit);
        }

        public void UpdateVoiceState(string guildID, string channelId, bool selfDeaf, bool selfMute)
        {
            var voiceState = new VoiceStateUpdate()
            {
                ChannelID = channelId,
                GuildID = guildID,
                SelfDeaf = selfDeaf,
                SelfMute = selfMute
            };

            var packet = new RPayload()
            {
                OpCode = OpCodes.VoiceStateUpdate,
                Data = voiceState
            };

            string payload = JsonConvert.SerializeObject(packet);
            _webSocket.Send(payload);
        }

        public void UpdateStatus(Presence presence)
        {
            var opcode = new SPayload()
            {
                OP = OpCodes.StatusUpdate,
                Payload = presence
            };

            var payload = JsonConvert.SerializeObject(opcode);
            _webSocket.Send(payload);
        }

        public Guild GetGuild(string id)
        {
            return this.DiscordServers?.FirstOrDefault(x => x.id == id);
        }

        public void UpdateGuild(string g_id, Guild newguild)
        {
            Guild g = this.GetGuild(g_id);
            if (g == null) return;
            int idx = DiscordServers?.IndexOf(g) ?? -1;
            if (idx == -1) return;
            this.DiscordServers[idx] = newguild;
        }

        #endregion
    }
}
