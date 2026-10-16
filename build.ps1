# Builds dist\Nomi.msi: the app, the llama.cpp engine and the local model in a single installer
# that works offline. Only what ships with Windows is used to build (.NET Framework csc, msi.dll,
# makecab). The engine and the model are downloaded once into vendor\ (checked by SHA-256);
# after that the build itself needs no connection either.
param([switch]$SkipMsi)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = $PSScriptRoot
$obj = Join-Path $root 'obj'
$dist = Join-Path $root 'dist'
$vendor = Join-Path $root 'vendor'
$stage = Join-Path $obj 'stage'
New-Item -ItemType Directory -Force $obj, $dist, $vendor | Out-Null

# ---------------------------------------------------------------- third-party files (pinned)
$llamaBuild = 'b11685'
$downloads = @(
    @{ Name = "llama-$llamaBuild-bin-win-cpu-x64.zip"
       Url = "https://github.com/ggml-org/llama.cpp/releases/download/$llamaBuild/llama-$llamaBuild-bin-win-cpu-x64.zip"
       Sha = '3AC857118B17B5403FECA3AAA9E02D3048B51E9D00630DCCD965B224A474B4EC' },
    @{ Name = 'Qwen3-4B-Instruct-2507-UD-IQ3_XXS.gguf'
       Url = 'https://huggingface.co/unsloth/Qwen3-4B-Instruct-2507-GGUF/resolve/main/Qwen3-4B-Instruct-2507-UD-IQ3_XXS.gguf'
       Sha = 'C886EFFC2F09E63BBF9419A8136FAB49DBA22A578C24885F63C5F483DE75AD3E' },
    @{ Name = 'LICENSE-llama.cpp.txt'
       Url = "https://raw.githubusercontent.com/ggml-org/llama.cpp/$llamaBuild/LICENSE"; Sha = $null },
    @{ Name = 'LICENSE-Qwen3.txt'
       Url = 'https://huggingface.co/Qwen/Qwen3-4B-Instruct-2507/resolve/main/LICENSE'; Sha = $null }
)
foreach ($d in $downloads) {
    $file = Join-Path $vendor $d.Name
    if (-not (Test-Path $file)) {
        Write-Host "Téléchargement de $($d.Name)..."
        Invoke-WebRequest -UseBasicParsing $d.Url -OutFile "$file.part"
        Move-Item "$file.part" $file -Force
    }
    if ($d.Sha) {
        $hash = (Get-FileHash -Algorithm SHA256 $file).Hash
        if ($hash -ne $d.Sha) { Remove-Item $file; throw "Empreinte SHA-256 incorrecte pour $($d.Name) (fichier supprimé, relancez le build)" }
    }
}

# ---------------------------------------------------------------- icon
Write-Host 'Icône...'
& (Join-Path $root 'build\make-icon.ps1') -Path (Join-Path $obj 'app.ico')

# ---------------------------------------------------------------- application
Write-Host 'Application...'
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $fw 'csc.exe'
$wpf = Join-Path $fw 'WPF'
$winmd = Join-Path $env:WINDIR 'System32\WinMetadata'
$sources = Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object { $_.FullName }
$xaml = Get-ChildItem (Join-Path $root 'src\ui') -Filter *.xaml | ForEach-Object { "/resource:$($_.FullName),Nomi.$($_.Name)" }
$appExe = Join-Path $obj 'Nomi.exe'
& $csc /nologo /target:winexe /optimize+ /codepage:65001 /platform:x64 /warn:4 /nowarn:1998 `
    "/out:$appExe" "/win32icon:$obj\app.ico" "/win32manifest:$root\src\app.manifest" `
    "/resource:$obj\app.ico,Nomi.app.ico" $xaml "/lib:$wpf" `
    /r:PresentationFramework.dll /r:PresentationCore.dll /r:WindowsBase.dll /r:System.Xaml.dll `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:Microsoft.CSharp.dll /r:System.Core.dll `
    /r:System.Web.Extensions.dll /r:System.Net.dll `
    "/r:$fw\System.Runtime.dll" "/r:$fw\System.Runtime.InteropServices.WindowsRuntime.dll" "/r:$fw\System.Threading.Tasks.dll" `
    "/r:$winmd\Windows.Foundation.winmd" "/r:$winmd\Windows.Graphics.winmd" "/r:$winmd\Windows.Media.winmd" `
    "/r:$winmd\Windows.Storage.winmd" "/r:$winmd\Windows.Globalization.winmd" `
    $sources
if ($LASTEXITCODE) { throw 'Échec de la compilation de Nomi' }

# ---------------------------------------------------------------- staging
Write-Host 'Préparation des fichiers...'
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
$engineDir = Join-Path $stage 'engine'
$modelDir = Join-Path $stage 'models'
$licenseDir = Join-Path $stage 'licenses'
New-Item -ItemType Directory -Force $engineDir, $modelDir, $licenseDir | Out-Null
Copy-Item $appExe $stage
Copy-Item (Join-Path $root 'src\app.config') (Join-Path $stage 'Nomi.exe.config')

$llamaDir = Join-Path $obj 'llama'
if (Test-Path $llamaDir) { Remove-Item -Recurse -Force $llamaDir }
Expand-Archive (Join-Path $vendor "llama-$llamaBuild-bin-win-cpu-x64.zip") $llamaDir
# Only the server and what it loads (every CPU variant, picked at run time for the processor).
$engineFiles = @('llama-server.exe', 'llama-server-impl.dll', 'llama-common.dll', 'llama.dll', 'mtmd.dll',
                 'ggml.dll', 'ggml-base.dll', 'libomp.dll') + (Get-ChildItem $llamaDir -Filter 'ggml-cpu-*.dll' | ForEach-Object Name)
foreach ($f in $engineFiles) { Copy-Item (Join-Path $llamaDir $f) $engineDir }
# Visual C++ runtime, deployed next to the engine so nothing has to be installed on the machine.
foreach ($f in 'msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll') {
    Copy-Item (Join-Path $env:WINDIR "System32\$f") $engineDir
}
Copy-Item (Join-Path $vendor 'Qwen3-4B-Instruct-2507-UD-IQ3_XXS.gguf') $modelDir
Copy-Item (Join-Path $vendor 'LICENSE-llama.cpp.txt') $licenseDir
Copy-Item (Join-Path $vendor 'LICENSE-Qwen3.txt') $licenseDir
Copy-Item (Join-Path $llamaDir 'LICENSE-LLVM-OpenMP') (Join-Path $licenseDir 'LICENSE-LLVM-OpenMP.txt')
Copy-Item (Join-Path $root 'THIRD-PARTY.txt') $licenseDir

if ($SkipMsi) { Write-Host "Fichiers prêts dans $stage"; return }

# ---------------------------------------------------------------- installer
Write-Host 'Installeur (quelques minutes, le modèle pèse 1,6 Go)...'
$builder = Join-Path $obj 'MsiBuilder.exe'
& $csc /nologo /target:exe /optimize+ /codepage:65001 /platform:x64 "/out:$builder" (Join-Path $root 'installer\MsiBuilder.cs')
if ($LASTEXITCODE) { throw 'Échec de la compilation de MsiBuilder' }
# Run in-process: some endpoint protections refuse to start freshly compiled executables.
$asm = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($builder))
$main = $asm.GetType('MsiBuilder').GetMethod('Main')
$msi = Join-Path $dist 'Nomi.msi'
$code = $main.Invoke($null, (, [string[]]@($stage, (Join-Path $obj 'app.ico'), $msi, $obj)))
if ($code -ne 0) { throw 'Échec de la génération du MSI' }
Copy-Item $appExe $dist -Force
Write-Host "Terminé : $msi"
