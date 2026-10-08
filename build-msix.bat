@echo off
REM Gera o MSIX para TESTE no seu PC (assinado com certificado de teste).
REM Para a Store, rode o build-msix.ps1 com os dados do Partner Center (veja o LEIA-ME).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-msix.ps1" -Sign
pause
