﻿// --------------------------------------------------------------------------------------------------------------------
// <copyright file="SparkplugApplication.cs" company="Hämmer Electronics">
// The project is licensed under the MIT license.
// </copyright>
// <summary>
//   A class that handles a Sparkplug application.
// </summary>
// --------------------------------------------------------------------------------------------------------------------

namespace SparkplugNet.Application
{
    using System.Collections.Concurrent;
    using System.Threading;
    using System.Threading.Tasks;

    using MQTTnet;
    using MQTTnet.Client;
    using MQTTnet.Client.Options;
    using MQTTnet.Formatter;
    using MQTTnet.Protocol;

    using SparkplugNet.Enumerations;
    using SparkplugNet.Extensions;

    using VersionAPayload = Payloads.VersionA.Payload;
    using VersionBPayload = Payloads.VersionB.Payload;

    /// <inheritdoc cref="SparkplugBase"/>
    /// <summary>
    /// A class that handles a Sparkplug application.
    /// </summary>
    /// <seealso cref="SparkplugBase"/>
    public class SparkplugApplication : SparkplugBase
    {
        /// <summary>
        /// The will message.
        /// </summary>
        private MqttApplicationMessage? willMessage;

        /// <summary>
        /// The application online message.
        /// </summary>
        private MqttApplicationMessage? applicationOnlineMessage;

        /// <inheritdoc cref="SparkplugBase"/>
        /// <summary>
        /// Initializes a new instance of the <see cref="SparkplugApplication"/> class.
        /// </summary>
        /// <param name="nameSpace">The namespace.</param>
        /// <seealso cref="SparkplugBase"/>
        public SparkplugApplication(SparkplugNamespace nameSpace) : base(nameSpace)
        {
        }

        /// <summary>
        /// Gets the node states for the payload version A.
        /// </summary>
        public ConcurrentDictionary<string, VersionAPayload.KuraMetric> NodeStatesPayloadA { get; } = new ();

        /// <summary>
        /// Gets the node states for the payload version B.
        /// </summary>
        public ConcurrentDictionary<string, VersionBPayload.Metric> NodeStatesPayloadB { get; } = new ();

        /// <summary>
        /// Starts the Sparkplug application.
        /// </summary>
        /// <param name="options">The configuration option.</param>
        /// <returns>A <see cref="Task"/> representing any asynchronous operation.</returns>
        public async Task Start(SparkplugApplicationOptions options)
        {
            // Clear states
            this.NodeStatesPayloadA.Clear();
            this.NodeStatesPayloadB.Clear();

            // Load messages
            this.LoadMessages(options);

            // Add handlers
            this.AddDisconnectedHandler(options);
            this.AddMessageReceivedHandler();

            // Connect, subscribe to incoming messages and send a state message
            await this.ConnectInternal(options);
            await this.SubscribeInternal();
            await this.PublishInternal(options);
        }

        /// <summary>
        /// Stops the Sparkplug application.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing any asynchronous operation.</returns>
        public async Task Stop()
        {
            await this.Client.DisconnectAsync();
        }

        /// <summary>
        /// Loads the messages used by the the Sparkplug application.
        /// </summary>
        /// <param name="options">The configuration option.</param>
        private void LoadMessages(SparkplugApplicationOptions options)
        {
            this.willMessage = this.MessageGenerator.GetSparkplugStateMessage(
                this.NameSpace,
                options.ScadaHostIdentifier,
                false);

            this.applicationOnlineMessage = this.MessageGenerator.GetSparkplugStateMessage(
                this.NameSpace,
                options.ScadaHostIdentifier,
                true);
        }

        /// <summary>
        /// Adds the disconnected handler and the reconnect functionality to the client.
        /// </summary>
        /// <param name="options">The configuration option.</param>
        private void AddDisconnectedHandler(SparkplugApplicationOptions options)
        {
            this.Client.UseDisconnectedHandler(
                async e =>
                    {
                        // Todo: Use the metrics correctly.
                        //// Set all states to unknown as we are disconnected
                        //foreach (var nodeState in this.NodeStatesPayloadA)
                        //{
                        //    var value = this.NodeStatesPayloadA[nodeState.Key];
                        //    value.ConnectionStatus = SparkplugConnectionStatus.Unknown;
                        //    this.NodeStatesPayloadA[nodeState.Key] = value;
                        //}

                        //// Set all states to unknown as we are disconnected
                        //foreach (var nodeState in this.NodeStatesPayloadB)
                        //{
                        //    var value = this.NodeStatesPayloadB[nodeState.Key];
                        //    value.ConnectionStatus = SparkplugConnectionStatus.Unknown;
                        //    this.NodeStatesPayloadB[nodeState.Key] = value;
                        //}

                        // Wait until the disconnect interval is reached
                        await Task.Delay(options.ReconnectInterval);

                        // Connect, subscribe to incoming messages and send a state message
                        await this.ConnectInternal(options);
                        await this.SubscribeInternal();
                        await this.PublishInternal(options);
                    });
        }

        /// <summary>
        /// Adds the message received handler to handle incoming messages.
        /// </summary>
        private void AddMessageReceivedHandler()
        {
            this.Client.UseApplicationMessageReceivedHandler(
                e =>
                    {
                        var topic = e.ApplicationMessage.Topic;

                        var needsPayloadHanding = topic.Contains(SparkplugMessageType.NodeBirth.GetDescription())
                                                  || topic.Contains(SparkplugMessageType.NodeDeath.GetDescription())
                                                  || topic.Contains(SparkplugMessageType.DeviceBirth.GetDescription())
                                                  || topic.Contains(SparkplugMessageType.DeviceDeath.GetDescription())
                                                  || topic.Contains(SparkplugMessageType.NodeData.GetDescription())
                                                  || topic.Contains(SparkplugMessageType.DeviceData.GetDescription())
                                                  || topic.Contains(SparkplugMessageType.NodeCommand.GetDescription())
                                                  || topic.Contains(SparkplugMessageType.DeviceCommand.GetDescription());

                        if (needsPayloadHanding)
                        {
                            switch (this.NameSpace)
                            {
                                case SparkplugNamespace.VersionA:
                                    var payloadVersionA = PayloadHelper.Deserialize<VersionAPayload>(e.ApplicationMessage.Payload);

                                    if (payloadVersionA != null)
                                    {
                                        this.VersionAPayloadReceived?.Invoke(payloadVersionA);
                                    }

                                    break;

                                case SparkplugNamespace.VersionB:
                                    var payloadVersionB = PayloadHelper.Deserialize<VersionBPayload>(e.ApplicationMessage.Payload);

                                    if (payloadVersionB != null)
                                    {
                                        this.VersionBPayloadReceived?.Invoke(payloadVersionB);
                                    }

                                    break;
                            }
                        }
                    });
        }

        /// <summary>
        /// Connects the Sparkplug application to the MQTT broker.
        /// </summary>
        /// <param name="options">The configuration option.</param>
        /// <returns>A <see cref="Task"/> representing any asynchronous operation.</returns>
        private async Task ConnectInternal(SparkplugApplicationOptions options)
        {
            options.CancellationToken ??= CancellationToken.None;

            var builder = new MqttClientOptionsBuilder()
                .WithClientId(options.ClientId)
                .WithCredentials(options.UserName, options.Password)
                .WithCleanSession(false)
                .WithProtocolVersion(MqttProtocolVersion.V311);

            if (options.UseTls)
            {
                builder.WithTls();
            }

            if (options.WebSocketParameters is null)
            {
                builder.WithTcpServer(options.BrokerAddress, options.Port);
            }
            else
            {
                builder.WithWebSocketServer(options.BrokerAddress, options.WebSocketParameters);
            }

            if (options.ProxyOptions != null)
            {
                builder.WithProxy(
                    options.ProxyOptions.Address,
                    options.ProxyOptions.Username,
                    options.ProxyOptions.Password,
                    options.ProxyOptions.Domain,
                    options.ProxyOptions.BypassOnLocal);
            }

            if (this.willMessage != null && options.IsPrimaryApplication)
            {
                builder.WithWillMessage(this.willMessage);
            }

            this.ClientOptions = builder.Build();

            await this.Client.ConnectAsync(this.ClientOptions, options.CancellationToken.Value);
        }

        /// <summary>
        /// Publishes data to the MQTT broker.
        /// </summary>
        /// <param name="options">The configuration option.</param>
        /// <returns>A <see cref="Task"/> representing any asynchronous operation.</returns>
        private async Task PublishInternal(SparkplugApplicationOptions options)
        {
            // Only send state messages for the primary application
            if (options.IsPrimaryApplication)
            {
                options.CancellationToken ??= CancellationToken.None;
                await this.Client.PublishAsync(this.applicationOnlineMessage, options.CancellationToken.Value);
            }
        }

        /// <summary>
        /// Subscribes the client to the application subscribe topic.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing any asynchronous operation.</returns>
        private async Task SubscribeInternal()
        {
            var topic = this.TopicGenerator.GetWildcardNamespaceSubscribeTopic(this.NameSpace);
            await this.Client.SubscribeAsync(topic, MqttQualityOfServiceLevel.AtLeastOnce);
        }
    }
}