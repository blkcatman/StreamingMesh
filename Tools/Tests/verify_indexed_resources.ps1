param(
  [Parameter(Mandatory=$true)][string]$EditorPath,
  [string]$UrpVersion = '17.5.0',
  [string]$VerificationProject,
  [string]$Output,
  [string]$ChannelOutput,
  [switch]$BuildOnly
)
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if (-not (Test-Path -LiteralPath $EditorPath -PathType Leaf)) { throw "Unity Editor executable not found: $EditorPath" }
if (-not $VerificationProject) { $VerificationProject = Join-Path $repoRoot ('Temp/IndexedResources-' + [guid]::NewGuid().ToString('N')) }
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
Copy-Item -LiteralPath "$PSScriptRoot/ReceiverMaterialVerification.cs", "$PSScriptRoot/ReceiverTangentVerification.cs", "$PSScriptRoot/ResourceIdentityVerification.cs" -Destination "$VerificationProject/Assets/Editor" -Force
Copy-Item -Path "$repoRoot/Assets/StreamingMesh/Editor/ReceiverResourceBuildProcessor.cs*" -Destination "$VerificationProject/Assets/Editor" -Force
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

if (-not $Output) { $Output = Join-Path $repoRoot 'Builds/IndexedReceiver' }
$Output = [IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Force -Path "$VerificationProject/Assets/Samples/UnityChanKAGURA/Prefabs" | Out-Null
Copy-Item -Path "$repoRoot/Assets/Samples/UnityChanKAGURA/Prefabs/UnityChanKAGURA.prefab*" -Destination "$VerificationProject/Assets/Samples/UnityChanKAGURA/Prefabs" -Force
if (-not $ChannelOutput) { $ChannelOutput = Join-Path $repoRoot 'DevData/channels/channel_KAGURA_INDEX' }
New-Item -ItemType Directory -Force -Path "$VerificationProject/Assets/Plugins/WebGL", "$repoRoot/Logs" | Out-Null
Copy-Item -LiteralPath "$PSScriptRoot/IndexedResourceVerification.cs" -Destination "$VerificationProject/Assets/Editor" -Force
Copy-Item -LiteralPath "$PSScriptRoot/TextureCompressionProbe.cs" -Destination "$VerificationProject/Assets" -Force
Copy-Item -LiteralPath "$PSScriptRoot/TextureCompressionProbe.shader" -Destination "$VerificationProject/Assets/Resources" -Force
Copy-Item -LiteralPath "$PSScriptRoot/TextureCompressionProbe.jslib" -Destination "$VerificationProject/Assets/Plugins/WebGL" -Force
Copy-Item -Path "$repoRoot/Assets/Plugins/WebGL/*.jslib" -Destination "$VerificationProject/Assets/Plugins/WebGL" -Force
Copy-Item -LiteralPath "$repoRoot/Assets/StreamingMesh/Editor/StreamingMeshWebBuild.cs" -Destination "$VerificationProject/Assets/Editor" -Force
$logPath = Join-Path $repoRoot 'Logs/IndexedResources-verification.log'
$fixture = Join-Path $repoRoot 'DevData/channels/channel_KAGURA_ID'
Write-Output "Verification project: $VerificationProject"
Write-Output "Channel output: $ChannelOutput"
$taskArguments = @('-batchmode','-force-d3d11','-projectPath',('"'+$VerificationProject+'"'),'-executeMethod','IndexedResourceVerification.Run','-indexedOutput',('"'+$Output+'"'),'-indexedChannel',('"'+$ChannelOutput+'"'),'-indexedFixture',('"'+$fixture+'"'),'-logFile',('"'+$logPath+'"'))
if ($BuildOnly) { $taskArguments += '-indexedBuildOnly' }
$editorProcess = Start-Process -FilePath $EditorPath -WindowStyle Hidden -PassThru -ArgumentList $taskArguments
$editorProcess.WaitForExit()
Get-Content -LiteralPath $logPath | Select-String 'STM_INDEX|PASS indexed|error CS|Exception:|Shader error' | ForEach-Object { $_.Line }
if ($editorProcess.ExitCode -ne 0) { throw "Verification failed ($($editorProcess.ExitCode)); see $logPath" }
if (-not (Select-String -LiteralPath $logPath -Pattern 'PASS indexed resources export/native/build' -Quiet)) { throw "Verification did not complete; see $logPath" }
Write-Output "Indexed resource export, native loading and Web build passed. Browser verification is separate."
