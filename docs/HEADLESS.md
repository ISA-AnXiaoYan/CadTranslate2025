# AutoCAD 2025 后台工作流

需要 Windows x64、PowerShell 7、.NET 8 SDK、已授权的 AutoCAD 2025 和已安装的 Microsoft YaHei。处理嵌入式 Excel 对象还需要已注册的 Office 兼容 OLE 服务和 Python 3.11 或更新版本。工作目录、术语和缓存放在仓库之外。

```powershell
$env:ACAD2025_DIR = 'C:\Program Files\Autodesk\AutoCAD 2025'
dotnet build ./src/HeadlessBatch/HeadlessBatch.csproj -c Release
dotnet build ./src/TranslationWorker/TranslationWorker.csproj -c Release
dotnet build ./src/OleWorkbookWorker/OleWorkbookWorker.csproj -c Release
```

后台 CAD 命令由 `Invoke-Headless.ps1` 启动在隔离的 Core Console 进程中。它为 `/i` 另做一份临时宿主图，按会话保存日志，超时仅终止本次创建的进程。命令结束时会主动退出宿主，因此请通过后台脚本运行。

## 原生 DWG 文字与 CAD 表格

下面示例以相对路径选定输入，并将结果放到单独的交付目录。批量运行可为 `-InputFiles` 传入多条相对路径。

```powershell
$job = 'D:\cad-jobs\job-001'
$src = 'D:\drawings'
$dst = 'D:\deliveries\job-001'

./tools/Invoke-DwgWorkflow.ps1 -Stage Prepare -WorkRoot $job -InputRoot $src -InputFiles 'MEP\plan.dwg'
Copy-Item ./examples/terminology.json (Join-Path $job 'terminology.json')
$cad = @{
    WorkRoot = $job
    AutoCADDirectory = $env:ACAD2025_DIR
    PluginDll = (Resolve-Path ./src/HeadlessBatch/bin/Release/net8.0-windows/HeadlessBatch.dll).Path
    StagingHost = (Join-Path $job 'staging/MEP/plan.dwg')
}
```

`Prepare` 建立源文件哈希清单，分别保留扫描副本与回写输入副本。先检查 `preview_extract.json` 的 `Issues`、`References`、`ProtectedContent`、外参和代理对象统计，再配置私有术语表。

- `Terms` 的 `Sources` 填入可互换的精确别名，`Chinese` 填最终译法。匹配时忽略首尾空白与大小写，精确匹配优先于缓存。复杂上下文仍需检查计划，避免把相同的词片段在不同复合术语里强制成同一译法。
- `ConfirmedInferences` 支持按 `File` 和 `Locations` 指定范围。位置记录包含 `Handle`，CAD 表格另带从 0 开始的 `Row` 和 `Column`。每条 `Output` 是该位置的完整目标文本。将原文、候选释义和待核状态一起保留。
- 草稿可以通过共享缓存、术语表和 DeepSeek 生成，也可以直接由复核人员编写。DeepSeek 密钥从 Windows 用户环境变量 `DEEPSEEK_API_KEY` 读取。仅当有未缓存的候选片段时才需要密钥。单进程默认并发 8，上限 50，补查并发同时受该配置约束。根据实际账户限额协调多个进程。

```powershell
./tools/Invoke-DwgWorkflow.ps1 -Stage Draft -WorkRoot $job
$plan = Join-Path $job 'preview_plan.json'
python ./tools/review_text.py (Join-Path $job 'preview_extract.json') --plan $plan --output (Join-Path $job 'text_review.json')
```

核对 `preview_translation_summary.json` 的未解决片段，复核 `preview_plan.json` 的漏译、半译、术语、型号、数值和控制符。PDF 导入的拆词建议根据 `Location`、图层和坐标判断相邻关系，再按句柄写入局部修正。每张图的计划含 `File` 和 `Entries`，记录字段为 `Handle`、`Kind`、`Source`、`Target`，表格单元格另含 `Row` 和 `Column`。

`review_text.py` 把中外文混排、同源多译和数字差异按原文分组。编号、单位和专名也可能进入候选列表，需要结合上下文判断。复核旧成果时使用 `--saved-scan` 传入重新扫描的成果清单，文件名规则不同时通过 `--drawing-map` 提供映射。

```powershell
$checkedHash = (Get-FileHash -LiteralPath $plan -Algorithm SHA256).Hash
./tools/Invoke-DwgWorkflow.ps1 -Stage Apply -WorkRoot $job -ReviewedPlanSha256 $checkedHash
./tools/Invoke-DwgWorkflow.ps1 -Stage Audit -WorkRoot $job
./tools/Invoke-DwgWorkflow.ps1 -Stage Publish -WorkRoot $job -OutputRoot $dst
```

`Apply` 逐项确认源文字，按需临时解锁图层，设置微软雅黑并核对保存后的文字和样式。单内容 CAD 单元格支持回写，公式、字段、块内容和多内容单元格进入受保护清单。块内容引线和尺寸覆盖文字进入审查清单。若这些对象含待译语句，须走适合其对象类型的流程。

`Audit` 重新扫描候选图，对照计划和保存回执检查文字、实体类型计数、锁层、受保护内容和图像路径，生成哈希回执。`Publish` 按原目录结构复制通过核验的候选，遇到不同内容的同名输出会停止，已发布且哈希相同的文件可续传。对原图就已缺少定义的图像对象，可在核对句柄后使用 `-AcceptExistingMissingImages`，对应例外写入回执。

## PDF 转 DWG 后的图像资源

图像重定位可在 `Apply` 后进行。把候选图和图片复制到私有目录，按目标目录结构布置图片。为每张待处理图在 `image_repath_manifest.json` 提供 `Input`、`Output` 和 `Paths`，其中 `Paths` 的每项有原引用值 `Original` 与新相对路径 `Relative`。同一个相对路径需在输入目录和输出目录都能找到对应图片。

```powershell
./tools/Invoke-Headless.ps1 @cad -Command CTREPATHIMAGES
```

检查 `image_repath_report.json`。在 `candidate_paths.json` 中把原始相对 DWG 文件名映射为最终候选相对于任务根目录的路径，然后执行 `Audit` 和 `Publish`。发布会复制审计发现的相对图片依赖。绝对引用按现有路径检查，适合携带的成果应先转成相对引用。图片和矢量轮廓中的文字需要独立检查。

## OLE 全工作表

此流程面向已验证的 OLE2FRAME 和内嵌 XLSX 结构。使用包含已知来源工作簿的临时副本。导出的 DXF 用于取得 OLE 容器和保存后的读回数据。处理了外链图片的图纸先完成图像重定位，再导出 OLE。

```powershell
python -m pip install -r ./tools/requirements.txt
$native = (Resolve-Path (Join-Path $job 'preview_outputs/MEP/plan_ZH.dwg')).Path
ConvertTo-Json -InputObject @($native) | Set-Content (Join-Path $job 'ole_inputs.json') -Encoding utf8
./tools/Invoke-Headless.ps1 @cad -Command CTBATCHOLEEXPORT
python ./tools/ole_workflow.py inventory $job
```

`ole_inputs.json` 选定包含 OLE 的候选图，候选文件的基本名应互不相同。`inventory` 读取所有工作表的文字单元格，标出当前显示页。按同一套批准术语编写 `ole-v1/all_sheet_plan.json`。为每个已导出对象提供一条计划，无编辑的对象使用空列表。以下为结构示例，实际工作表名、单元格地址和源文字以清单为准。

```json
[
  {"File":"ole_001","Entries":[{"Sheet":"Load schedule","Handle":"B2","Source":"SPARE","Target":"备用"}]},
  {"File":"ole_002","Entries":[]}
]
```

```powershell
python ./tools/ole_workflow.py refresh $job --worker ./src/OleWorkbookWorker/bin/Release/net8.0-windows/OleWorkbookWorker.dll
./tools/Invoke-Headless.ps1 @cad -Command CTBATCHOLEAPPLY
python ./tools/ole_workflow.py verify $job
./tools/Render-OleCache.ps1 -CacheFile (Join-Path $job 'ole-v1/translated/ole_001_cache.bin') -OutputPng (Join-Path $job 'ole-preview.png')
```

`refresh` 对工作簿副本做文字编辑，验证源值、公式和数值，通过 OLE 服务刷新显示缓存。Native apply 校验导出时的源图哈希和当前刷新回执。保存后再次核对工作表名称、顺序、可见性、活动页、单元格值、共享公式、合并范围、译文字体及 OLE 位置头。缓存图用于检查图面能看到的文字是否截断。

将通过核验的 OLE 输出填入 `candidate_paths.json`，例如 `{"MEP\\plan.dwg":"ole-v1\\plan_ZH.dwg"}`，再用 `Audit` 和 `Publish` 交付。对带图片的 OLE 候选，保持相对于其 DWG 的图片布局。旧的活动页入口 `OleWorkbookWorker <disposable.ole> <active-plan.json>` 和 `tools/verify_ole.py` 仍可用于旧任务。跨多个工作表的新任务使用上面的全表入口。

## 验证范围

公开版本已在本机完成 CAD 表格所在图纸的 329 处回写和重新扫描，其中 34 处为表格单元格，并验证了中文相对目录的交付。另一张图的 18 个图片引用完成相对路径保存后读回。OLE 复用测试对一张图中的 4 个嵌入对象执行导出、刷新、回写与读回，分别编辑了显示页和非显示页各一处，四个对象的 136 个工作表实例通过核验。术语优先级和编号保护使用离线样例验证。私有样本与业务术语保存在本机。

原生样本中原已缺少定义的图片对象按句柄记录在例外回执中。该回执保留原图的缺失信息，便于交付人员核对。
