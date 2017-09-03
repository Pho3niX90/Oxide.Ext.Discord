using System;
using System.Timers;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core.Plugins;
using Oxide.Ext.Discord.DiscordObjects;
using Oxide.Ext.Discord.Exceptions;
using WebSocketSharp;

namespace Oxide.Ext.Discord.WebSockets
{
    public class DiscordClient
    {
        public Plugin Plugin { get; private set; }
        public DiscordSettings Settings { get; private set; } = new DiscordSettings();
        public Guild DiscordServer { get; set; }
        public RESTHandler REST { get; private set; }
        public string WSSURL { get; private set; }
        public UpkeepHandler UpHandler { get; private set; }
        private WebSocket Socket = null;
        private SocketHandler Handler;
        private Timer Timer;
        private int LastHeartbeat;

        /// <exception cref="APIKeyException">Throws when a user does not provide a API key.</exception>
        /// <exception cref="NoURLException">Throws when CreateSocket is called, but no url is stored.</exception>
        /// <exception cref="SocketRunningException">Throws when CreateSocket is called, but a socket is already running.</exception>
        /// <exception cref="InvalidCreationException">Throws when a user tries to use this method to create a discord client. Should use the static method in the Discord class.</exception>

        public void Initialize(Plugin plugin, string apiKey)
        {
            if (string.IsNullOrEmpty(apiKey))
                throw new APIKeyException();

            Plugin = plugin ?? throw new PluginNullException();
            Settings.ApiToken = apiKey;
            REST = new RESTHandler(Settings.ApiToken);

            if (!Discord.Clients.Any(x => x.Settings.ApiToken == apiKey))
                throw new InvalidCreationException();

            this.Connect();
        }

        public void Connect()
        {
            if (!string.IsNullOrEmpty(WSSURL))
            {
                this.CreateSocket();
                return;
            }

            this.GetURL(() =>
            {
                this.CreateSocket();
            });
        }

        public void CreateSocket()
        {
            if (string.IsNullOrEmpty(WSSURL))
                throw new NoURLException();

            if (Socket != null && Socket.ReadyState != WebSocketState.Closed)
                throw new SocketRunningException();

            if (Plugin.CallHook("DiscordSocket_SocketConnecting", WSSURL) != null)
                return;

            Socket = new WebSocket(WSSURL + "/?v=6&encoding=json");
            Handler = new SocketHandler(this);
            UpHandler = new UpkeepHandler(this);
            Socket.OnOpen += Handler.SocketOpened;
            Socket.OnClose += Handler.SocketClosed;
            Socket.OnError += Handler.SocketErrored;
            Socket.OnMessage += Handler.SocketMessage;
            Socket.ConnectAsync();

            Plugin.CallHook("DiscordSocket_ConnectionStarted");
        }

        public void Disconnect()
        {
            if (Socket.IsAlive)
            {
                Socket.CloseAsync();
            }

            WSSURL = "";
            REST?.Shutdown();
            UpHandler?.Shutdown();
        }
        
        public bool IsAlive() => Socket.IsAlive;

        public bool IsClosing() => Socket.ReadyState == WebSocketState.Closing;
        
        public bool IsClosed() => Socket.ReadyState == WebSocketState.Closed;

        public void SendData(string contents) => Socket.Send(contents);
        
        public void CreateHeartbeat(float heartbeatInterval, int lastHeartbeat)
        {
            LastHeartbeat = lastHeartbeat;
            
            if (Timer != null) return;

            Timer = new Timer();
            Timer.Interval = heartbeatInterval;
            Timer.Elapsed += HeartbeatElapsed;
            Timer.Start();
        }

        private void HeartbeatElapsed(object sender, ElapsedEventArgs e)
        {
            if (!Socket.IsAlive || IsClosing() || IsClosed())
            {
                Timer.Dispose();
                Timer = null;
                return;
            }

            var packet = new Packet()
            {
                op = 1,
                d = LastHeartbeat
            };

            string message = JsonConvert.SerializeObject(packet);
            Socket.Send(message);
            Plugin.CallHook("DiscordSocket_HeartbeatSent");
        }

        private void GetURL(Action callback)
        {
            REST.DoRequest<JObject>("/gateway", "GET", null, (data) =>
            {
                WSSURL = (data as JObject).GetValue("url").ToString();
                callback.Invoke();
            });
        }
    }
}
