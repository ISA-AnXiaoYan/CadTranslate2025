#requires -Version 7.0
param(
    [Parameter(Mandatory)][ValidateSet('Prepare','Draft','Apply','Audit','Publish')][string]$Stage,
    [Parameter(Mandatory)][string]$WorkRoot,
    [string]$InputRoot,
    [string[]]$InputFiles,
    [string]$AutoCADDirectory=$env:ACAD2025_DIR,
    [string]$PluginDll=(Join-Path $PSScriptRoot '../src/HeadlessBatch/bin/Release/net8.0-windows/HeadlessBatch.dll'),
    [string]$TranslatorDll=(Join-Path $PSScriptRoot '../src/TranslationWorker/bin/Release/net8.0-windows/TranslationWorker.dll'),
    [string]$ReviewedPlanSha256,
    [string]$OutputRoot,
    [switch]$AcceptExistingMissingImages,
    [ValidateRange(10,3600)][int]$TimeoutSeconds=300
)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath($WorkRoot)
if($Stage -eq 'Prepare') { New-Item -ItemType Directory -Path $root -Force | Out-Null }
elseif(!(Test-Path -LiteralPath $root -PathType Container)) { throw 'Run Prepare first' }

function Read-Json([string]$path) { Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -Depth 100 -AsHashtable }
function Write-Json([string]$path, $value) {
    $temporary=$path+'.tmp'
    ConvertTo-Json -InputObject $value -Depth 100 | Set-Content -LiteralPath $temporary -Encoding utf8
    Move-Item -LiteralPath $temporary -Destination $path -Force
}
function Get-Sha([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
function Under([string]$base, [string]$relative) {
    if([IO.Path]::IsPathRooted($relative) -or $relative -match '(^|[\\/])\.\.([\\/]|$)' -or $relative.Contains(':')) {
        throw "Expected a relative path within the job: $relative"
    }
    $path=[IO.Path]::GetFullPath((Join-Path $base $relative))
    if(!$path.StartsWith($base.TrimEnd('\','/')+'\',[StringComparison]::OrdinalIgnoreCase)) { throw "Path escapes root: $relative" }
    return $path
}
function Copy-New([string]$source, [string]$target) {
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
    [IO.File]::Copy($source,$target,$false)
}
function Run-Cad([string]$dataRoot,[string]$hostFile,[string]$command) {
    if(!$AutoCADDirectory) { throw 'Set -AutoCADDirectory or ACAD2025_DIR' }
    $parameters=@{AutoCADDirectory=$AutoCADDirectory;WorkRoot=$dataRoot;StagingHost=$hostFile;PluginDll=$PluginDll;Command=$command;TimeoutSeconds=$TimeoutSeconds}
    & (Join-Path $PSScriptRoot 'Invoke-Headless.ps1') @parameters
}
function Assert-FileList($actual, $expected) {
    if(!$actual.Count -or $actual.Count -ne $expected.Count -or
       (($actual | Sort-Object) -join [char]10) -cne (($expected | Sort-Object) -join [char]10)) { throw 'Unexpected drawing list' }
}
function Assert-Scan($docs, $files) {
    Assert-FileList @($docs.File) @($files)
    if(@($docs | Where-Object {$_.Error -or $_.Issues.Count}).Count) { throw 'Scan report has read errors' }
}
function Text-Key($row) { '{0}/{1}/{2}' -f $row.Handle,$row.Row,$row.Column }
function Assert-Equal($before,$after,[string]$description) {
    if((ConvertTo-Json -InputObject $before -Depth 30 -Compress) -cne (ConvertTo-Json -InputObject $after -Depth 30 -Compress)) {
        throw "Readback differs: $description"
    }
}
function Sorted-Map($map) {
    @($map.Keys | Sort-Object | ForEach-Object { [ordered]@{Key=$_;Value=$map[$_]} })
}

$lease=[IO.File]::Open((Join-Path $root 'workflow.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
try {
    $manifestPath=Join-Path $root 'input_manifest.json'
    $scanPath=Join-Path $root 'preview_extract.json'
    $planPath=Join-Path $root 'preview_plan.json'
    $applyPath=Join-Path $root 'preview_apply_report.json'
    $receiptPath=Join-Path $root 'publish_receipt.json'
    $staging=Join-Path $root 'staging'
    $inputs=Join-Path $root 'preview_inputs'
    if($Stage -eq 'Prepare') {
        if(!$InputRoot -or !$InputFiles.Count) { throw 'Supply -InputRoot and explicit -InputFiles relative to it' }
        if((Test-Path -LiteralPath $staging) -or (Test-Path -LiteralPath $manifestPath)) { throw 'Prepare needs a fresh job directory' }
        $inputBase=(Resolve-Path -LiteralPath $InputRoot).Path
        if($root -eq $inputBase) { throw 'WorkRoot must differ from InputRoot' }
        $unique=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $manifest=@(foreach($relative in $InputFiles) {
            $source=Under $inputBase $relative
            if([IO.Path]::GetExtension($source) -ne '.dwg' -or !$unique.Add($source)) { throw "Invalid or duplicate DWG: $relative" }
            $relative=[IO.Path]::GetRelativePath($inputBase,$source)
            $sha=Get-Sha $source
            $scanCopy=Under $staging $relative
            $applyCopy=Under $inputs $relative
            Copy-New $source $scanCopy
            Copy-New $source $applyCopy
            if((Get-Sha $scanCopy) -ne $sha -or (Get-Sha $applyCopy) -ne $sha -or (Get-Sha $source) -ne $sha) { throw "Source changed: $relative" }
            [ordered]@{File=$relative;Source=$source;Sha256=$sha}
        })
        Run-Cad $root (Under $staging $manifest[0].File) CADTRANS_HEADLESS_SCAN
        Assert-Scan @(Read-Json $scanPath) $manifest.File
        Write-Json $manifestPath ([ordered]@{Files=$manifest;ScanSha256=(Get-Sha $scanPath)})
        return
    }
    $frozen=Read-Json $manifestPath
    $manifest=@($frozen.Files)
    if((Get-Sha $scanPath) -ne $frozen.ScanSha256) { throw 'Frozen source scan changed' }
    foreach($item in $manifest) {
        if((Get-Sha $item.Source) -ne $item.Sha256 -or (Get-Sha (Under $inputs $item.File)) -ne $item.Sha256) {
            throw "Original or apply input changed: $($item.File)"
        }
    }
    if($Stage -eq 'Draft') {
        if(Test-Path -LiteralPath $applyPath) { throw 'Create a fresh work root to redraft an applied job' }
        & dotnet $TranslatorDll $root
        if($LASTEXITCODE -ne 0) { throw 'Translation draft has unresolved entries or failed' }
        Get-Sha $planPath
        return
    }
    $planHash=Get-Sha $planPath
    if($Stage -eq 'Apply') {
        if(!$ReviewedPlanSha256 -or $planHash -ne $ReviewedPlanSha256) { throw 'Provide the SHA-256 of the reviewed plan' }
        if(Test-Path -LiteralPath $applyPath) { throw 'An apply report already exists. Use a fresh work root for a new apply' }
        $plans=@(Read-Json $planPath)
        Assert-FileList @($plans.File) @($manifest.File)
        Run-Cad $root (Under $staging $manifest[0].File) CADTRANS_HEADLESS_APPLY
    }
    $applied=@(Read-Json $applyPath)
    if($applied.Count -ne $manifest.Count -or @($applied | Where-Object {
        $_.Error -or $_.Errors.Count -or !$_.LayerLocksPreserved -or $_.PlanSha256 -ne $planHash -or $_.Changed -ne $_.ReadbackChecked
    }).Count) { throw 'Apply report is incomplete or has errors' }
    if($Stage -eq 'Apply') { return }
    if($Stage -eq 'Audit') {
        Write-Json $receiptPath @{Ready=$false}
        $auditRoot=Join-Path $root ('readback-'+[Guid]::NewGuid().ToString('N'))
        $auditStage=Join-Path $auditRoot 'staging'
        $overridesPath=Join-Path $root 'candidate_paths.json'
        $overrides=if(Test-Path -LiteralPath $overridesPath){Read-Json $overridesPath}else{@{}}
        $files=@(foreach($item in $manifest) {
            $result=@($applied|Where-Object {$_.File -ceq $item.File})
            if($result.Count -ne 1 -or $result[0].SourceSha256 -ne $item.Sha256) { throw "Apply source mismatch: $($item.File)" }
            $relative=[IO.Path]::Combine([IO.Path]::GetDirectoryName($item.File),[IO.Path]::GetFileNameWithoutExtension($item.File)+'_ZH.dwg')
            $path=if($overrides.Contains($item.File)) { Under $root $overrides[$item.File] } else { Under (Join-Path $root 'preview_outputs') $relative }
            $sha=Get-Sha $path
            if(!$overrides.Contains($item.File) -and $sha -ne $result[0].OutputSha256) { throw "Applied output changed: $relative" }
            $copy=Under $auditStage $relative
            Copy-New $path $copy
            if((Get-Sha $copy) -ne $sha) { throw "Audit copy changed: $relative" }
            [ordered]@{File=$item.File;Relative=$relative;Candidate=$path;Sha256=$sha}
        })
        Run-Cad $auditRoot (Under $auditStage $files[0].Relative) CADTRANS_HEADLESS_SCAN
        $after=@(Read-Json (Join-Path $auditRoot 'preview_extract.json'))
        Assert-Scan $after $files.Relative
        $before=@(Read-Json $scanPath)
        $oleReceiptPath=Join-Path $root 'ole-v1/ole_delivery_receipt.json'
        $oleReceipt=if(Test-Path -LiteralPath $oleReceiptPath){Read-Json $oleReceiptPath}else{@{}}
        $assets=@{}
        $missingImages=[Collections.Generic.List[object]]::new()
        foreach($file in $files) {
            $old=@($before|Where-Object {$_.File -ceq $file.File})[0]
            $new=@($after|Where-Object {$_.File -ceq $file.Relative})[0]
            $result=@($applied|Where-Object {$_.File -ceq $file.File})[0]
            if($old.EntityTypes.Ole2Frame -and $overrides.Contains($file.File) -and
                (!$oleReceipt.Ready -or $oleReceipt.DrawingSha256[$file.Candidate] -ne $file.Sha256)) {
                throw "OLE candidate needs saved-workbook verification: $($file.Relative)"
            }
            Assert-Equal (Sorted-Map $old.EntityTypes) (Sorted-Map $new.EntityTypes) $file.Relative
            Assert-Equal (Sorted-Map $old.LayerLocks) (Sorted-Map $new.LayerLocks) $file.Relative
            Assert-Equal $old.ProtectedContent $new.ProtectedContent $file.Relative
            $expected=@{}
            foreach($row in $old.Rows) { $expected[(Text-Key $row)]=$row.Raw }
            foreach($row in $result.SavedText) { $expected[(Text-Key $row)]=$row.Raw }
            if($new.Rows.Count -ne $expected.Count) { throw "Text inventory size changed: $($file.Relative)" }
            foreach($row in $new.Rows) {
                $key=Text-Key $row
                if(!$expected.Contains($key) -or $expected[$key] -cne $row.Raw) { throw "Saved text mismatch: $($file.Relative) $key" }
            }
            foreach($reference in $new.References) {
                $image=$reference.SourcePath
                if(!$image) {
                    $existing=@($old.References|Where-Object {$_.Handle -eq $reference.Handle -and !$_.SourcePath})
                    if(!$AcceptExistingMissingImages -or $existing.Count -ne 1) { throw "Missing image definition: $($file.Relative) $($reference.Handle)" }
                    $missingImages.Add(@{File=$file.Relative;Handle=$reference.Handle;Reason='Input image has no definition'})
                    continue
                }
                if([IO.Path]::IsPathRooted($image)) {
                    if(!(Test-Path -LiteralPath $image -PathType Leaf)) { throw "Unresolved image: $image" }
                    continue
                }
                $asset=[IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetDirectoryName($file.Candidate)) $image))
                $layoutRoot=Join-Path $auditRoot 'delivery-layout'
                $dwgLayout=Under $layoutRoot $file.Relative
                $assetLayout=[IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetDirectoryName($dwgLayout)) $image))
                $relativeAsset=[IO.Path]::GetRelativePath($layoutRoot,$assetLayout)
                $null=Under $layoutRoot $relativeAsset
                if([IO.Path]::GetExtension($asset) -eq '.dwg') { throw "Expected an image asset: $image" }
                $sha=Get-Sha $asset
                if($assets.ContainsKey($relativeAsset) -and $assets[$relativeAsset].Sha256 -ne $sha) { throw "Conflicting image references: $relativeAsset" }
                $assets[$relativeAsset]=[ordered]@{Relative=$relativeAsset;Candidate=$asset;Sha256=$sha}
            }
            if((Get-Sha $file.Candidate) -ne $file.Sha256) { throw "Candidate changed during audit: $($file.Relative)" }
        }
        Write-Json $receiptPath ([ordered]@{Ready=$true;PlanSha256=$planHash;Files=$files;Assets=@($assets.Values);MissingImages=$missingImages;ScanRoot=$auditRoot})
        return
    }
    if(!$OutputRoot) { throw 'Publish requires -OutputRoot' }
    $receipt=Read-Json $receiptPath
    if(!$receipt.Ready -or $receipt.PlanSha256 -ne $planHash) { throw 'Audit the current candidates before publishing' }
    $destination=[IO.Path]::GetFullPath($OutputRoot)
    $delivery=@($receipt.Assets)+@($receipt.Files)
    foreach($file in $delivery) {
        $target=Under $destination $file.Relative
        if((Get-Sha $file.Candidate) -ne $file.Sha256) { throw "Candidate changed: $($file.Relative)" }
        if((Test-Path -LiteralPath $target) -and (Get-Sha $target) -ne $file.Sha256) { throw "Destination already exists: $target" }
    }
    foreach($file in $delivery) {
        $target=Under $destination $file.Relative
        if(!(Test-Path -LiteralPath $target)) { Copy-New $file.Candidate $target }
        if((Get-Sha $target) -ne $file.Sha256) { throw "Delivery hash mismatch: $target" }
        $target
    }
}
finally { $lease.Dispose() }
