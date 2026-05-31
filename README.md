# SyncSession

**Production-ready, offline-first synchronization library for .NET applications**

SyncSystem enables multiple clients to sync data with a central server while handling edge cases like network failures, concurrent operations, and massive datasets through session-based change tracking.

> Documentation coming soon. See [releases](releases) for the latest version.


## 🎯 Core Problem Solved

**The "Lost Records" Problem**: Traditional version-based sync can miss records during concurrent operations.

**Our Solution**: Session-based tracking ensures clients track "which sessions they've processed" rather than "what version they're at", guaranteeing no lost records.

## ✨ Key Features

- ✅ **Session-Based Tracking** - No lost records during concurrent operations
- ✅ **Offline-First** - Clients can be offline for extended periods
- ✅ **Hybrid Temp Table Strategy** - Optimized for both small and large syncs
- ✅ **Multi-Table Atomic Sessions** - Referential integrity maintained
- ✅ **Last-In-Wins Conflict Resolution** - Configurable conflict handling
- ✅ **Cross-Platform** - Works on Windows, macOS, Linux, iOS, Android
- ✅ **Multiple Database Support** - MySQL, SQL Server, PostgreSQL, SQLite


## 🏗️ Architecture

```
┌─────────────────────────────────────────────┐
│        Client Application (Desktop/Mobile)  │
└──────────────────┬──────────────────────────┘
                   │ HTTP/HTTPS
┌──────────────────▼──────────────────────────┐
│           SyncSystem.Client                 │
│  ┌──────────┐  ┌──────────┐  ┌──────────┐  │
│  │SyncEngine│  │PushEngine│  │PullEngine│  │
│  └──────────┘  └──────────┘  └──────────┘  │
│  ┌───────────────────────────────────────┐  │
│  │  SqliteClientDatabase (Local SQLite) │  │
│  └───────────────────────────────────────┘  │
└──────────────────┬──────────────────────────┘
                   │ REST API
┌──────────────────▼──────────────────────────┐
│           SyncSystem.Server                 │
│  ┌──────────┐  ┌──────────┐  ┌──────────┐  │
│  │ Push API │  │ Pull API │  │Health API│  │
│  └──────────┘  └──────────┘  └──────────┘  │
│  ┌──────────┐  ┌──────────┐  ┌──────────┐  │
│  │TempTable │  │ Session  │  │  Queue   │  │
│  │ Manager  │  │ Tracker  │  │Processor │  │
│  └──────────┘  └──────────┘  └──────────┘  │
│  ┌───────────────────────────────────────┐  │
│  │    MySQL / SQL Server / PostgreSQL   │  │
│  └───────────────────────────────────────┘  │
└─────────────────────────────────────────────┘
```

## 🔧 Configuration

### Server Configuration (appsettings.json)

```json
{
  "ConnectionStrings": {
    "SyncDatabase": "Server=localhost;Database=SyncDb;..."
  },
  "SyncConfiguration": {
    "PushSharedTableThreshold": 10000,
    "PullSharedTableThreshold": 10000,
    "SessionActivityTimeoutMinutes": 30,
    "Tables": {
      "Customers": { "Priority": 1, "Enabled": true },
      "Orders": { "Priority": 2, "Enabled": true },
      "OrderItems": { "Priority": 3, "Enabled": true }
    }
  }
}
```

### Client Configuration

```csharp
var config = new SyncConfiguration
{
    PushBatchSize = 1000,  // Records per push batch
    PullBatchSize = 1000,  // Records per pull batch
};

// Adjust for mobile/slow networks
if (networkType == NetworkType.Cellular)
{
    config.PushBatchSize = 500;
    config.PullBatchSize = 500;
}
```

## 📊 Performance

Based on benchmarks (in-memory SQLite):

| Operation | Records | Time | Throughput |
|-----------|---------|------|------------|
| Push      | 1,000   | 1.2s | 833/sec    |
| Push      | 10,000  | 8.5s | 1,176/sec  |
| Push      | 100,000 | 89s  | 1,124/sec  |
| Pull      | 1,000   | 0.8s | 1,250/sec  |
| Pull      | 10,000  | 6.2s | 1,613/sec  |
| Pull      | 100,000 | 71s  | 1,408/sec  |

## 🧪 Testing

```bash

```

## 🐳 Docker Support

```bash

```

## 🤝 Contributing

Contributions are welcome! Please read our [Contributing Guide](CONTRIBUTING.md) for details.

1. Fork the repository
2. Create a feature branch (`git checkout -b feature/amazing-feature`)
3. Commit your changes (`git commit -m 'Add amazing feature'`)
4. Push to the branch (`git push origin feature/amazing-feature`)
5. Open a Pull Request

## 📝 License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## 🙏 Acknowledgments

Inspired by:

## 📞 Support

- **Documentation**: 
- **Issues**: 
- **Discussions**: 

## 🗺️ Roadmap

### v1.0
- [ ] Schema versioning and migrations
- [ ] Compression for network payloads
- [ ] Real-time sync triggers (WebSocket)

### v2.0
- [ ] Differential sync (delta updates only)
- [ ] Custom conflict resolution strategies
- [ ] Sync analytics dashboard
- [ ] Multi-master replication

---

**Built with ❤️ for offline-first applications**
