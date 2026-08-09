param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [ValidateSet("Stable", "Dev")]
    [string]$Channel = "Stable"
)

$ErrorActionPreference = "Stop"

$localSigning = Join-Path $PSScriptRoot "load-signing-env.ps1"
if (-not $env:VEXARK_KEYSTORE_PATH -and (Test-Path -LiteralPath $localSigning)) {
    . $localSigning
}
$projectRoot = Split-Path -Parent $PSScriptRoot
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnetCandidates = @(
    if ($env:VEXARK_DOTNET) { $env:VEXARK_DOTNET }
    (Join-Path $env:USERPROFILE ".dotnet\dotnet.exe")
    (Join-Path $env:TEMP "vexark-dotnet-sdk-9\dotnet.exe")
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
$javaCandidates = @(
    if ($env:JAVA_HOME) { $env:JAVA_HOME }
    "C:\Program Files\Android\Android Studio\jbr"
    (Join-Path $env:USERPROFILE ".gradle\jdks\jetbrains_s_r_o_-21-amd64-windows.2")
) | Select-Object -Unique
$javaHome = $javaCandidates |
    Where-Object {
        $javaExe = Join-Path $_ "bin\java.exe"
        if (-not (Test-Path -LiteralPath $javaExe)) { return $false }
        return (Get-Item -LiteralPath $javaExe).VersionInfo.ProductMajorPart -ge 17
    } |
    Select-Object -First 1
$embedded = Join-Path $projectRoot "src\PhoneBackup.Desktop\Embedded"
$publishFinal = if ($Channel -eq "Dev") {
    Join-Path $projectRoot "artifacts\dev\publish"
} else {
    Join-Path $projectRoot "artifacts\publish"
}
$artifactsRoot = Join-Path $projectRoot "artifacts"
$releaseArtifacts = Join-Path $projectRoot "artifacts\release"
$devArtifacts = Join-Path $projectRoot "artifacts\dev"
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
if ($Channel -eq "Stable" -and $Configuration -eq "Release") {
    $requiredSigning = @(
        "VEXARK_KEYSTORE_PATH",
        "VEXARK_KEYSTORE_PASSWORD",
        "VEXARK_KEY_ALIAS",
        "VEXARK_KEY_PASSWORD"
    )
    $missingSigning = $requiredSigning | Where-Object {
        [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($_))
    }
    if ($missingSigning.Count -gt 0) {
        throw "Stable Release требует production signing: отсутствуют " +
              ($missingSigning -join ", ")
    }
    if (-not (Test-Path -LiteralPath $env:VEXARK_KEYSTORE_PATH)) {
        throw "Production keystore не найден: $env:VEXARK_KEYSTORE_PATH"
    }
}

$gitSha = (& git -C $projectRoot rev-parse --short=12 HEAD 2>$null).Trim()
if ([string]::IsNullOrWhiteSpace($gitSha)) { $gitSha = "local" }
$gitDirty = -not [string]::IsNullOrWhiteSpace(
    ((& git -C $projectRoot status --porcelain 2>$null) -join "`n"))
$buildId = if ($gitDirty) { "$gitSha-dirty" } else { $gitSha }

$env:JAVA_HOME = $javaHome
$env:ANDROID_HOME = $androidSdk
$env:ANDROID_NDK_HOME = $ndkHome
$env:VEXARK_BUILD_ID = $buildId

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
    $agentTask = if ($Channel -eq "Dev") {
        ":app:assembleDev"
    } elseif ($Configuration -eq "Release") {
        ":app:assembleRelease"
    } else {
        ":app:assembleDebug"
    }
    & ".\gradlew.bat" ":app:testDebugUnitTest" ":app:lintDebug" $agentTask "--no-daemon"
    if ($LASTEXITCODE -ne 0) { throw "Сборка Android Agent завершилась ошибкой." }
}
finally {
    Pop-Location
}

New-Item -ItemType Directory -Force -Path $embedded | Out-Null
Copy-Item (Join-Path $androidSdk "platform-tools\adb.exe") $embedded -Force
Copy-Item (Join-Path $androidSdk "platform-tools\AdbWinApi.dll") $embedded -Force
Copy-Item (Join-Path $androidSdk "platform-tools\AdbWinUsbApi.dll") $embedded -Force
$agentApk = if ($Channel -eq "Dev") {
    Join-Path $projectRoot "agent\app\build\outputs\apk\dev\app-dev.apk"
} elseif ($Configuration -eq "Release") {
    Join-Path $projectRoot "agent\app\build\outputs\apk\release\app-release.apk"
} else {
    Join-Path $projectRoot "agent\app\build\outputs\apk\debug\app-debug.apk"
}
$apkMetadataPath = if ($Channel -eq "Dev") {
    Join-Path $projectRoot "agent\app\build\outputs\apk\dev\output-metadata.json"
} elseif ($Configuration -eq "Release") {
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
if ($Channel -eq "Stable" -and $Configuration -eq "Release") {
    $expectedSigningCertificate = if ($env:VEXARK_STABLE_SIGNING_CERT_SHA256) {
        $env:VEXARK_STABLE_SIGNING_CERT_SHA256.Trim().ToLowerInvariant()
    } else {
        "1f90562d1d034944be01148e8b09be10c1dc54b37415ef87bc4a18ac10c52489"
    }
    $apksigner = Get-ChildItem -LiteralPath (Join-Path $androidSdk "build-tools") `
        -Directory |
        Sort-Object { [version]$_.Name } -Descending |
        ForEach-Object { Join-Path $_.FullName "apksigner.bat" } |
        Where-Object { Test-Path -LiteralPath $_ } |
        Select-Object -First 1
    if (-not $apksigner) {
        throw "apksigner не найден в Android SDK: $androidSdk"
    }
    $signerOutput = (& $apksigner verify --print-certs-pem $agentApk 2>&1) -join "`n"
    if ($LASTEXITCODE -ne 0) {
        throw "Проверка подписи Android Agent завершилась ошибкой: $signerOutput"
    }
    $certificateMatch = [regex]::Match(
        $signerOutput,
        "(?s)-----BEGIN CERTIFICATE-----\s*" +
        "(?<base64>[A-Za-z0-9+/=\r\n]+?)\s*" +
        "-----END CERTIFICATE-----")
    if (-not $certificateMatch.Success) {
        throw "apksigner не вернул PEM-сертификат Android Agent."
    }
    $certificateBytes = [Convert]::FromBase64String(
        ($certificateMatch.Groups["base64"].Value -replace "\s", ""))
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $actualSigningCertificate = (
            $sha256.ComputeHash($certificateBytes) |
                ForEach-Object { $_.ToString("x2") }) -join ""
    }
    finally {
        $sha256.Dispose()
    }
    if ($actualSigningCertificate -ne $expectedSigningCertificate) {
        throw "APK подписан неверным сертификатом: $actualSigningCertificate; " +
              "stable ожидает $expectedSigningCertificate. Публикация заблокирована."
    }
}
$projectProperties = [xml](Get-Content -Raw (Join-Path $projectRoot "Directory.Build.props"))
$projectVersion = @($projectProperties.Project.PropertyGroup.Version) |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
    Select-Object -First 1
$projectVersion = $projectVersion.Trim()
$stagingRoot = Join-Path $artifactsRoot "staging"
$stageName = "{0}-{1}-{2}" -f $Channel.ToLowerInvariant(), $projectVersion, $buildId
$publish = Join-Path $stagingRoot $stageName
New-Item -ItemType Directory -Force -Path $stagingRoot | Out-Null
$resolvedArtifacts = [IO.Path]::GetFullPath($artifactsRoot) + [IO.Path]::DirectorySeparatorChar
$resolvedPublish = [IO.Path]::GetFullPath($publish)
if (-not $resolvedPublish.StartsWith($resolvedArtifacts, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Небезопасный staging path: $resolvedPublish"
}
if (Test-Path -LiteralPath $publish) {
    Remove-Item -LiteralPath $publish -Recurse -Force
}
$apkMetadata = Get-Content -Raw $apkMetadataPath | ConvertFrom-Json
$apkVersion = $apkMetadata.elements[0].versionName
$expectedApkVersion = if ($Channel -eq "Dev") { "$projectVersion-dev" } else { $projectVersion }
if ($apkVersion -ne $expectedApkVersion) {
    throw "Версия Android Agent ($apkVersion) не совпадает с версией desktop ($projectVersion)."
}
if ($Channel -eq "Dev" -and
    $apkMetadata.applicationId -ne "com.vex.phonebackup.agent.dev") {
    throw "Dev Agent собран с неожиданным package ID: $($apkMetadata.applicationId)"
}
Copy-Item $agentApk `
    (Join-Path $embedded "phonebackup-agent.apk") -Force

& $dotnet test (Join-Path $projectRoot "tests\PhoneBackup.Core.Tests\PhoneBackup.Core.Tests.csproj") `
    "--configuration" $Configuration "--nologo"
if ($LASTEXITCODE -ne 0) { throw "Core tests завершились ошибкой." }

& $dotnet test (Join-Path $projectRoot "tests\PhoneBackup.Desktop.Tests\PhoneBackup.Desktop.Tests.csproj") `
    "--configuration" $Configuration "--nologo" `
    "-p:VeXArkChannel=Dev" "-p:VeXArkBuildId=$buildId"
if ($LASTEXITCODE -ne 0) { throw "Desktop tests завершились ошибкой." }

& $dotnet publish (Join-Path $projectRoot "src\PhoneBackup.Desktop\PhoneBackup.Desktop.csproj") `
    "--configuration" $Configuration "--runtime" "win-x64" "--self-contained" "true" `
    "--output" $publish "--nologo" `
    "-p:VeXArkChannel=$Channel" "-p:VeXArkBuildId=$buildId"
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
if ($Channel -ne "Dev") {
    Get-ChildItem -LiteralPath $publish -Filter "*.pdb" | Remove-Item -Force
}
Copy-Item (Join-Path $projectRoot "LICENSE") (Join-Path $publish "LICENSE.txt") -Force
Copy-Item (Join-Path $projectRoot "NOTICE") (Join-Path $publish "NOTICE.txt") -Force

$exeName = if ($Channel -eq "Dev") { "VeXArk-Dev.exe" } else { "VeXArk.exe" }
$exe = Join-Path $publish $exeName
$previousPublish = "$publishFinal.previous-$buildId"
$publishFinalParent = Split-Path -Parent $publishFinal
New-Item -ItemType Directory -Force -Path $publishFinalParent | Out-Null
$resolvedPrevious = [IO.Path]::GetFullPath($previousPublish)
if (-not $resolvedPrevious.StartsWith($resolvedArtifacts, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Небезопасный previous publish path: $resolvedPrevious"
}
if (Test-Path -LiteralPath $previousPublish) {
    Remove-Item -LiteralPath $previousPublish -Recurse -Force
}
$movedPrevious = $false
try {
    if (Test-Path -LiteralPath $publishFinal) {
        Move-Item -LiteralPath $publishFinal -Destination $previousPublish
        $movedPrevious = $true
    }
    Move-Item -LiteralPath $publish -Destination $publishFinal
}
catch {
    if ($movedPrevious -and
        -not (Test-Path -LiteralPath $publishFinal) -and
        (Test-Path -LiteralPath $previousPublish)) {
        Move-Item -LiteralPath $previousPublish -Destination $publishFinal
    }
    throw "Не удалось безопасно заменить publish. Закройте VeXArk и повторите сборку. " +
          "Готовый staging сохранён в $publish. $($_.Exception.Message)"
}
if (Test-Path -LiteralPath $previousPublish) {
    Remove-Item -LiteralPath $previousPublish -Recurse -Force
}
$publish = $publishFinal
$exe = Join-Path $publish $exeName
if ($Channel -eq "Dev") {
    New-Item -ItemType Directory -Force -Path $devArtifacts | Out-Null
    $devExe = Join-Path $devArtifacts "VeXArk-Dev.exe"
    $devApk = Join-Path $devArtifacts "VeXArk-Agent-Dev.apk"
    Copy-Item $exe $devExe -Force
    Copy-Item $agentApk $devApk -Force

    $checksumPath = Join-Path $devArtifacts "SHA256SUMS.txt"
    $checksumLines = @($devExe, $devApk) |
        Sort-Object { Split-Path -Leaf $_ } |
        ForEach-Object {
            $hash = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant()
            "$hash  $(Split-Path -Leaf $_)"
        }
    Set-Content -LiteralPath $checksumPath -Value $checksumLines -Encoding ascii
} elseif ($Configuration -eq "Release") {
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
if ($Channel -eq "Dev") {
    Write-Host "Dev-артефакты: $devArtifacts"
    Write-Host "Build ID: $buildId"
} elseif ($Configuration -eq "Release") {
    Write-Host "Release-артефакты: $releaseArtifacts"
}
