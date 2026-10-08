; Instalador estilo Chrome: uma janela escura, sem etapas.
;  - equalizador animado, nome do app, texto "Instalando..." e barra de progresso real
;  - instala sozinho e abre o app no final
; Incluído por AzulGroove.iss.
; Se der erro de compilação aqui, apague a linha  #include "installer-animation.iss"
; no fim do AzulGroove.iss: volta o instalador normal (sem animação).

[Code]
function SetTimer(hWnd, nIDEvent, uElapse, lpTimerFunc: LongWord): LongWord;
  external 'SetTimer@user32.dll stdcall';
function KillTimer(hWnd, uIDEvent: LongWord): Boolean;
  external 'KillTimer@user32.dll stdcall';

var
  AnimTimer: LongWord;
  AnimTick: Integer;
  Canvas1: TBitmapImage;

procedure DrawScreen;
var
  W, H, i, y, bw, gap, total, x0, left, v, bh, cy, maxH: Integer;
  pct, mx, barW, barH, barX, barY, fillW: Integer;
  C: TCanvas;
  t: String;
begin
  if Canvas1 = nil then Exit;
  W := Canvas1.Bitmap.Width;
  H := Canvas1.Bitmap.Height;
  C := Canvas1.Bitmap.Canvas;

  { fundo escuro em degradê vertical (cores em BGR) }
  C.Pen.Style := psClear;
  for y := 0 to H - 1 do
  begin
    C.Brush.Color := ((($17 + y * 14 div H) shl 16) or (($11 + y * 6 div H) shl 8) or ($0F + y * 4 div H));
    C.Rectangle(0, y, W, y + 2);
  end;
  C.Pen.Style := psSolid;

  { equalizador grande, no centro }
  bw := W * 5 div 100;
  gap := bw * 6 div 10;
  total := 7 * bw + 6 * gap;
  x0 := (W - total) div 2;
  cy := H * 36 div 100;
  maxH := H * 26 div 100;
  for i := 0 to 6 do
  begin
    v := (AnimTick * 2 + i * 7) mod 40;
    if v > 20 then v := 40 - v;
    bh := maxH * (20 + v * 4) div 100;
    left := x0 + i * (bw + gap);
    if (i mod 2) = 0 then C.Brush.Color := $F2A27A else C.Brush.Color := $F26558;
    C.Pen.Color := C.Brush.Color;
    C.RoundRect(left, cy - bh div 2, left + bw, cy + bh div 2, bw, bw);
  end;

  { progresso real }
  pct := 0;
  mx := WizardForm.ProgressGauge.Max - WizardForm.ProgressGauge.Min;
  if mx > 0 then
    pct := (WizardForm.ProgressGauge.Position - WizardForm.ProgressGauge.Min) * 100 div mx;
  if pct > 100 then pct := 100;

  { nome do app }
  C.Brush.Style := bsClear;
  C.Font.Name := 'Segoe UI Semibold';
  C.Font.Size := 20;
  C.Font.Color := $FFFFFF;
  t := 'Azul Groove';
  C.TextOut((W - C.TextWidth(t)) div 2, H * 58 div 100, t);

  { status }
  C.Font.Name := 'Segoe UI';
  C.Font.Size := 10;
  C.Font.Color := $D8C8C0;
  t := 'Instalando' + Copy('...', 1, (AnimTick div 4) mod 4) + '  ' + IntToStr(pct) + '%';
  C.TextOut((W - C.TextWidth(t)) div 2, H * 72 div 100, t);

  { barra fina de progresso }
  barW := W * 56 div 100;
  barH := H * 15 div 1000;
  if barH < 4 then barH := 4;
  barX := (W - barW) div 2;
  barY := H * 84 div 100;
  C.Brush.Style := bsSolid;
  C.Brush.Color := $4A3A30;
  C.Pen.Color := $4A3A30;
  C.RoundRect(barX, barY, barX + barW, barY + barH, barH, barH);
  fillW := barW * pct div 100;
  if (pct > 0) and (fillW < barH) then fillW := barH;
  if fillW > 0 then
  begin
    C.Brush.Color := $F2A27A;
    C.Pen.Color := $F2A27A;
    C.RoundRect(barX, barY, barX + fillW, barY + barH, barH, barH);
  end;

  Canvas1.Invalidate;
end;

procedure AnimProc(H, Msg, Id, Time: LongWord);
begin
  AnimTick := AnimTick + 1;
  try
    DrawScreen;
  except
    { nunca deixa a animação derrubar o instalador }
  end;
end;

procedure InitializeWizard;
begin
  try
    { janela compacta }
    WizardForm.ClientWidth := ScaleX(520);
    WizardForm.ClientHeight := ScaleY(340);

    { uma tela só, por cima de tudo }
    Canvas1 := TBitmapImage.Create(WizardForm);
    Canvas1.Parent := WizardForm;
    Canvas1.Left := 0;
    Canvas1.Top := 0;
    Canvas1.Width := WizardForm.ClientWidth;
    Canvas1.Height := WizardForm.ClientHeight;
    Canvas1.Bitmap.Width := Canvas1.Width;
    Canvas1.Bitmap.Height := Canvas1.Height;
    Canvas1.BringToFront;
    DrawScreen;
  except
    Canvas1 := nil;
  end;
  AnimTimer := SetTimer(0, 0, 60, CreateCallback(@AnimProc));
end;

procedure DeinitializeSetup;
begin
  if AnimTimer <> 0 then
    KillTimer(0, AnimTimer);
  AnimTimer := 0;
end;
