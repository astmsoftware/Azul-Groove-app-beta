@echo off
REM Gera o .exe e depois o instalador (installer\AzulGroove-Setup.exe)
REM Precisa de: .NET 8 SDK  +  Inno Setup 6.6 ou mais novo (https://jrsoftware.org/isdl.php)

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
if "%ISCC%"=="" (
  echo.
  echo Inno Setup nao encontrado. Instale em https://jrsoftware.org/isdl.php e rode de novo.
  goto erro
)

"%ISCC%" AzulGroove.iss
if errorlevel 1 goto erro

echo.
echo Pronto! Instalador: installer\AzulGroove-Setup.exe
pause
exit /b 0

:erro
echo.
echo Algo deu errado. Veja a mensagem acima.
pause
exit /b 1
