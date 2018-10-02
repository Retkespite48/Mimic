﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using WebSocketSharp;

namespace Conduit
{
    /**
     * Handles a connection with a mobile client, which is wrapped through a connection with the hub. Also handles
     * key negotiation and encryption.
     */
    class MobileConnectionHandler
    {
        public delegate void SendMessageDelegate(string s);

        private LeagueConnection league;
        private byte[] key;
        private Dictionary<string, Regex> observedPaths = new Dictionary<string, Regex>();

        private SendMessageDelegate Send;
        private SendMessageDelegate SendRaw;

        public MobileConnectionHandler(LeagueConnection league, SendMessageDelegate send)
        {
            this.league = league;
            this.league.OnWebsocketEvent += HandleLeagueEvent;

            this.SendRaw = send;
            this.Send = msg => SendRaw("\"" + CryptoHelpers.EncryptAES(key, msg) + "\"");
        }

        /**
         * Called by the HubConnectionHandler when the connection with this mobile peer closes.
         */
        public void OnClose()
        {
            this.league.OnWebsocketEvent -= HandleLeagueEvent;
        }

        /**
         * Handles a message incoming through rift. First checks its opcode, then possibly decrypts the contents.
         */
        public void HandleMessage(dynamic msg)
        {
            if (key != null)
            {
                try
                {
                    var contents = CryptoHelpers.DecryptAES(key, (string) msg);
                    this.HandleMimicMessage(SimpleJson.DeserializeObject(contents));
                }
                catch
                {
                    // Ignore message.
                    return;
                }
            }
        
            if (!(msg is JsonArray)) return;

            if (msg[0] == (long) MobileOpcode.Secret)
            {
                var info = CryptoHelpers.DecryptRSA((string) msg[1]);

                if (info == null)
                {
                    this.SendRaw("[" + MobileOpcode.SecretResponse + ",false]");
                    return;
                }

                dynamic contents = SimpleJson.DeserializeObject(info);
                if (contents["secret"] == null || contents["identity"] == null || contents["device"] == null) return;

                // TODO: Ask user for approval.

                this.key = Convert.FromBase64String((string) contents["secret"]);
                this.SendRaw("[" + (long) MobileOpcode.SecretResponse + ",true]");
            }
        }

        /**
         * Handles a message post-decryption, which is the raw message sent by the mobile client.
         */
        private async void HandleMimicMessage(dynamic msg)
        {
            Console.WriteLine(msg.ToString());

            if (!(msg is JsonArray)) return;


            if (msg[0] == (long) MobileOpcode.Subscribe)
            {
                var path = (string) msg[1];
                if (!observedPaths.ContainsKey(path)) observedPaths.Add(path, new Regex(path));
            }
            else if (msg[0] == (long) MobileOpcode.Unsubscribe)
            {
                var path = (string) msg[1];
                if (observedPaths.ContainsKey(path)) observedPaths.Remove(path);
            }
            else if (msg[0] == (long) MobileOpcode.Request)
            {
                var id = (long) msg[1];
                var path = (string) msg[2];
                var method = (string) msg[3];
                var body = (string) msg[4];

                var result = await league.Request(method, path, body);
                var contents = await result.Content.ReadAsStringAsync();
                if (contents.IsNullOrEmpty()) contents = "null";

                Send("[" + (long) MobileOpcode.Response + "," + id + "," + (long) result.StatusCode + "," + contents + "]");
            }
            else if (msg[0] == (long) MobileOpcode.Version)
            {
                Send("[" + (long) MobileOpcode.VersionResponse + ", \"" + Program.VERSION + "\", \"" + Environment.MachineName + "\"]");
            }
        }

        /**
         * Handles an update coming from the League socket.
         */
        private void HandleLeagueEvent(OnWebsocketEventArgs ev)
        {
            if (!observedPaths.Values.Any(x => x.IsMatch(ev.Path))) return;

            var status = ev.Type.Equals("Create") || ev.Type.Equals("Update") ? 200 : 404;
            Send("[" + (long) MobileOpcode.Update + ",\"" + ev.Path + "\"," + status + "," + ev.Data.ToString() + "]");
        }
    }

    enum MobileOpcode : long
    {
        // Mobile -> Conduit, sends encrypted shared secret.
        Secret = 1,

        // Conduit <- Mobile, sends result of secret negotiation. If true, rest of communications is encrypted.
        SecretResponse = 2,

        // Mobile -> Conduit, request version
        Version = 3,

        // Conduit <- Mobile, send version
        VersionResponse = 4,

        // Mobile -> Conduit, subscribe to LCU updates that match regex
        Subscribe = 5,

        // Mobile -> Conduit, unsibscribe to LCU updates that match regex
        Unsubscribe = 6,

        // Mobile -> Conduit, make LCU request
        Request = 7,

        // Conduit -> Mobile, response of a previous request message.
        Response = 8,

        // Conduit -> Mobile, when any subscribed endpoint gets an update
        Update = 9
    }
}
 