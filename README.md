# wSocket .NET SDK

Official C# / .NET SDK for [wSocket](https://wsocket.io) — Realtime Pub/Sub over WebSockets.

[![NuGet](https://img.shields.io/nuget/v/wSocket)](https://www.nuget.org/packages/wSocket)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

## Installation

```bash
dotnet add package wSocket
```

## Quick Start

```csharp
using wSocket;

var client = new Client("wss://your-server.com", "your-api-key");
await client.ConnectAsync();

var chat = client.Channel("chat:general");
chat.Subscribe((data, meta) => {
    Console.WriteLine($"[{meta.Channel}] {data}");
});

chat.Publish(new { text = "Hello from C#!" });

await Task.Delay(-1); // keep alive
```

## Features

- **Pub/Sub** — Subscribe and publish to channels in real-time
- **Presence** — Track who is online in a channel
- **History** — Retrieve past messages
- **Connection Recovery** — Automatic reconnection with message replay
- **Async/Await** — Built on `System.Net.WebSockets`

## Presence

```csharp
var chat = client.Channel("chat:general");

chat.Presence.OnEnter(m => Console.WriteLine($"Joined: {m.ClientId}"));
chat.Presence.OnLeave(m => Console.WriteLine($"Left: {m.ClientId}"));
chat.Presence.Enter(new { name = "Alice" });

var members = chat.Presence.Get();
```

## History

```csharp
chat.OnHistory(result => {
    foreach (var msg in result.Messages) {
        Console.WriteLine($"[{msg.Timestamp}] {msg.Data}");
    }
});

chat.History(new HistoryOptions { Limit = 50 });
```

## Requirements

- .NET 8.0+
- No external dependencies

## Development

```bash
dotnet build
dotnet test
```

## License

MIT
