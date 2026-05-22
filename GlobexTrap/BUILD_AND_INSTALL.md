# Build & Install — IOF Globex Trap

## One-time setup
1. Install [.NET 6 SDK](https://dotnet.microsoft.com/download/dotnet/6.0) if not already present
2. Have Visual Studio 2022 (or VS Build Tools) installed

## Build in Visual Studio
1. Open `GlobexTrap.csproj` in Visual Studio
2. Set configuration to **Release**
3. Build → Build Solution  (`Ctrl+Shift+B`)
4. DLL appears at: `GlobexTrap\bin\Release\IOF_GlobexTrap.dll`

## Build from command line (dotnet CLI)
```
cd GlobexTrap
dotnet restore
dotnet build -c Release
```

## Install into Quantower
1. In Quantower open: `Settings → Scripts → Indicators`  
   (or navigate to `C:\Quantower\Settings\Scripts\Indicators\`)
2. Create a NEW subfolder named exactly:  `TradePhantoms_IOF_GlobexTrap`
3. Copy these two files into that folder:
   - `bin\Release\IOF_GlobexTrap.dll`
   - `IOF_GlobexTrap.cs`  *(optional — for source visibility)*
4. Restart Quantower (or click Refresh Scripts)
5. Add **IOF Globex Trap** to your **5m MNQ chart**

## Verify it loaded
- Indicators list should show: `IOF Globex Trap (IOF-GT)`
- If missing, check: `Quantower → Help → Logs` for compile errors

## Parameters to configure on first load
| Parameter | Default | Notes |
|---|---|---|
| Zone proximity (ticks) | 30 | How close a zone must be to Asia/London H/L |
| Asia start/end (UTC hr) | 0 / 7 | Summer MNQ. Winter: +1 to each |
| London start/end (UTC hr) | 7 / 14 | Summer MNQ. Winter: +1 to each |
| Show 15m / 1h / 4h zones | all ON | Toggle per timeframe |
| MTFC fill (yellow) | ON | Yellow = 2+ TF confluence near level |
