param(
    [Parameter(Mandatory=$true)][string]$EditorPath,
    [string]$VerificationProject,
    [string]$Output
)
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if (-not (Test-Path -LiteralPath $EditorPath -PathType Leaf)) { throw "Editor not found: $EditorPath" }
if (-not $VerificationProject) { $VerificationProject = Join-Path $repoRoot ('Temp/TextureCompression-' + [guid]::NewGuid().ToString('N')) }
if (-not $Output) { $Output = Join-Path $repoRoot 'Builds/TextureCompressionProbe' }
$VerificationProject = [IO.Path]::GetFullPath($VerificationProject)
$Output = [IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Force -Path "$VerificationProject/Assets/Editor", "$VerificationProject/Assets/Resources", "$VerificationProject/Assets/Plugins/WebGL", "$VerificationProject/Packages", "$VerificationProject/ProjectSettings", "$repoRoot/Logs" | Out-Null
Copy-Item -LiteralPath "$PSScriptRoot/TextureCompressionProbe.cs" -Destination "$VerificationProject/Assets" -Force
Copy-Item -LiteralPath "$PSScriptRoot/TextureCompressionVerification.cs" -Destination "$VerificationProject/Assets/Editor" -Force
Copy-Item -LiteralPath "$PSScriptRoot/TextureCompressionProbe.shader" -Destination "$VerificationProject/Assets/Resources" -Force
Copy-Item -LiteralPath "$PSScriptRoot/TextureCompressionProbe.jslib", "$repoRoot/Assets/Plugins/WebGL/StreamingMeshMemory.jslib" -Destination "$VerificationProject/Assets/Plugins/WebGL" -Force
$utf8 = New-Object System.Text.UTF8Encoding($false)
$editorVersion = (Get-Item -LiteralPath $EditorPath).Directory.Parent.Name
[IO.File]::WriteAllText("$VerificationProject/ProjectSettings/ProjectVersion.txt", "m_EditorVersion: $editorVersion`n", $utf8)
$dependencies = @{ 'com.unity.modules.jsonserialize'='1.0.0'; 'com.unity.modules.imageconversion'='1.0.0'; 'com.unity.modules.imgui'='1.0.0'; 'com.unity.modules.unitywebrequest'='1.0.0' }
[IO.File]::WriteAllText("$VerificationProject/Packages/manifest.json", (@{dependencies=$dependencies} | ConvertTo-Json), $utf8)
$body = Join-Path $repoRoot 'Assets/Samples/UnityChanKAGURA/Models/FBX/Materials/TEX/PNG/Body_Base.png'
$logPath = Join-Path $repoRoot 'Logs/TextureCompression-verification.log'
Write-Output "Verification project: $VerificationProject"
Write-Output "Output: $Output"
$editorProcess = Start-Process -FilePath $EditorPath -WindowStyle Hidden -PassThru -ArgumentList @('-batchmode','-force-d3d11','-projectPath',('"'+$VerificationProject+'"'),'-executeMethod','TextureCompressionVerification.Run','-probeOutput',('"'+$Output+'"'),'-probeBody',('"'+$body+'"'),'-logFile',('"'+$logPath+'"'))
$editorProcess.WaitForExit()
Get-Content -LiteralPath $logPath | Select-String 'STM_TEX_EXPORT|STM_TEX_RESULT|STM_TEX_SUMMARY|PASS texture|error CS|Exception:|Shader error' | ForEach-Object { $_.Line }
if ($editorProcess.ExitCode -ne 0) { throw "Verification failed ($($editorProcess.ExitCode)); see $logPath" }
if (-not (Select-String -LiteralPath $logPath -Pattern 'PASS texture compression export/native/build' -Quiet)) { throw "Verification did not complete; see $logPath" }
Copy-Item -LiteralPath "$Output/native-results.json" -Destination "$repoRoot/Logs/TextureCompression-native-results.json" -Force
# Keep the result JSON below the canvas instead of overlapping Unity's centered template.
$indexPath = Join-Path $Output 'index.html'
$index = [IO.File]::ReadAllText($indexPath)
$layout = '<style>body{margin:16px;background:#10131a;color:#eee}#unity-container.unity-desktop{position:relative;left:auto;top:auto;transform:none;margin:0 auto}#texture-probe-result{overflow-wrap:anywhere;box-sizing:border-box}</style>'
[IO.File]::WriteAllText($indexPath, $index.Replace('</head>', ($layout + "`n</head>")), $utf8)
Write-Output "Native GPU verification and Web build passed. Browser verification is a separate step."
