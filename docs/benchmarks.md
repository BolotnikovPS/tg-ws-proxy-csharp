# Бенчмарки горячих путей

Микробенчмарки через BenchmarkDotNet. Проект: `TgWsProxy.Benchmarks`.
Запуск: `dotnet run -c Release --project TgWsProxy.Benchmarks [--filter "*Crypto*"]`

## Baseline — net8.0

Окружение: .NET SDK 11.0.100-rc.2, Runtime .NET 8.0.32, X64 RyuJIT AVX2,
GC = Concurrent Workstation, Windows 11.

| Benchmark | Param | Mean | Allocated |
|-----------|-------|-----:|----------:|
| CryptoBenchmarks.UpdateInto (AES-CTR) | 1024 B | 8.791 us | 0 B |
| CryptoBenchmarks.UpdateInto (AES-CTR) | 8192 B | 58.093 us | 0 B |
| MsgSplitterBenchmarks.Split | 1290 B (10 пакетов) | 305.7 ns | 552 B |
| MsgSplitterBenchmarks.Split | 12900 B (100 пакетов) | 2697.3 ns | 4208 B |
| PeerStatsBenchmarks.Snapshot | 1000 peers | 242.6 us | 156.38 KB |
| PeerStatsBenchmarks.Snapshot | 10000 peers | 3180.8 us | 1562.63 KB |
| RawWebSocketBenchmarks.Send | 256 B | 277.7 ns | 0 B |
| RawWebSocketBenchmarks.Send | 4096 B | 509.4 ns | 0 B |

### Выводы baseline

- **Crypto (IncrementalCipher.UpdateInto)** — 0 аллокаций, ~141 GB/s на 8 КБ
  (аппаратный AES-NI), оптимизировать нечего.
- **RawWebSocket.Send** — 0 аллокаций (ReadOnlyMemory + ArrayPool + векторный XOR),
  цель рефакторинга P1 достигнута.
- **MsgSplitter.Split** — остаточная аллокация 552 B (10 пакетов) / 4208 B (100)
  — это `List<ReadOnlyMemory<byte>>` результата (16 B × пакет + список). Мала,
  но на пути каждого чанка; кандидат на точечную оптимизацию (переиспользуемый буфер).
- **PeerStatsRegistry.Snapshot** — ~156 B/peer. При 100k peers ≈ 15 MB на снимок,
  а `/metrics` scrape и `/api/per-ip` (панель, POLL_MS=2000) тянут его регулярно.
  Смягчается кэшем `PeerStatsSnapshotCache` (TTL 1 s), но это главный источник GC-нагрузки
  в observability-пути.

## Сравнение net8.0 → net11.0

(заполняется после миграции)
