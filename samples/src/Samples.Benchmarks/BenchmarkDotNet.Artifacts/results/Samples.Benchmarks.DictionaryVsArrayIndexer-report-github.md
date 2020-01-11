``` ini

BenchmarkDotNet=v0.12.0, OS=macOS 10.15.2 (19C57) [Darwin 19.2.0]
Intel Core i7-3740QM CPU 2.70GHz (Ivy Bridge), 1 CPU, 8 logical and 4 physical cores
.NET Core SDK=3.1.100
  [Host]     : .NET Core 3.1.0 (CoreCLR 4.700.19.56402, CoreFX 4.700.19.56404), X64 RyuJIT
  DefaultJob : .NET Core 3.1.0 (CoreCLR 4.700.19.56402, CoreFX 4.700.19.56404), X64 RyuJIT


```
|           Method |       Mean |    Error |   StdDev | Ratio |
|----------------- |-----------:|---------:|---------:|------:|
| DictionaryLookup | 2,189.1 ns | 22.97 ns | 20.36 ns |  1.00 |
|     ArrayIndexer |   121.4 ns |  1.59 ns |  1.49 ns |  0.06 |
