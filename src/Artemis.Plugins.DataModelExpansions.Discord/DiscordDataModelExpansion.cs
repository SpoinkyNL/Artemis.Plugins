﻿using Artemis.Core;
using Artemis.Core.DataModelExpansions;
using Artemis.Plugins.DataModelExpansions.Discord.DataModels;
using Artemis.Plugins.DataModelExpansions.Discord.Enums;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Artemis.Plugins.DataModelExpansions.Discord
{
    public class DiscordDataModelExpansion : DataModelExpansion<DiscordDataModel>
    {
        private static readonly JsonSerializerSettings _jsonSerializerSettings = new JsonSerializerSettings
        {
            ContractResolver = new DefaultContractResolver
            {
                NamingStrategy = new SnakeCaseNamingStrategy { ProcessDictionaryKeys = true }
            }
        };
        private static readonly HttpClient client = new HttpClient();

        private readonly PluginSetting<string> clientId;
        private readonly PluginSetting<string> clientSecret;
        private readonly PluginSetting<SavedToken> token;
        private readonly ILogger _logger;

        private static readonly string[] _scopes = new string[]
        {
            "rpc",
            "identify",
            "messages.read",
            "rpc.notifications.read"
        };
        const string PIPE = @"discord-ipc-0";
        const string RPC_VERSION = "1";
        private NamedPipeClientStream _pipe;
        private CancellationTokenSource _cancellationToken;

        public DiscordDataModelExpansion(PluginSettings pluginSettings, ILogger logger)
        {
            _logger = logger;
            clientId = pluginSettings.GetSetting<string>("DiscordClientId", null);
            clientSecret = pluginSettings.GetSetting<string>("DiscordClientSecret", null);
            token = pluginSettings.GetSetting<SavedToken>("DiscordToken", null);

            //REMOVE THIS
            if (clientId.Value == null)
            {
                clientId.Value = "YOUR_CLIENT_ID";
                clientId.Save();
            }
            if (clientSecret.Value == null)
            {
                clientSecret.Value = "YOUR_CLIENT_SECRET";
                clientSecret.Save();
            }
            //REMOVE THIS
        }

        public override void Enable()
        {
            if (clientId.Value == null || clientSecret == null)
                throw new ArtemisPluginException("Client ID or secret invalid");

            try
            {
                Connect();
            }
            catch (Exception e)
            {
                throw new ArtemisPluginException("Failed to connect to Discord RPC", e);
            }

            SendPacket(new { v = RPC_VERSION, client_id = clientId.Value }, RpcPacketType.HANDSHAKE);
            _cancellationToken = new CancellationTokenSource();
            Task.Run(StartReceive);
        }

        public override void Disable()
        {
            _cancellationToken.Cancel();
            _pipe.Dispose();
        }

        public override void Update(double deltaTime)
        {
            //nothing
        }

        #region IPC
        private void Connect()
        {
            _pipe = new NamedPipeClientStream(".", PIPE, PipeDirection.InOut, PipeOptions.None);
            _pipe.Connect(500);
        }

        private void SendPacket(object obj, RpcPacketType opcode = RpcPacketType.FRAME)
        {
            string stringData = JsonConvert.SerializeObject(obj, _jsonSerializerSettings);
            byte[] data = Encoding.UTF8.GetBytes(stringData);
            int dataLength = data.Length;
            byte[] sendBuff = new byte[dataLength + 8];
            BinaryWriter writer = new BinaryWriter(new MemoryStream(sendBuff));
            writer.Write((int)opcode);
            writer.Write(dataLength);
            writer.Write(data);
            _pipe.Write(sendBuff);
        }

        private void StartReceive()
        {
            while (!_cancellationToken.IsCancellationRequested)
            {
                byte[] buffer = new byte[8192];
                _pipe.Read(buffer, 0, buffer.Length);
                BinaryReader reader = new BinaryReader(new MemoryStream(buffer));
                RpcPacketType opCode = (RpcPacketType)reader.ReadInt32();
                int dataLength = reader.ReadInt32();
                string data = Encoding.UTF8.GetString(reader.ReadBytes(dataLength));

                OnMessageReceived(opCode, data);
            }
        }

        private void OnMessageReceived(RpcPacketType opCode, string data)
        {
            if (opCode == RpcPacketType.PING)
            {
                SendPacket(data, RpcPacketType.PONG);
                return;
            }

            IDiscordMessage discordMessage;
            try
            {
                discordMessage = JsonConvert.DeserializeObject<IDiscordMessage>(data, _jsonSerializerSettings);
            }
            catch (Exception exc)
            {
                _logger.Error(exc, $"Error deserializing discord message");
                return;
            }

            if (discordMessage is DiscordResponse discordResponse)
                ProcessDiscordResponse(discordResponse);
            else if (discordMessage is DiscordEvent discordEvent)
                ProcessDiscordEvent(discordEvent);
        }
        #endregion

        #region Message handling
        private void ProcessDiscordEvent(DiscordEvent discordEvent)
        {
            switch (discordEvent)
            {
                case ReadyDiscordEvent:
                    if (token.Value == null)
                    {
                        //We have no token saved. This means it's probably the first time
                        //the user is using the plugin. We need to ask for their permission
                        //to get a token from discord. 
                        //This token can be saved and reused (+ refreshed) later.
                        SendPacket(new DiscordRequest(DiscordRpcCommand.AUTHORIZE)
                            .WithArgument("client_id", clientId.Value)
                            .WithArgument("scopes", _scopes));
                    }
                    else
                    {
                        //Ff we already have a token saved from earlier,
                        //we need to check if it expired or not.
                        //If yes, refresh it.
                        //Then, authenticate.
                        if (token.Value.ExpirationDate > DateTime.UtcNow)
                        {
                            TokenResponse tokenResponse = RefreshAccessTokenAsync(token.Value.RefreshToken).Result;
                            SaveToken(tokenResponse);
                        }

                        SendPacket(new DiscordRequest(DiscordRpcCommand.AUTHENTICATE)
                                        .WithArgument("access_token", token.Value.AccessToken));
                    }
                    break;
                case VoiceSettingsUpdateDiscordEvent voice:
                    DataModel.VoiceSettings.Deafened = voice.Data.Deaf;
                    DataModel.VoiceSettings.Muted = voice.Data.Mute;
                    break;
                case VoiceConnectionStatusDiscordEvent voiceStatus:
                    DataModel.VoiceConnection.State = voiceStatus.Data.State;
                    DataModel.VoiceConnection.Ping = voiceStatus.Data.LastPing;
                    break;
                case NotificationCreateDiscordEvent:
                    DataModel.Notification.Trigger();
                    break;
                case SpeakingStopDiscordEvent speakingStop:
                    if (speakingStop.Data.UserId == DataModel.User.Id)
                    {
                        DataModel.VoiceSettings.Speaking = false;
                    }
                    break;
                case SpeakingStartDiscordEvent speakingStart:
                    if (speakingStart.Data.UserId == DataModel.User.Id)
                    {
                        DataModel.VoiceSettings.Speaking = true;
                    }
                    break;
                case VoiceChannelSelectDiscordEvent voiceSelect:
                    if (voiceSelect.Data.ChannelId is not null)//join voice channel
                    {
                        SubscribeToSpeakingEvents(voiceSelect.Data.ChannelId);
                    }
                    else//leave voice channel
                    {
                    }
                    break;
                default:
                    break;
            }
        }

        private void ProcessDiscordResponse(DiscordResponse discordResponse)
        {
            switch (discordResponse)
            {
                //we should only receive the authorize event once from the client
                //since after that the token should be refreshed
                case AuthorizeDiscordResponse authorize:
                    //If we get here, it means it's the first time the user is using the plugin.
                    //In this case, we need to ask for their permission for this app to be used
                    //so we can get a token with the Code discord gives us. 
                    //This token can then be reused.

                    TokenResponse tokenResponse = GetAccessTokenAsync(authorize.Data.Code).Result;
                    SaveToken(tokenResponse);
                    SendPacket(new DiscordRequest(DiscordRpcCommand.AUTHENTICATE).WithArgument("access_token", token.Value.AccessToken));
                    break;
                case AuthenticateDiscordResponse authenticate:
                    DataModel.User.Username = authenticate.Data.User.Username;
                    DataModel.User.Discriminator = authenticate.Data.User.Discriminator;
                    DataModel.User.Id = authenticate.Data.User.Id;

                    //Initial request for data, then use events after
                    SendPacket(new DiscordRequest(DiscordRpcCommand.GET_VOICE_SETTINGS));
                    SendPacket(new DiscordRequest(DiscordRpcCommand.GET_SELECTED_VOICE_CHANNEL));

                    //Subscribe to these events as well
                    SendPacket(new DiscordSubscribe(DiscordRpcEvent.VOICE_SETTINGS_UPDATE));
                    SendPacket(new DiscordSubscribe(DiscordRpcEvent.NOTIFICATION_CREATE));
                    SendPacket(new DiscordSubscribe(DiscordRpcEvent.VOICE_CONNECTION_STATUS));
                    SendPacket(new DiscordSubscribe(DiscordRpcEvent.VOICE_CHANNEL_SELECT));
                    break;
                case VoiceSettingsDiscordResponse voice:
                    DataModel.VoiceSettings.Deafened = voice.Data.Deaf;
                    DataModel.VoiceSettings.Muted = voice.Data.Mute;
                    break;
                case SubscribeDiscordResponse subscribe:
                    _logger.Verbose($"Subscribed to event {subscribe.Data.Event} successfully.");
                    break;

                case SelectedVoiceChannelDiscordResponse selectedVoiceChannel:
                    //Data is null when the user leaves a voice channel
                    if (selectedVoiceChannel.Data != null)
                        SubscribeToSpeakingEvents(selectedVoiceChannel.Data.Id);
                    break;
                default:
                    break;
            }
        }

        private void SubscribeToSpeakingEvents(string id)
        {
            SendPacket(new DiscordSubscribe(DiscordRpcEvent.SPEAKING_START).WithArgument("channel_id", id));
            SendPacket(new DiscordSubscribe(DiscordRpcEvent.SPEAKING_STOP).WithArgument("channel_id", id));
        }
        #endregion

        #region Authorization & Authentication
        private void SaveToken(TokenResponse newToken)
        {
            token.Value = new SavedToken
            {
                AccessToken = newToken.AccessToken,
                RefreshToken = newToken.RefreshToken,
                ExpirationDate = DateTime.UtcNow.AddSeconds(newToken.ExpiresIn)
            };
            token.Save();
        }

        private async Task<TokenResponse> GetAccessTokenAsync(string challengeCode)
        {
            return await GetCredentials("authorization_code", "code", challengeCode);
        }

        private async Task<TokenResponse> RefreshAccessTokenAsync(string refreshToken)
        {
            return await GetCredentials("refresh_token", "refresh_token", refreshToken);
        }

        private async Task<TokenResponse> GetCredentials(string grantType, string secretType, string secret)
        {
            Dictionary<string, string> values = new Dictionary<string, string>
            {
                ["grant_type"] = grantType,
                [secretType] = secret,
                ["client_id"] = clientId.Value,
                ["client_secret"] = clientSecret.Value
            };

            using HttpResponseMessage response = await client.PostAsync("https://discord.com/api/oauth2/token", new FormUrlEncodedContent(values));
            string responseString = await response.Content.ReadAsStringAsync();
            return JsonConvert.DeserializeObject<TokenResponse>(responseString, _jsonSerializerSettings);
        }
        #endregion
    }
}