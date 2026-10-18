using BenchmarkDotNet.Running;

// Запуск: dotnet run -c Release --project benchmarks/TgWsProxy.Benchmarks [--filter "*Crypto*"]
// Каждый бенчмарк-класс можно запускать отдельно через --filter.
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
