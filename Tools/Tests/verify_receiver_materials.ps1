param(
  [Parameter(Mandatory=$true)][string]$EditorPath,
  [string]$UrpVersion = '17.5.0',
  [string]$VerificationProject
)
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if (-not (Test-Path -LiteralPath $EditorPath -PathType Leaf)) { throw "Unity Editor executable not found: $EditorPath" }
if (-not $VerificationProject) { $VerificationProject = Join-Path $repoRoot ('Temp/ReceiverMaterials-' + [guid]::NewGuid().ToString('N')) }
$VerificationProject = [IO.Path]::GetFullPath($VerificationProject)
New-Item -ItemType Directory -Force -Path "$VerificationProject/Assets/Editor", "$VerificationProject/Assets/Shaders", "$VerificationProject/Assets/Resources", "$VerificationProject/Packages", "$VerificationProject/ProjectSettings", "$VerificationProject/Assets/Samples/UnityChanKAGURA/Models/FBX", "$VerificationProject/Assets/Samples/UnityChanKAGURA/Scenes", "$VerificationProject/Assets/Samples/UnityChanKAGURA/Scripts" | Out-Null
foreach ($folder in @('Core','Lib','Net','Utils')) {
  Copy-Item -LiteralPath "$repoRoot/Assets/StreamingMesh/Scripts/$folder" -Destination "$VerificationProject/Assets" -Recurse -Force
}
foreach ($file in @('Receiver.cs','Receiver.cs.meta','STMHttpSerializer.cs','STMHttpBaseSerializer.cs','STMAudioRecorder.cs','TilePacker.cs','ReceiverCameraControls.cs')) {
  Copy-Item -LiteralPath "$repoRoot/Assets/StreamingMesh/Scripts/$file" -Destination "$VerificationProject/Assets/$file" -Force
}
Copy-Item -LiteralPath "$repoRoot/Assets/StreamingMesh/STMHttpSender.cs" -Destination "$VerificationProject/Assets/STMHttpSender.cs" -Force
Copy-Item -Path "$repoRoot/Assets/StreamingMesh/Resources/*.compute" -Destination "$VerificationProject/Assets/Resources" -Force
Copy-Item -Path "$repoRoot/Assets/StreamingMesh/Shaders/ExportNormal.shader*" -Destination "$VerificationProject/Assets/Shaders" -Force
Copy-Item -LiteralPath "$repoRoot/Assets/StreamingMesh/Settings" -Destination "$VerificationProject/Assets" -Recurse -Force
Copy-Item -LiteralPath "$repoRoot/Packages/com.unity.universaltoonshader.urp" -Destination "$VerificationProject/Packages" -Recurse -Force
Copy-Item -LiteralPath "$repoRoot/Assets/Samples/UnityChanKAGURA/Models/FBX/Materials" -Destination "$VerificationProject/Assets/Samples/UnityChanKAGURA/Models/FBX" -Recurse -Force
Copy-Item -Path "$repoRoot/Assets/Samples/UnityChanKAGURA/Models/FBX/UnityCHanKAGURA.fbx*" -Destination "$VerificationProject/Assets/Samples/UnityChanKAGURA/Models/FBX" -Force
Copy-Item -Path "$repoRoot/Assets/Samples/UnityChanKAGURA/Scenes/KaguraReceiver.unity*" -Destination "$VerificationProject/Assets/Samples/UnityChanKAGURA/Scenes" -Force
foreach ($script in @('KaguraReceiverControls','KaguraDemoControls')) {
  Copy-Item -Path "$repoRoot/Assets/Samples/UnityChanKAGURA/Scripts/$script.cs*" -Destination "$VerificationProject/Assets/Samples/UnityChanKAGURA/Scripts" -Force
}
Copy-Item -LiteralPath "$PSScriptRoot/ReceiverMaterialVerification.cs", "$PSScriptRoot/ReceiverTangentVerification.cs" -Destination "$VerificationProject/Assets/Editor" -Force
Copy-Item -LiteralPath "$PSScriptRoot/MaterialFixture.shader" -Destination "$VerificationProject/Assets/Shaders" -Force
$editorVersion = (Get-Item -LiteralPath $EditorPath).Directory.Parent.Name
"m_EditorVersion: $editorVersion" | Set-Content -LiteralPath "$VerificationProject/ProjectSettings/ProjectVersion.txt" -Encoding utf8
$lock = Get-Content -LiteralPath "$repoRoot/Packages/packages-lock.json" -Raw | ConvertFrom-Json
$coreHash = $lock.dependencies.'com.timewire.core'.hash
$dependencies = @{
  'com.unity.render-pipelines.universal' = $UrpVersion
  'com.timewire.core' = "https://github.com/blkcatman/TimeWire.git?path=/Core#$coreHash"
  'com.unity.modules.audio' = '1.0.0'
  'com.unity.modules.director' = '1.0.0'
  'com.unity.modules.jsonserialize' = '1.0.0'
  'com.unity.modules.imageconversion' = '1.0.0'
  'com.unity.modules.unitywebrequest' = '1.0.0'
  'com.unity.modules.unitywebrequestaudio' = '1.0.0'
  'com.unity.modules.unitywebrequesttexture' = '1.0.0'
}
@{dependencies=$dependencies} | ConvertTo-Json | Set-Content -LiteralPath "$VerificationProject/Packages/manifest.json" -Encoding utf8
$logPath = Join-Path $VerificationProject 'material-verification.log'
Write-Output "Verification project: $VerificationProject"
$editorProcess = Start-Process -FilePath $EditorPath -WindowStyle Hidden -PassThru -ArgumentList @('-batchmode','-quit','-force-d3d11','-projectPath',('"'+$VerificationProject+'"'),'-executeMethod','ReceiverMaterialVerification.Run','-logFile',('"'+$logPath+'"'))
$editorProcess.WaitForExit()
Get-Content -LiteralPath $logPath | Select-String 'PASS receiver|KAGURA material render|KAGURA GPU|error CS|Exception:|Shader error' | ForEach-Object { $_.Line }
if ($editorProcess.ExitCode -ne 0) { throw "Verification failed ($($editorProcess.ExitCode)); see $logPath" }
if (-not (Select-String -LiteralPath $logPath -Pattern 'PASS receiver materials' -Quiet)) { throw "Verification did not complete; see $logPath" }
Write-Output "Unity verification passed. Images: $VerificationProject/Results"
