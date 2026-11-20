# -KeepStaging：保留 payloads／generated 暫存（預設在 MSI 驗證通過後刪除，下次打包會重新產生）
param([ValidateSet('2024','2025','2026')][string[]]$RevitVersion=@('2024','2025','2026'),[switch]$SkipBuild,[switch]$KeepStaging)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$vswhere='C:/Program Files (x86)/Microsoft Visual Studio/Installer/vswhere.exe'
$vs=& $vswhere -latest -property installationPath
$devenv=Join-Path $vs 'Common7/IDE/devenv.com'
$fix=Join-Path $vs 'Common7/IDE/CommonExtensions/Microsoft/VSI/DisableOutOfProcBuild/DisableOutOfProcBuild.exe'
if(!(Test-Path -LiteralPath $fix)){throw 'Install Microsoft Visual Studio Installer Projects extension first.'}
Push-Location $vs
try { & $fix } finally { Pop-Location }
if($LASTEXITCODE -ne 0){throw 'Failed to enable command-line setup builds.'}
Push-Location $root
try {
 if(!$SkipBuild){
  Push-Location server
  try{& npm.cmd run build;if($LASTEXITCODE -ne 0){throw 'Node build failed'}}finally{Pop-Location}
 }
 foreach($year in $RevitVersion){
  if(!$SkipBuild){
   $cfg="Release R$($year.Substring(2))"
   # The command set deploys into the add-in output, so build the add-in first.
   foreach($project in 'src/Sinotech.Revit.Addin/RevitMCPPlugin.csproj','src/Sinotech.Revit.Commands/RevitMCPCommandSet.csproj','src/Sinotech.Revit.Extensions/Sinotech.Revit.Extensions.csproj'){
    & dotnet build $project -c $cfg
    if($LASTEXITCODE -ne 0){throw "Revit $year compilation failed: $project"}
   }
  }
  & (Join-Path $PSScriptRoot 'Prepare-Payload.ps1') -RevitVersion $year
  # 自有 DLL 的檔案版本必須等於安裝檔版本：版本沒變時，MSI 升級會保留舊 DLL，Revit 仍載入舊程式
  $expected=(Get-Content server/package.json -Raw | ConvertFrom-Json).version + '.0'
  foreach($dll in 'RevitMCPPlugin.dll',"Commands/RevitMCPCommandSet/$year/RevitMCPCommandSet.dll","Commands/SinotechExtensions/$year/SinotechExtensions.dll"){
   $path=Join-Path $PSScriptRoot "payloads/$year/$dll"
   $actual=[Diagnostics.FileVersionInfo]::GetVersionInfo($path).FileVersion
   if($actual -ne $expected){throw "$dll 檔案版本為 $actual，應為 $expected（請重新編譯，勿使用 -SkipBuild 搭配舊的編譯結果）"}
  }
  $solution=Join-Path $PSScriptRoot "generated/$year/Sinotech_MCP_$year.sln"
  $log=Join-Path $PSScriptRoot "generated/$year/build.log"
  & $devenv $solution /Rebuild Release /Out $log > (Join-Path $PSScriptRoot "generated/$year/console.log") 2>&1
  if($LASTEXITCODE -ne 0){throw "MSI build failed: $log"}
  $version=(Get-Content server/package.json -Raw | ConvertFrom-Json).version
  if(!(Test-Path -LiteralPath (Join-Path $PSScriptRoot "Release/Sinotech_MCP_Revit${year}_$version.msi"))){throw "Visual Studio skipped the setup project. Inspect $log"}
  # Isolate MSI writer COM handles before opening the same package for review.
  & (Get-Process -Id $PID).Path -NoProfile -File (Join-Path $PSScriptRoot 'Set-MsiRegistration.ps1') -RevitVersion $year
  if($LASTEXITCODE -ne 0){throw "MSI registration settings failed for $year"}
  & (Join-Path $PSScriptRoot 'Verify-Msi.ps1') -RevitVersion $year
  # 新版驗證通過後，刪除同年份被取代的舊版安裝檔（其他年份不動），減少容量
  Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'Release') -Filter "Sinotech_MCP_Revit${year}_*.msi" |
   Where-Object { $_.Name -ne "Sinotech_MCP_Revit${year}_$version.msi" } |
   ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force; Write-Host "已刪除舊版安裝檔：$($_.Name)" }
  # 打包暫存（每年份約 140 MB）可由 Prepare-Payload 重新產生；registration 供 Verify-Msi 使用，保留
  if(!$KeepStaging){
   foreach($staging in "payloads/$year","generated/$year"){
    $path=Join-Path $PSScriptRoot $staging
    if(Test-Path -LiteralPath $path){Remove-Item -LiteralPath $path -Recurse -Force}
   }
  }
 }
}finally{Pop-Location}
