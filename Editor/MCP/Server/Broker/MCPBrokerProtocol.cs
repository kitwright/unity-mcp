// Copyright (C) KitWright. Licensed under MIT.

namespace KitWright.Editor.MCP.Server
{
    internal static class MCPBrokerProtocol
    {
        // v2: pull responses carry AcceptSseHeader (client's Accept: text/event-stream),
        //     push requests may carry ContentTypeHeader to override the client-facing
        //     response content type (used for SSE-piggybacked notifications).
        // v3: the broker checks the project pin in the request path -- a v2 broker left running
        //     would keep forwarding another project's requests, so the bump forces it to be
        //     replaced; and a tools/call that arrives while no Unity backend is attached comes
        //     back as an ordinary tool result telling the agent to retry, not a JSON-RPC error.
        // v4: pull responses carry ClientPortHeader -- the connecting client's TCP port, which the
        //     editor turns into the owning pid for its first-connect approval gate. A v3 broker
        //     would forward requests with no way to identify the client, so the bump replaces it.
        // v5: the client's Mcp-Session-Id crosses the broker in both directions (McpSessionHeader).
        //     A v4 broker dropped it, so every broker client shared one sessionless slot in the
        //     editor: whatever revision the last client negotiated applied to all of them, and a
        //     dead session id was served as if it were live. The bump replaces such a broker.
        // v6: pushes may carry StatusHeader, the HTTP status the broker gives the client. A dead
        //     Mcp-Session-Id has to come back as 404 -- that is the status the MCP spec makes a
        //     client re-initialize on, and it is what the direct transport already answers. A v5
        //     broker always wrote 200, so the refusal arrived as a JSON-RPC error the client had no
        //     rule for: it kept the dead id and every later call failed while the connection still
        //     looked healthy. The bump replaces such a broker.
        public const int Version = 6;
        public const string Name = "kitwright-unity-mcp-broker";
        public const string HealthPath = "/_kitwright/broker/health";
        public const string AttachPath = "/_kitwright/broker/attach";
        public const string PullPath = "/_kitwright/broker/pull";
        public const string PushPath = "/_kitwright/broker/push";
        public const string DetachPath = "/_kitwright/broker/detach";
        public const string ShutdownPath = "/_kitwright/broker/shutdown";
        public const string TokenHeader = "X-KitWright-Broker-Token";
        public const string SessionHeader = "X-KitWright-Broker-Session";
        public const string ReqIdHeader = "X-KitWright-Broker-ReqId";
        public const string RedeliveryHeader = "X-KitWright-Broker-Redelivery";
        public const string BrokerHeader = "X-KitWright-Broker";
        public const string AcceptSseHeader = "X-KitWright-Broker-Accept-SSE";
        public const string ContentTypeHeader = "X-KitWright-Broker-Content-Type";
        public const string ClientPortHeader = "X-KitWright-Broker-Client-Port";
        public const string McpSessionHeader = "X-KitWright-Broker-Mcp-Session";
        public const string StatusHeader = "X-KitWright-Broker-Status";
    }
}
