# Headless components

The public headless sources are generalized from the local multilingual workflow. They require explicit private staging and orchestration. Do not run their commands in a user's interactive AutoCAD session: commands exit their isolated host process after writing reports.

Build `src/HeadlessBatch/HeadlessBatch.csproj` with `AutoCAD2025Dir`, and `src/TranslationWorker/TranslationWorker.csproj` with .NET 8. `CADTRANS_WORK_ROOT` points to a private job directory.

`tools/Invoke-Headless.ps1` launches the isolated host with hidden windows and Unicode logs. It requires a disposable host inside the private staging directory and copies that host again before opening it. Run it under PowerShell 7, supplying the AutoCAD directory, work root, staging host, compiled DLL and one of the documented commands. It adds only the exact DLL directory to TRUSTEDPATHS for loading and restores the previous value before the command.

1. Freeze original hashes. Copy inputs into `staging/<relative path>`. Separately copy them into `preview_inputs/<relative path>` for apply. Never use an original as Core Console's active `/i` host.
2. In an isolated hidden Core Console, NETLOAD `HeadlessBatch.dll` and execute `CADTRANS_HEADLESS_SCAN`. It writes `preview_extract.json`, including reachability, visibility and unsupported-object counts.
3. Review a corpus-wide glossary. Copy `examples/terminology.json` into the private work root and adapt it to the drawing discipline. `ConfirmedInferences` accepts scoped records `{File, Locations:[{Handle}], Output}`; each Output is the complete replacement for that entity, so formatting controls must be preserved. Never publish customer terms or text.
4. Run `dotnet TranslationWorker.dll <work root>` outside CAD. It creates `preview_plan.json`, `preview_text_cache.json` and `preview_translation_summary.json`. It translates to Simplified Chinese and reuses the shared cache. Review all unresolved items before apply. `CADTRANS_MODEL` and `CADTRANS_MAX_API_CONCURRENCY` configure calls; default concurrency is 8, capped at 50 per process. This is a local bound, not a claim about provider account limits. The individual fallback pass uses up to 4 calls. Coordinate all workers against the actual account limit.
5. In a fresh hidden Core Console execute `CADTRANS_HEADLESS_APPLY`. It writes `preview_outputs/<relative stem>_ZH.dwg`, refuses existing outputs, checks current source per handle, temporarily unlocks layers, binds a Chinese font and verifies saved text and layer locks. Check every report row for both `Error` and `Errors`; a process exit code of zero is insufficient.
6. Reopen the results and independently check fonts, unsupported coverage, original hashes and placement. Visually approve representative samples before a full batch. The generic apply component checks text and layer locks; final font/geometry/coverage acceptance remains the controller's responsibility.

`preview_text_cache.json` is private state. Archive it with the glossary version and input hash manifest; do not reuse an unreviewed cache across unrelated disciplines.

## Experimental OLE path

Create private `ole_inputs.json`, an array of absolute paths to translated DWG copies. `CTBATCHOLEEXPORT` writes `ole-v1/batch_objects.json` and per-object DXFs for storage extraction. The exported DXF is an inspection carrier, not a file to import back into the drawing.

Extract the OLE2FRAME entity's group-310 payload only; in the locally qualified format the compound storage begins at byte 128. A per-object plan for `OleWorkbookWorker` is an array of `{File, Entries:[{Handle,Source,Target}]}`; File is the `.ole` basename without extension and Handle is an Excel cell address. Set up storage copies under `ole-v1/refined/<key>.ole` and run the workbook worker on each copy. It uses the registered local OLE server without a visible Excel window.

Only after source verification and Office formula/numeric checks, execute `CTBATCHOLEAPPLY`. It preserves the first 124 payload bytes, updates storage length, writes a new DWG, and exports saved OLE objects for independent readback. Duplicate input basenames are rejected; use separate work roots in that case. Manifests are trusted private controller input and must not be changed between export and apply. This experimental component does not itself freeze source hashes.

For the qualified payload format, `tools/verify_ole.py <work root>` compares saved strings, expanded shared formulas, cached numeric results and placement prefix across all worksheets. Install the packages in `tools/requirements.txt` in an isolated environment. A complete controller must additionally render presentation caches and audit native text, layer state, fonts and original hashes.

Keep commercial AutoCAD/Office assemblies, fonts, drawings and workbook data outside this repository. Refer to the lessons learned before changing OLE serialization or unattended shutdown.

## Validation evidence boundary

The generalized headless scanner, translation worker and hidden runner were exercised on one private representative drawing: 180 distinct fragments reused a local reviewed cache, 205 changed native entities matched saved readback, layer locks were preserved, and no API calls were needed. The private batch used the corresponding project-specific pipeline. This does not establish arbitrary-file compatibility or an independent live API qualification of the generalized prompt. The public OLE verifier was checked against saved private object fixtures; those fixtures are not distributed.
