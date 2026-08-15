# P2 Settings/Export Hotfix 2

- Corrige o erro C# CS0019 no owner da MessageBox de Export.
- O owner e agora tratado explicitamente como `IWin32Window`.
- Remove tres warnings CS8602 do `LocalHistoryControl` usando referencias de coluna verificadas contra null.
- Mantem o toggle de Settings e o fluxo direto de Export introduzidos no Hotfix 1.
- Nao altera Core, Persistence, decoder, IQ pipeline, SQLite, ADSBdb/HexDB ou JSONL writer.
