<#
  build-msix.ps1  —  构建并自签名 MSIX（无需管理员）
  流程：Visual Studio MSBuild publish(打包模型自包含) -> 复制清单/图标 -> 生成自签名证书 -> makeappx -> signtool
  产物：packaging\build\WhisperSubtitler_<ver>_x64.msix 与 packaging\WhisperSubtitler_TestCert.cer
  日志：packaging\build\build.log ；结果标记：packaging\build\_result.txt (OK / FAIL:...)
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Version = "1.0.0.0",
    [string]$CertSubject = "CN=WhisperSubtitler Dev"
)
$ErrorActionPreference = "Stop"

$PackagingDir = $PSScriptRoot
$RepoRoot     = Split-Path -Parent $PackagingDir
$Csproj       = Join-Path $RepoRoot "src\WhisperSubtitler\WhisperSubtitler.csproj"
$BuildDir     = Join-Path $PackagingDir "build"
$PayloadDir   = Join-Path $BuildDir "payload"
$MsixPath     = Join-Path $BuildDir "WhisperSubtitler_${Version}_x64.msix"
$CerPath      = Join-Path $PackagingDir "WhisperSubtitler_TestCert.cer"

New-Item -ItemType Directory -Force -Path $BuildDir | Out-Null
$ResultFile = Join-Path $BuildDir "_result.txt"
Remove-Item $ResultFile -ErrorAction SilentlyContinue
Start-Transcript -Path (Join-Path $BuildDir "build.log") -Force | Out-Null

try {
    Write-Host "==> 仓库根目录: $RepoRoot"

    # 1) 定位 SDK 工具 makeappx / signtool（取最新版 x64）
    function Find-SdkTool([string]$name) {
        $roots = @("${env:ProgramFiles(x86)}\Windows Kits\10\bin", "$env:ProgramFiles\Windows Kits\10\bin") |
                 Where-Object { Test-Path $_ }
        $tool = Get-ChildItem -Path $roots -Recurse -Filter $name -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -match '\\x64\\' } |
                Sort-Object FullName -Descending | Select-Object -First 1
        if (-not $tool) { throw "找不到 $name。请安装 Windows 10/11 SDK（随 Visual Studio 桌面/UWP 工作负载附带）。" }
        return $tool.FullName
    }

    function Find-MsBuild {
        $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
        if (-not (Test-Path $vswhere)) {
            throw "找不到 vswhere.exe。请安装 Visual Studio 的 MSBuild/WinUI 开发组件。"
        }
        $tool = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild `
            -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1
        if (-not $tool -or -not (Test-Path $tool)) {
            throw "找不到 Visual Studio MSBuild。请安装 Visual Studio 的 MSBuild/WinUI 开发组件。"
        }
        return $tool
    }

    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw "找不到 dotnet。请安装 .NET 8 SDK。"
    }
    $makeappx = Find-SdkTool "makeappx.exe"
    $signtool = Find-SdkTool "signtool.exe"
    $makepri  = Find-SdkTool "makepri.exe"
    $msbuild  = Find-MsBuild
    Write-Host "==> makeappx : $makeappx"
    Write-Host "==> signtool : $signtool"
    Write-Host "==> MSBuild  : $msbuild"

    # 2) Visual Studio MSBuild publish（打包模型 + 自包含）；MSIX 文件由后续 makeappx 统一封装。
    # WinUI 的 XAML/MRT 目标依赖 Visual Studio 提供的 MSBuild 任务，不能用 dotnet 自带的 MSBuild。
    if (Test-Path $PayloadDir) { Remove-Item $PayloadDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $PayloadDir | Out-Null

    Write-Host "==> MSBuild publish ...（首次会还原 NuGet，较慢）"
    # Rebuild 很重要：同一项目平时按 unpackaged 模型构建，不能复用那套增量输出。
    & $msbuild $Csproj /restore "/t:Rebuild;Publish" `
        "/p:Configuration=$Configuration" /p:Platform=x64 /p:RuntimeIdentifier=win-x64 `
        /p:SelfContained=true /p:WindowsPackageType=MSIX /p:WindowsAppSDKSelfContained=true `
        /p:GenerateAppxPackageOnBuild=false "/p:PublishDir=$PayloadDir\" /v:minimal
    if ($LASTEXITCODE -ne 0) { throw "MSBuild publish 失败（退出码 $LASTEXITCODE）。" }

    # PublishDir 不会自动带上 WinUI 应用自身的 PRI/XBF；缺少它们会在 XAML 初始化时崩溃。
    $projectBin = Join-Path (Split-Path -Parent $Csproj) "bin\x64\$Configuration"
    $appPri = Get-ChildItem $projectBin -Recurse -Filter "WhisperSubtitler.pri" -File `
        -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $appPri) { throw "找不到构建生成的 WhisperSubtitler.pri。" }
    $xamlOutputDir = $appPri.Directory.FullName
    Copy-Item (Join-Path $xamlOutputDir "*.pri") $PayloadDir -Force
    Copy-Item (Join-Path $xamlOutputDir "*.xbf") $PayloadDir -Force

    # 3) 复制清单与磁贴图标到 payload 根，并把版本号写入清单
    Copy-Item (Join-Path $PackagingDir "AppxManifest.xml") $PayloadDir -Force
    $manifestPath = Join-Path $PayloadDir "AppxManifest.xml"
    (Get-Content $manifestPath -Raw -Encoding UTF8) `
        -replace '(<Identity\b[^>]*\bVersion=")[^"]*(")', "`${1}$Version`${2}" |
        Set-Content $manifestPath -Encoding UTF8
    Copy-Item (Join-Path $PackagingDir "Images") (Join-Path $PayloadDir "Images") -Recurse -Force

    # 3b) 生成 resources.pri：合并应用自身 PRI 并索引 Images 的
    #     scale-*/targetsize-*/altform-* 变体；没有 PRI 索引时，开始菜单只能
    #     拉伸单张 44px 位图（图标模糊的根源）。失败则回退为直接复用应用 PRI。
    # 注意：不能直接扫描整个 payload —— Microsoft.UI.Xaml.pri 与其散置的
    # Assets PNG 会触发 Duplicate Entry (PRI175)。只把需要索引的内容放进暂存目录。
    $PriStaging = Join-Path $BuildDir "pri-staging"
    if (Test-Path $PriStaging) { Remove-Item $PriStaging -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $PriStaging | Out-Null
    Copy-Item $manifestPath $PriStaging -Force
    Copy-Item (Join-Path $PackagingDir "Images") (Join-Path $PriStaging "Images") -Recurse -Force
    Copy-Item $appPri.FullName (Join-Path $PriStaging "WhisperSubtitler.pri") -Force

    $priConfig = Join-Path $BuildDir "priconfig.xml"
    & $makepri createconfig /cf $priConfig /dq lang-en-US_scale-100 /pv 10.0.0 /o
    if ($LASTEXITCODE -eq 0) {
        # 删除 <packaging> 节：默认配置会把 scale/语言候选拆成 resources.scale-*.pri
        # 资源包（仅 bundle 有用）；单 .msix 只加载 resources.pri，必须全部合入一个文件。
        (Get-Content $priConfig -Raw) -replace '(?s)<packaging>.*?</packaging>', '' |
            Set-Content $priConfig -Encoding UTF8
        Remove-Item (Join-Path $PayloadDir "resources*.pri") -Force -ErrorAction SilentlyContinue
        & $makepri new /pr $PriStaging /cf $priConfig /mn (Join-Path $PriStaging "AppxManifest.xml") `
            /of (Join-Path $PayloadDir "resources.pri") /o
    }
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path (Join-Path $PayloadDir "resources.pri"))) {
        Write-Host "==> makepri 失败，回退为复用应用 PRI（图标将无高分变体）" -ForegroundColor Yellow
        Copy-Item $appPri.FullName (Join-Path $PayloadDir "resources.pri") -Force
    }
    $backendPayload = Join-Path $PayloadDir "backend"
    New-Item -ItemType Directory -Force -Path $backendPayload | Out-Null
    foreach ($name in @("transcribe.py", "requirements.txt", "setup.bat", "setup.ps1")) {
        Copy-Item (Join-Path $RepoRoot "backend\$name") $backendPayload -Force
    }
    Get-ChildItem $PayloadDir -Filter *.pdb -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

    # 4) 确保存在自签名代码签名证书（CurrentUser\My）
    $cert = Get-ChildItem Cert:\CurrentUser\My |
            Where-Object { $_.Subject -eq $CertSubject } | Select-Object -First 1
    if (-not $cert) {
        Write-Host "==> 创建自签名代码签名证书: $CertSubject"
        $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $CertSubject `
            -KeyUsage DigitalSignature -FriendlyName "WhisperSubtitler Dev Cert" `
            -CertStoreLocation "Cert:\CurrentUser\My" `
            -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}") `
            -NotAfter (Get-Date).AddYears(3)
    } else {
        Write-Host "==> 复用已有证书"
    }
    Write-Host "==> 证书指纹: $($cert.Thumbprint)"
    Export-Certificate -Cert $cert -FilePath $CerPath -Force | Out-Null
    Write-Host "==> 已导出公钥证书: $CerPath"

    # 5) makeappx 打包
    if (Test-Path $MsixPath) { Remove-Item $MsixPath -Force }
    Write-Host "==> makeappx pack ..."
    & $makeappx pack /o /d $PayloadDir /p $MsixPath
    if ($LASTEXITCODE -ne 0) { throw "makeappx pack 失败（退出码 $LASTEXITCODE）。" }

    # 6) 签名
    Write-Host "==> signtool 签名 ..."
    & $signtool sign /fd SHA256 /sha1 $cert.Thumbprint $MsixPath
    if ($LASTEXITCODE -ne 0) { throw "signtool 签名失败（退出码 $LASTEXITCODE）。" }

    Write-Host ""
    Write-Host "==================================================================="
    Write-Host " 打包完成"
    Write-Host "   MSIX : $MsixPath"
    Write-Host "   证书 : $CerPath"
    Write-Host " 下一步：右键 install-msix.bat -> 以管理员身份运行"
    Write-Host "==================================================================="
    "OK" | Set-Content -Path $ResultFile -Encoding UTF8
}
catch {
    Write-Host ""
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    "FAIL: $($_.Exception.Message)" | Set-Content -Path $ResultFile -Encoding UTF8
}
finally {
    try { Stop-Transcript | Out-Null } catch {}
}
