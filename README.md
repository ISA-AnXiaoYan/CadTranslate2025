# CadTranslate2025

Windows / AutoCAD 2025 DWG text translation components, with DeepSeek, local translation memory and copy-only output. MIT licensed source code; Autodesk and Office software remain separately licensed prerequisites.

This initial source release contains a working AutoCAD plugin plus experimental native OLE components. It is not a turnkey packaged release of the later project-specific multilingual orchestration. No drawings, customer terminology, translated documents, API credentials, Autodesk assemblies or Microsoft fonts are distributed.

## Build

Requires Windows x64, .NET 8 SDK, and a licensed local AutoCAD 2025 installation. Set `ACAD2025_DIR` or pass the directory explicitly:

```powershell
dotnet build src/AutoCadPlugin/CadTranslate2025.csproj -c Release -p:AutoCAD2025Dir="<AutoCAD 2025 directory>"
dotnet build src/NativeOle/NativeOle.csproj -c Release -p:AutoCAD2025Dir="<AutoCAD 2025 directory>"
dotnet build src/OleWorkbookWorker/OleWorkbookWorker.csproj -c Release
```

Load the plugin using NETLOAD from a trusted local location. Configure the `DEEPSEEK_API_KEY` environment variable externally; do not put its value in scripts or configuration files. Supply `project_terms.csv` in a plugin Config folder and retain local translation memory outside version control.

Commands: `CADTRANS`, `CADTRANS_BATCH`, `CADTRANS_STATUS`, `CADTRANS_APPLY`, `CADTRANS_BG_EN2ZH`, `CADTRANS_BG_APPLY_JOB`, `CADTRANS_PING`. GUI directions remain ASCII `ZH2EN` / `EN2ZH`.

Background options: `CADTRANS_OUTPUT_ROOT`, `CADTRANS_CACHE_PATH`, `CADTRANS_MAX_API_CONCURRENCY`, `CADTRANS_FREEZE_ROOT`, `CADTRANS_JOB_PATH`. API concurrency defaults to one; the configured limit is per process. A real batch controller must enforce a total account-level limit and terminal job timeouts.

## Scope and limitations

- Native DBText, MText, attributes and supported MLeader content are handled. File success must be checked against object readback, not just process exit codes or `applied` status.
- The baseline plugin prompt supports English/Chinese. Multilingual-to-Chinese and corpus-wide terminology approval were validated in a separate local pipeline and are not yet integrated into this baseline command interface.
- XREF source editing, AEC/proxy text, raster OCR and automatic layout are not implemented. Treat unsupported content as explicit coverage gaps.
- OLE workbook editing requires a registered local Office-compatible OLE server. `OleWorkbookWorker` takes a disposable `.ole` storage copy and a translation plan JSON; it checks formulas and numeric values on the active sheet and refreshes the OLE presentation cache. It does not itself write a DWG.
- `NativeOle/MemoryFiler.cs` is an experimental CopyFiler implementation for native object serialization. FileFiler mode is unsupported. Do not use it on irreplaceable drawings without independent readback validation.

See [lessons learned](docs/LESSONS_LEARNED.md) for the workflow and failure modes that informed this release.
