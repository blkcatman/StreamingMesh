param(
  [Parameter(Mandatory=$true)][string]$EditorPath
)
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if (-not (Test-Path -LiteralPath $EditorPath -PathType Leaf)) { throw "Unity Editor executable not found: $EditorPath" }
$projectPath = Join-Path $repoRoot ('Temp/ReceiverMemory-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path "$projectPath/Assets/Editor", "$projectPath/Assets/Resources", "$projectPath/Packages" | Out-Null
# Isolate verification from sample imports and the source project's Editor version.
foreach ($folder in @('Core','Lib','Net','Utils')) {
  Copy-Item -LiteralPath "$repoRoot/Assets/StreamingMesh/Scripts/$folder" -Destination "$projectPath/Assets/$folder" -Recurse
}
Copy-Item -LiteralPath "$repoRoot/Assets/StreamingMesh/Scripts/Receiver.cs" -Destination "$projectPath/Assets/Receiver.cs"
Copy-Item -Path "$repoRoot/Assets/StreamingMesh/Resources/*.compute" -Destination "$projectPath/Assets/Resources"
Copy-Item -LiteralPath "$PSScriptRoot/ReceiverMemoryVerification.cs" -Destination "$projectPath/Assets/ReceiverMemoryVerification.cs"
@'
{"dependencies":{"com.unity.modules.audio":"1.0.0","com.unity.modules.jsonserialize":"1.0.0","com.unity.modules.imageconversion":"1.0.0","com.unity.modules.unitywebrequest":"1.0.0","com.unity.modules.unitywebrequestaudio":"1.0.0","com.unity.modules.unitywebrequesttexture":"1.0.0"}}
'@ | Set-Content -LiteralPath "$projectPath/Packages/manifest.json" -Encoding utf8
$logPath = Join-Path $projectPath 'verification.log'
Write-Output "Verification project: $projectPath"
$editorProcess = Start-Process -FilePath $EditorPath -WindowStyle Hidden -PassThru -ArgumentList @('-batchmode','-force-d3d11','-projectPath',('"'+$projectPath+'"'),'-executeMethod','ReceiverMemoryVerification.Run','-logFile',('"'+$logPath+'"'))
$editorProcess.WaitForExit()
Get-Content -LiteralPath $logPath | Select-String 'PASS receiver memory|managed allocations|chunk reuse|error CS|Exception:|Shader error' | ForEach-Object { $_.Line }
if ($editorProcess.ExitCode -ne 0) { throw "Verification failed ($($editorProcess.ExitCode)); see $logPath" }
if (-not (Select-String -LiteralPath $logPath -Pattern 'PASS receiver memory' -Quiet)) { throw "Verification did not complete; see $logPath" }
Write-Output "Unity verification passed. Log: $logPath"
