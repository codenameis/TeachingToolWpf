param([switch]$DependenciesOnly)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$root = Join-Path $repo 'artifacts/streaming-codecs'
$rid = if ($IsWindows) { 'win-x64' } elseif ($IsLinux) { 'linux-x64' } else { throw 'Windows/Linux x64만 지원합니다.' }
if ([Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne 'X64') { throw 'x64 빌드 호스트가 필요합니다.' }
$prefix = Join-Path $root "$rid/install"
$source = Join-Path $root 'sources'
New-Item -ItemType Directory -Force -Path "$root/downloads",$source,$prefix | Out-Null

if ($IsWindows) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    $vs = & $vswhere -latest -prerelease -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $vs) { throw 'Visual Studio C++ 빌드 도구가 필요합니다.' }
    & (Join-Path $vs 'Common7/Tools/Launch-VsDevShell.ps1') -Arch amd64 -HostArch amd64 -SkipAutomaticLocation | Out-Null
    $cmake = Join-Path $vs 'Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe'
    $env:PATH = (Join-Path $vs 'Common7/IDE/CommonExtensions/Microsoft/CMake/Ninja') + [IO.Path]::PathSeparator + $env:PATH
} else { $cmake = (Get-Command cmake -ErrorAction Stop).Source }

# 버전·해시를 고정하고 기존 결과를 삭제하지 않는다. 공급자 소스는 artifacts 안에서만 빌드한다.
$dependencies = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'native/dependencies.json') -Raw | ConvertFrom-Json
foreach ($dependency in $dependencies) {
    if ($dependency.windowsOnly -and -not $IsWindows) { continue }
    $archive = Join-Path "$root/downloads" $dependency.archive
    if (-not (Test-Path -LiteralPath $archive)) { Invoke-WebRequest -Uri $dependency.url -OutFile $archive }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $dependency.sha256) { throw "Checksum mismatch: $($dependency.name)" }
    if (-not (Test-Path -LiteralPath (Join-Path $source $dependency.name))) {
        if ($dependency.archive.EndsWith('.zip')) { Expand-Archive -LiteralPath $archive -DestinationPath $source }
        else { & tar -xzf $archive -C $source; if ($LASTEXITCODE -ne 0) { throw 'Extraction failed.' } }
    }
}
if ($IsWindows) { $env:PATH = (Join-Path $source 'nasm-2.16.03') + [IO.Path]::PathSeparator + $env:PATH }
Get-Command nasm -ErrorAction Stop | Out-Null

function Build-Library([string]$name, [string[]]$options) {
    $build = Join-Path $root "$rid/build/$name"
    & $cmake -S (Join-Path $source $name) -B $build -G Ninja '-DCMAKE_BUILD_TYPE=Release' "-DCMAKE_INSTALL_PREFIX=$prefix" "-DCMAKE_PREFIX_PATH=$prefix" '-DCMAKE_INSTALL_LIBDIR=lib' '-DCMAKE_POSITION_INDEPENDENT_CODE=ON' @options
    if ($LASTEXITCODE -ne 0) { throw "Configure failed: $name" }
    & $cmake --build $build --parallel 6
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $name" }
    & $cmake --install $build
    if ($LASTEXITCODE -ne 0) { throw "Install failed: $name" }
}
Build-Library 'zlib-1.3.2' @('-DZLIB_BUILD_SHARED=OFF','-DZLIB_BUILD_TESTING=OFF')
Build-Library 'libjpeg-turbo-3.2.0' @('-DENABLE_SHARED=OFF','-DENABLE_STATIC=ON','-DWITH_TURBOJPEG=OFF','-DWITH_TOOLS=OFF','-DWITH_TESTS=OFF','-DWITH_SIMD=ON')
Build-Library 'libpng-1.6.59' @('-DPNG_SHARED=OFF','-DPNG_TESTS=OFF','-DPNG_TOOLS=OFF','-DZLIB_USE_STATIC_LIBS=ON')
Build-Library 'tiff-4.7.2' @('-DBUILD_SHARED_LIBS=OFF','-Dtiff-tools=OFF','-Dtiff-tests=OFF','-Dtiff-contrib=OFF','-Dtiff-docs=OFF','-Dzlib=ON','-Djpeg=OFF','-Dlerc=OFF','-Dlibdeflate=OFF','-Dlzma=OFF','-Dzstd=OFF','-Dwebp=OFF','-Djbig=OFF','-DZLIB_USE_STATIC_LIBS=ON')
Build-Library 'libwebp-1.6.0' @('-DBUILD_SHARED_LIBS=OFF','-DWEBP_BUILD_ANIM_UTILS=OFF','-DWEBP_BUILD_CWEBP=OFF','-DWEBP_BUILD_DWEBP=OFF','-DWEBP_BUILD_GIF2WEBP=OFF','-DWEBP_BUILD_IMG2WEBP=OFF','-DWEBP_BUILD_VWEBP=OFF','-DWEBP_BUILD_WEBPINFO=OFF','-DWEBP_BUILD_LIBWEBPMUX=OFF','-DWEBP_BUILD_WEBPMUX=OFF','-DWEBP_BUILD_EXTRAS=OFF','-DWEBP_BUILD_FUZZTEST=OFF')
if ($DependenciesOnly) { return }
$shimBuild = Join-Path $root "$rid/build/shim"
& $cmake -S (Join-Path $PSScriptRoot 'native') -B $shimBuild -G Ninja '-DCMAKE_BUILD_TYPE=Release' "-DCMAKE_PREFIX_PATH=$prefix" "-DCMAKE_INSTALL_PREFIX=$root/$rid/runtime"
if ($LASTEXITCODE -ne 0) { throw 'Shim configure failed.' }
& $cmake --build $shimBuild --parallel 6
if ($LASTEXITCODE -ne 0) { throw 'Shim build failed.' }
& $cmake --install $shimBuild
if ($LASTEXITCODE -ne 0) { throw 'Shim install failed.' }
