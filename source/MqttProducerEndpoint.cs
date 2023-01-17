﻿// (c) Cloud Native Computing Foundation. See LICENSE for details

using CloudNative.CloudEvents.Mqtt;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Client.Options;

namespace CloudNative.CloudEvents.Endpoints
{
    class MqttProducerEndpoint : ProducerEndpoint
    {
        private readonly ILogger _logger;
        private readonly IEndpointCredential _credential;
        private List<Uri> _endpoints;
        private Dictionary<Uri, IMqttClient> endpointConnections = new();
        private string _topic;
        private int _qos;

        public MqttProducerEndpoint(ILogger logger, IEndpointCredential credential, Dictionary<string, string> options, List<Uri> endpoints)
        {
            if ( !options.TryGetValue("topic", out var topic) )
            {
                throw new ArgumentException("topic is required");
            }
            _topic = topic;
            
            this._qos = 1;
            if (options.TryGetValue("qos", out var qos))
            {
                int.TryParse(qos, out this._qos);
            }
            this._logger = logger;
            this._credential = credential;
            this._endpoints = endpoints;
        }

        public override async Task SendAsync(CloudEvent cloudEvent, ContentMode contentMode, CloudEventFormatter formatter)
        {
            _logger.LogInformation("Sending message to endpoints");
            try
            {
                foreach (var endpoint in _endpoints)
                {
                    var connection = await GetEndpointConnectionAsync(endpoint);
                    var message = cloudEvent.ToMqttApplicationMessage(contentMode, formatter, _topic);
                    message.Topic = _topic;
                    message.QualityOfServiceLevel = (MQTTnet.Protocol.MqttQualityOfServiceLevel)_qos;
                    try
                    {
                        await connection.PublishAsync(message);
                    }
                    catch
                    {
                        _logger.LogError("Error publishing message to endpoint {endpoint}. Removing endpoint from connections", endpoint);
                        endpointConnections.Remove(endpoint);
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending message to endpoints");
            }
            _logger.LogInformation("Message sent to all endpoints");
        }


        private async Task<IMqttClient> GetEndpointConnectionAsync(Uri endpoint)
        {
            if (endpointConnections.TryGetValue(endpoint, out var connection))
            {
                return connection;
            }

            var mqttClient = new MqttFactory().CreateMqttClient();
            var options = new MqttClientOptionsBuilder()
               .WithClientId(Guid.NewGuid().ToString())
               .WithTcpServer(endpoint.Host, endpoint.Port)
               .WithCleanSession();
            if (_credential is IPlainEndpointCredential plainCredential)
            {
                options = options.WithCredentials(plainCredential.ClientId, plainCredential.ClientSecret);
            }
            if (endpoint.Scheme == "mqtts" )
            {
                options = options.WithTls(new MqttClientOptionsBuilderTlsParameters());
            }
            await mqttClient.ConnectAsync(options.Build(), CancellationToken.None);
            endpointConnections.Add(endpoint, mqttClient);
            return mqttClient;             
        }
    }
}