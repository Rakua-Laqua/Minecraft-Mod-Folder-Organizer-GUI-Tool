# Minecraft-Mod-Folder-Organizer-GUI-Tool

展開済みMinecraft Modフォルダ群を対象に、各Mod内の `assets/<modid>/lang/` を抽出して `Mod直下/lang` に集約し、それ以外を削除するGUIツールです。

## 開発

### ビルド

```powershell
dotnet build .\MinecraftModFolderOrganizer.sln -c Release
```

### 実行

```powershell
dotnet run --project .\OrganizerTool\OrganizerTool.csproj
```

## 配布（publish）

`publish.ps1` で publish できます。成果物は `artifacts/publish/` 配下に出力されます。

### 方式A: self-contained（.NET同梱 / 容量増）

```powershell
powershell -ExecutionPolicy Bypass -File .\publish.ps1 -Mode self-contained -Runtime win-x64
```

### 方式B: framework-dependent（Runtimeが必要 / 容量小）

```powershell
powershell -ExecutionPolicy Bypass -File .\publish.ps1 -Mode framework-dependent -Runtime win-x64
```

### 方式C: 両方まとめて出力（おすすめ）

```powershell
powershell -ExecutionPolicy Bypass -File .\publish.ps1 -Mode both -Runtime win-x64
```

- `artifacts/publish/win-x64/self-contained/`
- `artifacts/publish/win-x64/framework-dependent/`

※ framework-dependent で配布する場合、利用者側に「.NET 8 Desktop Runtime」が必要です。