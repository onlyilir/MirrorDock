# Builds MirrorDock: the phone engine (Java -> dex) and the PC app (C#).
# Needs: Windows' built-in C# compiler (always there) and an Android SDK + JDK.
# It finds the SDK that Unity installs; or set ANDROID_SDK and JAVA_HOME yourself.
# Usage:  powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$out = Join-Path $root "dist"
$tmp = Join-Path $root "build"
New-Item -ItemType Directory -Force $out, $tmp | Out-Null

# ---------- locate tools
$unity = Get-ChildItem "C:\Program Files\Unity\Hub\Editor\*\Editor\Data\PlaybackEngines\AndroidPlayer" -Directory -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending | Select-Object -First 1
$sdk = $env:ANDROID_SDK
if (-not $sdk -and $unity) { $sdk = Join-Path $unity.FullName "SDK" }
if (-not $sdk) { $sdk = Join-Path $env:LOCALAPPDATA "Android\Sdk" }
$jdk = $env:JAVA_HOME
if (-not $jdk -and $unity) { $jdk = Join-Path $unity.FullName "OpenJDK" }
$javac = Join-Path $jdk "bin\javac.exe"
$androidJar = Get-ChildItem "$sdk\platforms\android-*\android.jar" | Sort-Object { [int]($_.Directory.Name -replace '\D', '') } -Descending | Select-Object -First 1
$d8 = Get-ChildItem "$sdk\build-tools\*\d8.bat" | Sort-Object FullName -Descending | Select-Object -First 1
foreach ($t in @($javac, $androidJar, $d8)) { if (-not $t -or -not (Test-Path $t)) { throw "Missing build tool: $t" } }
$env:JAVA_HOME = $jdk

# ---------- 1. phone engine
$classes = Join-Path $tmp "classes"
Remove-Item -Recurse -Force $classes -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $classes | Out-Null
$java = Get-ChildItem (Join-Path $root "server\src"), (Join-Path $root "server\stubs") -Recurse -Filter *.java | ForEach-Object { $_.FullName }
& $javac -nowarn -Xlint:-options -source 8 -target 8 -bootclasspath $androidJar.FullName -d $classes @java
if ($LASTEXITCODE -ne 0) { throw "Java build failed" }
$serverJar = Join-Path $tmp "mirrordock-server.jar"
Remove-Item $serverJar -ErrorAction SilentlyContinue
# Only our own classes go into the engine; server\stubs are compile-time stand-ins for hidden Android classes.
$classFiles = Get-ChildItem (Join-Path $classes "com") -Recurse -Filter *.class | ForEach-Object { $_.FullName }
& $d8.FullName --release --min-api 24 --lib $androidJar.FullName --output $serverJar @classFiles
if ($LASTEXITCODE -ne 0) { throw "d8 failed" }
Write-Host "Built phone engine ($((Get-Item $serverJar).Length) bytes)"

# ---------- 2. adb (Google platform-tools) next to the exe
$adbDir = Join-Path $out "adb"
New-Item -ItemType Directory -Force $adbDir | Out-Null
foreach ($f in "adb.exe", "AdbWinApi.dll", "AdbWinUsbApi.dll", "NOTICE.txt") {
    $src = Join-Path "$sdk\platform-tools" $f
    if ((Test-Path $src) -and -not (Test-Path (Join-Path $adbDir $f))) { Copy-Item $src $adbDir }
}

# ---------- 3. PC app (engine jar embedded as a resource)
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { $csc = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe" }
$src = Get-ChildItem (Join-Path $root "src") -Filter *.cs | ForEach-Object { $_.FullName }
$refs = "/unsafe", "/r:System.dll", "/r:System.Core.dll", "/r:System.Drawing.dll", "/r:System.Windows.Forms.dll", "/r:System.Management.dll"
$exe = Join-Path $out "MirrorDock.exe"
$ico = Join-Path $tmp "MirrorDock.ico"
$res = "/resource:$serverJar,mirrordock-server.jar"
if (-not (Test-Path $ico)) {
    & $csc /nologo /target:winexe /optimize+ /out:$exe $res @refs @src
    if ($LASTEXITCODE -ne 0) { throw "C# build failed" }
    Start-Process -FilePath $exe -ArgumentList "--make-icon", "`"$ico`"" -Wait
}
$manifest = Join-Path $root "src\app.manifest"
& $csc /nologo /target:winexe /optimize+ /win32icon:$ico /win32manifest:$manifest /out:$exe $res @refs @src
if ($LASTEXITCODE -ne 0) { throw "C# build failed" }
Write-Host "Built $exe"

# ---------- 4. installer + portable zip + checksums  (only with -Release)
if ($args -contains "-Release") {
    $ver = (Select-String -Path (Join-Path $root "src\MainForm.cs") -Pattern 'Version = "([^"]+)"').Matches[0].Groups[1].Value
    $rel = Join-Path $out "release"
    Remove-Item -Recurse -Force $rel -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $rel | Out-Null
    Copy-Item (Join-Path $root "LICENSE") (Join-Path $out "LICENSE.txt") -Force

    $payload = @("/resource:$exe,payload/MirrorDock.exe", "/resource:$(Join-Path $out 'LICENSE.txt'),payload/LICENSE.txt")
    foreach ($f in Get-ChildItem $adbDir -File) { $payload += "/resource:$($f.FullName),payload/adb/$($f.Name)" }
    $setupSrc = (Get-ChildItem (Join-Path $root "src") -Filter *.cs | Where-Object { $_.Name -ne "Program.cs" } | ForEach-Object { $_.FullName }) + (Join-Path $root "installer\Setup.cs")
    $setup = Join-Path $rel "MirrorDock-$ver-Setup.exe"
    & $csc /nologo /target:winexe /optimize+ /win32icon:$ico /win32manifest:$manifest /out:$setup @payload @refs @setupSrc
    if ($LASTEXITCODE -ne 0) { throw "Installer build failed" }
    Write-Host "Built $setup"

    $stage = Join-Path $tmp "portable\MirrorDock"
    Remove-Item -Recurse -Force (Join-Path $tmp "portable") -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force (Join-Path $stage "adb") | Out-Null
    Copy-Item $exe, (Join-Path $out "LICENSE.txt") $stage
    Copy-Item (Join-Path $adbDir "*") (Join-Path $stage "adb")
    Set-Content (Join-Path $stage "README.txt") -Encoding ascii -Value @(
        "MirrorDock $ver (portable)", "",
        "Double-click MirrorDock.exe. Keep the adb folder next to it.",
        "Settings are saved in %APPDATA%\MirrorDock.",
        "Help and updates: https://github.com/onlyilir/MirrorDock")
    $zip = Join-Path $rel "MirrorDock-$ver-portable.zip"
    # Build the zip entry by entry so paths use "/" (CreateFromDirectory on .NET 4.x writes "\", which some unzip tools mishandle).
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $base = Join-Path $tmp "portable"
    $za = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($f in Get-ChildItem $base -Recurse -File) {
            $entry = $f.FullName.Substring($base.Length + 1).Replace('\', '/')
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($za, $f.FullName, $entry, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally { $za.Dispose() }
    Write-Host "Built $zip"

    $sums = Get-ChildItem $rel -File | ForEach-Object { (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower() + "  " + $_.Name }
    Set-Content (Join-Path $rel "SHA256SUMS.txt") -Encoding ascii -Value $sums
    $sums | ForEach-Object { Write-Host $_ }
}
