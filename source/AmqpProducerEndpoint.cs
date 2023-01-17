﻿// (c) Cloud Native Computing Foundation. See LICENSE for details

using CloudNative.CloudEvents.Amqp;
using Amqp;
using Amqp.Sasl;
using Microsoft.Extensions.Logging;

namespace CloudNative.CloudEvents.Endpoints
{
    class AmqpProducerEndpoint : ProducerEndpoint
    {
        private readonly ILogger _logger;
        private readonly IEndpointCredential _credential;
        private List<Uri> _endpoints;
        private Dictionary<Uri, Tuple<Connection, Session, SenderLink>> endpointConnections = new();
        private string? _node;

        public AmqpProducerEndpoint(ILogger logger, IEndpointCredential credential, Dictionary<string, string> options, List<Uri> endpoints)
        {
            this._logger = logger;
            this._credential = credential;
            this._endpoints = endpoints;
            if (options.TryGetValue("node", out var node))
            {
                _node = node;
            }
        }

        public override async Task SendAsync(CloudEvent cloudEvent, ContentMode contentMode, CloudEventFormatter formatter)
        {
            foreach (var endpoint in _endpoints)
            {
                var connectionTuple = await GetEndpointConnectionAsync(endpoint);
                var sender = connectionTuple.Item3;
                try
                {
                    await sender.SendAsync(cloudEvent.ToAmqpMessage(contentMode, formatter));
                }
                catch (AmqpException ex)
                {
                    _logger.LogError("Error sending message to endpoint " + endpoint + ": " + ex.Message);
                    _endpoints.Remove(endpoint);
                    throw;
                }
            }
        }

        private async Task<Tuple<Connection, Session, SenderLink>> GetEndpointConnectionAsync(Uri endpoint)
        {
            try
            {
                if (endpointConnections.TryGetValue(endpoint, out var connectionTuple))
                {
                    return connectionTuple;
                }

                Address address = new Address(
                   endpoint.Host,
                   endpoint.Port == -1 ? endpoint.Scheme == "amqps" ? 5671 : 5672 : endpoint.Port,
                   path: _node != null ? _node : endpoint.AbsolutePath, scheme: endpoint.Scheme.ToUpper(),
                   user: (_credential as IPlainEndpointCredential)?.ClientId,
                   password: (_credential as IPlainEndpointCredential)?.ClientSecret);

                ConnectionFactory factory = new ConnectionFactory();
                if (_credential is ITokenEndpointCredential tokenCredential)
                {
                    factory.SASL.Profile = SaslProfile.Anonymous;
                }

                var connection = await factory.CreateAsync(address);
                var session = new Session(connection);
                if (_credential is ITokenEndpointCredential)
                {
                    var token = ((ITokenEndpointCredential)_credential).GetTokenAsync().Result;
                    var cbsSender = new SenderLink(session, "$cbs", "$cbs");
                    var request = new global::Amqp.Message(token);
                    request.Properties.MessageId = Guid.NewGuid().ToString();
                    request.ApplicationProperties["operation"] = "put-token";
                    request.ApplicationProperties["type"] = "amqp:jwt";
                    request.ApplicationProperties["name"] = string.Format("amqp://{0}/{1}", address.Host, address.Path);
                    await cbsSender.SendAsync(request);
                    await cbsSender.CloseAsync();
                }
                var sender = new SenderLink(session, "sender-link", endpoint.PathAndQuery);
                connectionTuple = new Tuple<Connection, Session, SenderLink>(connection, session, sender);
                endpointConnections.Add(endpoint, connectionTuple);
                return connectionTuple;
            }
            catch( Exception ex)
            {
                _logger.LogError("Error establishing connection to endpoint " + endpoint + ": " + ex.Message);
                _endpoints.Remove(endpoint);
                throw;
            }
        }
    }
}