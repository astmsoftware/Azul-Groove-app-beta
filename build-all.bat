@echo off
REM Gera tudo de uma vez na pasta "dist":
REM   AzulGroove-Setup.exe     -> instalador (assistente, atalhos, desinstalar)
REM   AzulGroove-Portable.exe  -> versao .exe unica, sem instalar (abre com dois cliques)
REM Precisa de: .NET 8 SDK  +  Inno Setup 6.6 ou mais novo (https://jrsoftware.org/isdl.php)
REM (O pacote MSIX da Store e gerado a parte, com build-msix.bat / build-msix.ps1)

dotnet publish -c Release -o publish
if errorlevel 1 goto erro

set ISCC=
if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe
if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe
if exist "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" set ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe
if exist "%ProgramFiles(x86)%\Inno Setup 7\ISCC.exe" set ISCC=%ProgramFiles(x86)%\Inno Setup 7\ISCC.exe
if exist "%ProgramFiles%\Inno Setup 7\ISCC.exe" set ISCC=%ProgramFiles%\Inno Setup 7\ISCC.exe
if exist "%LOCALAPPDATA%\Programs\Inno Setup 7\ISCC.exe" set ISCC=%LOCALAPPDATA%\Programs\Inno Setup 7\ISCC.exe
if "%ISCC%"=="" for /f "delims=" %%I in ('where ISCC.exe 2^>nul') do set ISCC=%%I

if not exist dist mkdir dist
copy /Y publish\AzulGroove.exe dist\AzulGroove-Portable.exe >nul

if "%ISCC%"=="" (
  echo.
  echo Inno Setup nao encontrado: so a versao portatil foi gerada.
  echo Para gerar o instalador, instale em https://jrsoftware.org/isdl.php e rode de novo.
  echo Portatil: dist\AzulGroove-Portable.exe
  pause
  exit /b 0
)

"%ISCC%" AzulGroove.iss
if errorlevel 1 goto erro
copy /Y installer\AzulGroove-Setup.exe dist\AzulGroove-Setup.exe >nul

echo.
echo Pronto! Arquivos em "dist":
echo   dist\AzulGroove-Setup.exe     (instalador)
echo   dist\AzulGroove-Portable.exe  (sem instalar)
pause
exit /b 0

:erro
echo.
echo Algo deu errado. Veja a mensagem acima.
pause
exit /b 1
