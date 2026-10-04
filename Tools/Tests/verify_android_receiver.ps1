param(
  [Parameter(Mandatory=$true)][string]$EditorPath,
  [Parameter(Mandatory=$true)][string]$VerificationProject,
  [string]$Output
)
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$VerificationProject = [IO.Path]::GetFullPath($VerificationProject)
if (-not (Test-Path -LiteralPath "$VerificationProject/Assets/Samples/UnityChanKAGURA/Scenes/KaguraReceiver.unity")) {
  throw 'First create an isolated project with verify_indexed_resources.ps1.'
}
if (-not (Test-Path -LiteralPath $EditorPath)) { throw 'Unity Editor not found.' }
if (-not $Output) { $Output = Join-Path $repoRoot 'Builds/AndroidMultiFormatReceiver.apk' }
$Output = [IO.Path]::GetFullPath($Output)
foreach ($folder in @('Core','Lib','Net','Utils')) {
  Copy-Item -LiteralPath "$repoRoot/Assets/StreamingMesh/Scripts/$folder" -Destination "$VerificationProject/Assets" -Recurse -Force
}
Copy-Item -LiteralPath "$repoRoot/Assets/StreamingMesh/Scripts/Receiver.cs" -Destination "$VerificationProject/Assets/Receiver.cs" -Force
Copy-Item -LiteralPath "$repoRoot/Assets/Samples/UnityChanKAGURA/Scripts/KaguraReceiverControls.cs" -Destination "$VerificationProject/Assets/Samples/UnityChanKAGURA/Scripts/KaguraReceiverControls.cs" -Force
New-Item -ItemType Directory -Force -Path "$VerificationProject/Assets/Plugins/Android", "$VerificationProject/Assets/Editor", "$repoRoot/Logs" | Out-Null
Copy-Item -LiteralPath "$PSScriptRoot/AndroidReceiverVerification.cs" -Destination "$VerificationProject/Assets/Editor/AndroidReceiverVerification.cs" -Force
Copy-Item -LiteralPath "$PSScriptRoot/AndroidReceiverProbe.cs" -Destination "$VerificationProject/Assets/AndroidReceiverProbe.cs" -Force
Copy-Item -Path "$repoRoot/Assets/Plugins/Android/*" -Destination "$VerificationProject/Assets/Plugins/Android" -Force
Copy-Item -Path "$repoRoot/Assets/StreamingMesh/Android/*.java*" -Destination "$VerificationProject/Assets/Plugins/Android" -Force
$utf8 = New-Object Text.UTF8Encoding($false)
$manifestPath = Join-Path $VerificationProject 'Packages/manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$manifest.dependencies | Add-Member -NotePropertyName 'com.unity.modules.androidjni' -NotePropertyValue '1.0.0' -Force
[IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 10), $utf8)
$settingsPath = Join-Path $VerificationProject 'ProjectSettings/ProjectSettings.asset'
$settings = Get-Content -LiteralPath $settingsPath -Raw
$settings = $settings.Replace('useCustomMainGradleTemplate: 0','useCustomMainGradleTemplate: 1').Replace('useCustomMainManifest: 0','useCustomMainManifest: 1').Replace('androidApplicationEntry: 2','androidApplicationEntry: 1')
[IO.File]::WriteAllText($settingsPath, $settings, $utf8)
$log = Join-Path $repoRoot 'Logs/AndroidMultiFormatReceiver-build.log'
$process = Start-Process -FilePath $EditorPath -ArgumentList @('-batchmode','-quit','-projectPath',('"'+$VerificationProject+'"'),'-buildTarget','Android','-executeMethod','AndroidReceiverVerification.Build','-androidOutput',('"'+$Output+'"'),'-logFile',('"'+$log+'"')) -PassThru -WindowStyle Hidden
$process.WaitForExit()
if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $Output)) { throw "Android build failed. See $log" }
Write-Output "Built $Output"
