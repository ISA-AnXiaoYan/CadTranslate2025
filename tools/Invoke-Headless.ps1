param(
 [Parameter(Mandatory)][string]$AutoCADDirectory,
 [Parameter(Mandatory)][string]$WorkRoot,
 [Parameter(Mandatory)][string]$StagingHost,
 [Parameter(Mandatory)][string]$PluginDll,
 [Parameter(Mandatory)][ValidateSet('CADTRANS_HEADLESS_SCAN','CADTRANS_HEADLESS_APPLY','CTBATCHOLEEXPORT','CTBATCHOLEAPPLY','CTREPATHIMAGES')][string]$Command,
 [ValidateRange(10,3600)][int]$TimeoutSeconds=300
)
$ErrorActionPreference='Stop'
$root=(Resolve-Path -LiteralPath $WorkRoot).Path
$stage=(Resolve-Path -LiteralPath (Join-Path $root 'staging')).Path.TrimEnd('\')+'\'
$hostPath=(Resolve-Path -LiteralPath $StagingHost).Path
if(!$hostPath.StartsWith($stage,[StringComparison]::OrdinalIgnoreCase)){throw 'StagingHost must be a disposable DWG inside WorkRoot/staging'}
$dll=(Resolve-Path -LiteralPath $PluginDll).Path
$exe=Join-Path $AutoCADDirectory 'accoreconsole.exe'
if(!(Test-Path -LiteralPath $exe)){throw 'Core Console executable not found'}
$session=Join-Path $root ('console-sessions\'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $session -Force|Out-Null
$hostCopy=Join-Path $session 'host.dwg';Copy-Item -LiteralPath $hostPath -Destination $hostCopy
function LispString([string]$value){return '"'+$value.Replace('\','\\').Replace('"','\"')+'"'}
$trust=LispString ([IO.Path]::GetDirectoryName($dll));$load=LispString $dll
$script=Join-Path $session 'run.scr'
@(
 '(setq cadtransOldTrust (getvar "TRUSTEDPATHS"))'
 "(setvar `"TRUSTEDPATHS`" (strcat cadtransOldTrust `";`" $trust))"
 '_.FILEDIA'
 '0'
 '(command "_.NETLOAD" '+$load+')'
 '(setvar "TRUSTEDPATHS" cadtransOldTrust)'
 $Command
) | Set-Content -LiteralPath $script -Encoding utf8
$psi=[Diagnostics.ProcessStartInfo]::new($exe)
$psi.UseShellExecute=$false;$psi.CreateNoWindow=$true;$psi.WindowStyle='Hidden'
$psi.RedirectStandardOutput=$true;$psi.RedirectStandardError=$true
$psi.StandardOutputEncoding=[Text.Encoding]::Unicode;$psi.StandardErrorEncoding=[Text.Encoding]::Unicode
$psi.WorkingDirectory=$session;$psi.Environment['CADTRANS_WORK_ROOT']=$root
$psi.Environment.Remove('DEEPSEEK_API_KEY')|Out-Null
foreach($arg in @('/i',$hostCopy,'/s',$script,'/suppressgraphics')){$psi.ArgumentList.Add($arg)}
$process=[Diagnostics.Process]::Start($psi)
$stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
$timedOut=!$process.WaitForExit($TimeoutSeconds*1000)
if($timedOut){$process.Kill($true);$process.WaitForExit()}
$stdout.Result|Set-Content -LiteralPath (Join-Path $session 'stdout.log') -Encoding utf8
$stderr.Result|Set-Content -LiteralPath (Join-Path $session 'stderr.log') -Encoding utf8
$result=[pscustomobject]@{ProcessId=$process.Id;ExitCode=$process.ExitCode;Timeout=$timedOut;Evidence=$session}
$result|ConvertTo-Json
if($timedOut -or $process.ExitCode -ne 0){throw 'Isolated Core Console failed; inspect preserved logs'}
# Command reports and saved DWG readback remain required even when exit code is zero.
