<#
  Gera o pacote MSIX do Azul Groove.

  Para a MICROSOFT STORE (use os 3 valores do Partner Center -> Gerenciamento de produto -> Identidade do produto):
    .\build-msix.ps1 -IdentityName "12345ASTMSoftware.AzulGroove" -Publisher "CN=XXXXXXXX-XXXX-..." -PublisherDisplayName "Seu Nome"

  Para TESTAR no seu PC (cria um certificado de teste e assina):
    .\build-msix.ps1 -Sign

  Precisa de: .NET 8 SDK + Windows SDK (traz o makeappx.exe e o signtool.exe)
#>
param(
  [string]$IdentityName = "ASTMSoftware.AzulGroove",
  [string]$Publisher = "CN=ASTM Software",
  [string]$PublisherDisplayName = "ASTM Software",
  [string]$Version = "1.0.0.0",
  [switch]$Sign
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw "Version precisa ter 4 numeros, ex.: 1.0.0.0" }

$out    = Join-Path $PSScriptRoot "msix\out"
$layout = Join-Path $out "layout"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Path $layout | Out-Null

Write-Host "1/4 Compilando o app..." -ForegroundColor Cyan
# STORE = versao sem o auto-instalador do WebView2 (a Store nao gosta de app que baixa e roda .exe)
dotnet publish -c Release -o $layout -p:DefineConstants=STORE -p:PublishSingleFile=false -p:IncludeNativeLibrariesForSelfExtract=false -p:EnableCompressionInSingleFile=false
if ($LASTEXITCODE -ne 0) { throw "dotnet publish falhou" }

Write-Host "2/4 Montando manifesto e imagens..." -ForegroundColor Cyan
Copy-Item (Join-Path $PSScriptRoot "msix\Assets") (Join-Path $layout "Assets") -Recurse
$manifest = Get-Content (Join-Path $PSScriptRoot "msix\AppxManifest.template.xml") -Raw -Encoding UTF8
$manifest = $manifest.Replace("{{IDENTITY_NAME}}", $IdentityName).
                      Replace("{{PUBLISHER}}", $Publisher).
                      Replace("{{PUBLISHER_DISPLAY_NAME}}", $PublisherDisplayName).
                      Replace("{{VERSION}}", $Version)
[IO.File]::WriteAllText((Join-Path $layout "AppxManifest.xml"), $manifest, (New-Object System.Text.UTF8Encoding($false)))

Write-Host "3/4 Empacotando (makeappx)..." -ForegroundColor Cyan
$kit = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\makeappx.exe" -ErrorAction SilentlyContinue |
       Sort-Object FullName -Descending | Select-Object -First 1
if (-not $kit) { throw "makeappx.exe nao encontrado. Instale o Windows SDK: https://developer.microsoft.com/windows/downloads/windows-sdk/" }
$makeappx = $kit.FullName
$signtool = Join-Path $kit.DirectoryName "signtool.exe"
$msix = Join-Path $out ("AzulGroove_{0}_x64.msix" -f $Version)
& $makeappx pack /d $layout /p $msix /o
if ($LASTEXITCODE -ne 0) { throw "makeappx falhou" }

if ($Sign) {
  Write-Host "4/4 Assinando com certificado de TESTE..." -ForegroundColor Cyan
  $senha = "azulgroove-teste"
  $cert = New-SelfSignedCertificate -Type Custom -Subject $Publisher -KeyUsage DigitalSignature `
            -FriendlyName "Azul Groove (teste)" -CertStoreLocation "Cert:\CurrentUser\My" `
            -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
  $sec = ConvertTo-SecureString $senha -AsPlainText -Force
  $pfx = Join-Path $out "AzulGroove-teste.pfx"
  $cer = Join-Path $out "AzulGroove-teste.cer"
  Export-PfxCertificate -Cert $cert -FilePath $pfx -Password $sec | Out-Null
  Export-Certificate    -Cert $cert -FilePath $cer | Out-Null
  & $signtool sign /fd SHA256 /f $pfx /p $senha $msix
  if ($LASTEXITCODE -ne 0) { throw "signtool falhou" }
  Write-Host ""
  Write-Host "Para instalar o teste (PowerShell como ADMINISTRADOR):" -ForegroundColor Yellow
  Write-Host "  Import-Certificate -FilePath `"$cer`" -CertStoreLocation Cert:\LocalMachine\TrustedPeople"
  Write-Host "  Add-AppxPackage `"$msix`""
} else {
  Write-Host "4/4 Sem assinatura (ok para enviar a Store: ela assina o pacote)." -ForegroundColor Cyan
}

Write-Host ""
Write-Host "Pronto! Pacote: $msix" -ForegroundColor Green
