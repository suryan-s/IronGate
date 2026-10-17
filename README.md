# IronGate

A high-performance, distributed **API Gateway** and **L7 (Application Layer) Traffic Controller** built on .NET.

IronGate sits in front of your services as the single ingress point: it routes, throttles, authenticates, and shapes traffic at the application layer, then fans requests out to your upstream pool — horizontally scalable across nodes with no single point of failure.

## Why

- **Throughput over ceremony.** Built on .NET's async networking stack and tuned for high concurrent-connection counts.
- **L7 awareness.** Routing decisions are made on method, path, headers, and body — not just IP and port.
- **Control plane.** Policies are declared as configuration and distributed to the data plane, so scaling out is a config change, not a code change.
- **Open by default.** No proprietary control plane, no vendor lock-in.

## Status

Early development. See [Roadmap](#roadmap).

## Tech

| Layer | Choice |
| --- | --- |
| Runtime | .NET (latest LTS) |
| Language | C# |
| Transport | HTTP / HTTPS, HTTP/2, WebSocket, gRPC |

## Building

Requires the [.NET SDK](https://dotnet.microsoft.com/download).

```bash
dotnet build
dotnet test
```

## Usage

```bash
dotnet run --project src/IronGate
```

## Contributing

Issues and pull requests are welcome. For larger changes, open an issue first so we can agree on the design.

## License

[MIT](LICENSE)

## Roadmap

- [ ] L7 routing: path, method, header, and content matching
- [ ] Rate limiting and traffic shaping
- [ ] Distributed config / control plane
- [ ] Load balancing and health checks across upstream pools
- [ ] Observability: metrics, structured logs, tracing
- [ ] Clustering and leader election