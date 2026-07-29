param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$localSigning = Join-Path $PSScriptRoot "load-signing-env.ps1"
if (-not $env:VEXARK_KEYSTORE_PATH -and (Test-Path -LiteralPath $localSigning)) {
    . $localSigning
}
$projectRoot = Split-Path -Parent $PSScriptRoot
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnetCandidates = @(
    (Join-Path $env:USERPROFILE ".dotnet\dotnet.exe")
    if ($dotnetCommand) { $dotnetCommand.Source }
) | Select-Object -Unique
$dotnet = $dotnetCandidates |
    Where-Object {
        (Test-Path -LiteralPath $_) -and
        ((& $_ --list-sdks 2>$null) -match "^9\.")
    } |
    Select-Object -First 1
$androidSdk = if ($env:ANDROID_HOME) {
    $env:ANDROID_HOME
} elseif ($env:ANDROID_SDK_ROOT) {
    $env:ANDROID_SDK_ROOT
} else {
    Join-Path $env:LOCALAPPDATA "Android\Sdk"
}
$javaHome = if ($env:JAVA_HOME) {
    $env:JAVA_HOME
} else {
    Join-Path $env:USERPROFILE ".gradle\jdks\jetbrains_s_r_o_-21-amd64-windows.2"
}
$embedded = Join-Path $projectRoot "src\PhoneBackup.Desktop\Embedded"
$publish = Join-Path $projectRoot "artifacts\publish"
$releaseArtifacts = Join-Path $projectRoot "artifacts\release"
$cargoCommand = Get-Command cargo -ErrorAction SilentlyContinue
$cargo = if ($cargoCommand) { $cargoCommand.Source } else { $null }
if (-not $cargo) { $cargo = Join-Path $env:USERPROFILE ".cargo\bin\cargo.exe" }
$cargoToolchain = @()
$rustup = Join-Path $env:USERPROFILE ".cargo\bin\rustup.exe"
if (-not (Get-Command link.exe -ErrorAction SilentlyContinue) -and
    (Test-Path -LiteralPath $rustup) -and
    ((& $rustup toolchain list) -match "stable-x86_64-pc-windows-gnu")) {
    $cargoToolchain = @("+stable-x86_64-pc-windows-gnu")
}
$ndkHome = if ($env:ANDROID_NDK_HOME) {
    $env:ANDROID_NDK_HOME
} else {
    Join-Path $androidSdk "ndk\29.0.14206865"
}

if (-not (Test-Path $dotnet)) { throw ".NET SDK не найден: $dotnet" }
if (-not (Test-Path $javaHome)) { throw "JDK 21 не найден: $javaHome" }
if (-not (Test-Path (Join-Path $androidSdk "platform-tools\adb.exe"))) {
    throw "Android Platform Tools не найдены: $androidSdk"
}
if (-not (Test-Path $cargo)) { throw "Rust/Cargo не найден: $cargo" }
if (-not (Test-Path $ndkHome)) { throw "Android NDK 29 не найден: $ndkHome" }

$env:JAVA_HOME = $javaHome
$env:ANDROID_HOME = $androidSdk
$env:ANDROID_NDK_HOME = $ndkHome

Push-Location (Join-Path $projectRoot "helper")
try {
    & $cargo @cargoToolchain "ndk" "--target" "arm64-v8a" `
        "--platform" "29" "build" "--release"
    if ($LASTEXITCODE -ne 0) { throw "Сборка Rust root-helper завершилась ошибкой." }
}
finally {
    Pop-Location
}

$helperAssets = Join-Path $projectRoot "agent\app\src\main\assets\helper\arm64-v8a"
New-Item -ItemType Directory -Force -Path $helperAssets | Out-Null
Copy-Item (Join-Path $projectRoot "helper\target\aarch64-linux-android\release\phonebackup-helper") `
    $helperAssets -Force

Push-Location (Join-Path $projectRoot "agent")
try {
    $agentTask = if ($Configuration -eq "Release") { ":app:assembleRelease" } else { ":app:assembleDebug" }
    & ".\gradlew.bat" ":app:testDebugUnitTest" $agentTask "--no-daemon"
    if ($LASTEXITCODE -ne 0) { throw "Сборка Android Agent завершилась ошибкой." }
}
finally {
    Pop-Location
}

New-Item -ItemType Directory -Force -Path $embedded | Out-Null
Copy-Item (Join-Path $androidSdk "platform-tools\adb.exe") $embedded -Force
Copy-Item (Join-Path $androidSdk "platform-tools\AdbWinApi.dll") $embedded -Force
Copy-Item (Join-Path $androidSdk "platform-tools\AdbWinUsbApi.dll") $embedded -Force
$agentApk = if ($Configuration -eq "Release") {
    Join-Path $projectRoot "agent\app\build\outputs\apk\release\app-release.apk"
} else {
    Join-Path $projectRoot "agent\app\build\outputs\apk\debug\app-debug.apk"
}
$apkMetadataPath = if ($Configuration -eq "Release") {
    Join-Path $projectRoot "agent\app\build\outputs\apk\release\output-metadata.json"
} else {
    Join-Path $projectRoot "agent\app\build\outputs\apk\debug\output-metadata.json"
}
if (-not (Test-Path -LiteralPath $agentApk)) {
    throw "Android Agent APK не найден: $agentApk"
}
if (-not (Test-Path -LiteralPath $apkMetadataPath)) {
    throw "Метаданные Android Agent не найдены: $apkMetadataPath"
}
$projectVersion = ([xml](Get-Content -Raw (Join-Path $projectRoot "Directory.Build.props"))).Project.PropertyGroup.Version
$apkMetadata = Get-Content -Raw $apkMetadataPath | ConvertFrom-Json
$apkVersion = $apkMetadata.elements[0].versionName
if ($apkVersion -ne $projectVersion) {
    throw "Версия Android Agent ($apkVersion) не совпадает с версией desktop ($projectVersion)."
}
Copy-Item $agentApk `
    (Join-Path $embedded "phonebackup-agent.apk") -Force

& $dotnet test (Join-Path $projectRoot "tests\PhoneBackup.Core.Tests\PhoneBackup.Core.Tests.csproj") `
    "--configuration" $Configuration "--nologo"
if ($LASTEXITCODE -ne 0) { throw "Core tests завершились ошибкой." }

& $dotnet publish (Join-Path $projectRoot "src\PhoneBackup.Desktop\PhoneBackup.Desktop.csproj") `
    "--configuration" $Configuration "--runtime" "win-x64" "--self-contained" "true" `
    "--output" $publish "--nologo"
if ($LASTEXITCODE -ne 0) { throw "Сборка VeXArk.exe завершилась ошибкой." }

$legacyExecutables = @("PhoneBackup.exe", "MobiArk.exe")
foreach ($legacyName in $legacyExecutables) {
    $legacyExe = Join-Path $publish $legacyName
    if (Test-Path -LiteralPath $legacyExe) {
        try {
            Remove-Item -LiteralPath $legacyExe -Force -ErrorAction Stop
        }
        catch {
            Write-Warning "Старый $legacyName запущен и будет удалён после его закрытия."
        }
    }
}
Get-ChildItem -LiteralPath $publish -Filter "*.pdb" | Remove-Item -Force
Copy-Item (Join-Path $projectRoot "LICENSE") (Join-Path $publish "LICENSE.txt") -Force
Copy-Item (Join-Path $projectRoot "NOTICE") (Join-Path $publish "NOTICE.txt") -Force

$exe = Join-Path $publish "VeXArk.exe"
if ($Configuration -eq "Release") {
    New-Item -ItemType Directory -Force -Path $releaseArtifacts | Out-Null
    $releaseExe = Join-Path $releaseArtifacts "VeXArk.exe"
    $releaseApk = Join-Path $releaseArtifacts "VeXArk-Agent.apk"
    Copy-Item $exe $releaseExe -Force
    Copy-Item $agentApk $releaseApk -Force

    $checksumPath = Join-Path $releaseArtifacts "SHA256SUMS.txt"
    $checksumLines = @($releaseExe, $releaseApk) |
        Sort-Object { Split-Path -Leaf $_ } |
        ForEach-Object {
            $hash = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant()
            "$hash  $(Split-Path -Leaf $_)"
        }
    Set-Content -LiteralPath $checksumPath -Value $checksumLines -Encoding ascii
}
Write-Host ""
Write-Host "Готово: $exe"
Write-Host ("Размер: {0:N1} МБ" -f ((Get-Item $exe).Length / 1MB))
if ($Configuration -eq "Release") {
    Write-Host "Release-артефакты: $releaseArtifacts"
}
