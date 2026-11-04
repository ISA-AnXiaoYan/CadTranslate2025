# CadTranslate2025

CadTranslate2025 translates DWG text on Windows with AutoCAD 2025, a project glossary, local translation memory and DeepSeek. The source is MIT licensed. AutoCAD, Office-compatible software and fonts are separate local prerequisites.

The headless workflow freezes source copies, generates a reviewable plan, writes Chinese text and verifies saved drawings before delivery. It covers CAD table text cells and provides a qualified XLSX OLE workflow for all embedded worksheets.

- [后台操作手册](docs/HEADLESS.md) gives setup and runnable commands for the native, OLE and image-reference paths.
- [阶段复盘](docs/阶段复盘.md) records private batch results and public tool qualification separately.
- [前期经验](docs/经验总结.md) covers numbered labels, fonts, locked layers and failed OLE approaches.

## Build

Use Windows x64, the .NET 8 SDK, PowerShell 7 and a licensed local AutoCAD 2025 installation. Set `ACAD2025_DIR` to its install directory, or pass `-p:AutoCAD2025Dir` to the AutoCAD-dependent projects.

```powershell
dotnet build src/AutoCadPlugin/CadTranslate2025.csproj -c Release
dotnet build src/HeadlessBatch/HeadlessBatch.csproj -c Release
dotnet build src/TranslationWorker/TranslationWorker.csproj -c Release
dotnet build src/OleWorkbookWorker/OleWorkbookWorker.csproj -c Release
```

## Entry points

Use `tools/Invoke-DwgWorkflow.ps1` for the staged multilingual workflow. The stages are `Prepare`, `Draft`, `Apply`, `Audit` and `Publish`. The manual covers optional all-sheet OLE and image relinking steps between apply and audit.

The interactive plugin retains `CADTRANS`, `CADTRANS_BATCH`, `CADTRANS_STATUS`, `CADTRANS_APPLY`, `CADTRANS_BG_EN2ZH`, `CADTRANS_BG_APPLY_JOB` and `CADTRANS_PING`. Load it using NETLOAD from a trusted location and configure `Config/project_terms.csv` for the plugin. Its directions remain `ZH2EN` and `EN2ZH`.

Set `DEEPSEEK_API_KEY` in the Windows user environment. Keep private glossary data, job directories and caches outside version control.

## Coverage

- The headless writer supports DBText, MText, attributes, MLeader text and single-content CAD table text cells. It inventories block leader values, protected table content, dimension overrides and image dependencies.
- The qualified XLSX OLE path uses a registered local Office-compatible OLE server and validates saved workbook content independently. It uses native CopyFiler replacement on disposable drawing copies.
- External reference source editing, AEC/proxy text, raster OCR, outlined text recovery and automatic reflow require a separate process.
