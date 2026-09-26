```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9445/25H2/2025Update/HudsonValley2)
Intel Core Ultra 7 255H 2.00GHz, 1 CPU, 16 logical and 16 physical cores
.NET SDK 10.0.401
  [Host]    : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3
  .NET 10.0 : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3

Job=.NET 10.0  Runtime=.NET 10.0  

```
| Method                     | Key                  | Mean       | Error     | StdDev    | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|--------------------------- |--------------------- |-----------:|----------:|----------:|------:|--------:|-------:|----------:|------------:|
| **&#39;legacy Split + recursion&#39;** | **finalize**             |   **3.086 ns** | **0.0972 ns** | **0.1040 ns** |  **1.00** |    **0.05** |      **-** |         **-** |          **NA** |
| &#39;span walk (shipped)&#39;      | finalize             |   3.091 ns | 0.1001 ns | 0.1072 ns |  1.00 |    0.05 |      - |         - |          NA |
|                            |                      |            |           |           |       |         |        |           |             |
| **&#39;legacy Split + recursion&#39;** | **proce(...).leaf [23]** | **120.270 ns** | **2.4065 ns** | **3.5274 ns** |  **1.00** |    **0.04** | **0.0060** |     **224 B** |        **1.00** |
| &#39;span walk (shipped)&#39;      | proce(...).leaf [23] |  73.646 ns | 1.4897 ns | 1.4631 ns |  0.61 |    0.02 |      - |         - |        0.00 |
|                            |                      |            |           |           |       |         |        |           |             |
| **&#39;legacy Split + recursion&#39;** | **process.37.validate**  |  **78.902 ns** | **1.6059 ns** | **2.2512 ns** |  **1.00** |    **0.04** | **0.0043** |     **160 B** |        **1.00** |
| &#39;span walk (shipped)&#39;      | process.37.validate  |  47.106 ns | 0.8187 ns | 0.7658 ns |  0.60 |    0.02 |      - |         - |        0.00 |
