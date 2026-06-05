# Renders a faithful mockup of the InterMoor overlay badges over a simulated 2x2 video wall,
# mirroring the palette + geometry in OverlayForm.cs. Preview only — the app uses the C# code.
Add-Type -AssemblyName System.Drawing

function Rounded([single]$x,[single]$y,[single]$w,[single]$h,[single]$r){
  $d=[Math]::Min($r*2,[Math]::Min($w,$h)); $p=New-Object System.Drawing.Drawing2D.GraphicsPath
  $p.AddArc($x,$y,$d,$d,180,90); $p.AddArc($x+$w-$d,$y,$d,$d,270,90)
  $p.AddArc($x+$w-$d,$y+$h-$d,$d,$d,0,90); $p.AddArc($x,$y+$h-$d,$d,$d,90,90); $p.CloseFigure(); $p
}
function C([int]$a,[int]$r,[int]$g,[int]$b){ [System.Drawing.Color]::FromArgb($a,$r,$g,$b) }

# Ellipsize text to fit maxW (mirrors OverlayForm.Fit).
function Fit($g,[string]$text,$font,[single]$maxW){
  if($maxW -le 0){ return "" }
  if($g.MeasureString($text,$font).Width -le $maxW){ return $text }
  for($len=$text.Length-1;$len -gt 0;$len--){
    $t=$text.Substring(0,$len).TrimEnd()+[char]0x2026
    if($g.MeasureString($t,$font).Width -le $maxW){ return $t }
  }
  return ""
}

# Palette (matches OverlayForm.cs)
$BrandDeep=C 255 0 70 182; $BrandAccentHi=C 255 40 150 224
$LedHealthy=C 255 38 199 120; $LedWarn=C 255 245 169 35; $LedDown=C 255 229 72 77; $LedConn=C 255 64 156 255
$PillFill=C 196 14 17 24; $PillBorder=C 64 255 255 255
$TextPrimary=C 245 245 245 248; $TextMuted=C 165 176 192

function Draw-Led($g,[single]$x,[single]$y,[single]$d,$color){
  $glow=$d*0.9; $gr=New-Object System.Drawing.RectangleF(($x-$glow/2),($y-$glow/2),($d+$glow),($d+$glow))
  $gp=New-Object System.Drawing.Drawing2D.GraphicsPath; $gp.AddEllipse($gr)
  $pgb=New-Object System.Drawing.Drawing2D.PathGradientBrush($gp)
  $pgb.CenterColor=C 150 $color.R $color.G $color.B; $pgb.SurroundColors=@(C 0 $color.R $color.G $color.B)
  $g.FillEllipse($pgb,$gr); $pgb.Dispose(); $gp.Dispose()
  $cb=New-Object System.Drawing.SolidBrush($color); $g.FillEllipse($cb,$x,$y,$d,$d); $cb.Dispose()
  $rp=New-Object System.Drawing.Pen((C 70 0 0 0),1); $g.DrawEllipse($rp,$x,$y,$d,$d); $rp.Dispose()
  $hb=New-Object System.Drawing.SolidBrush((C 150 255 255 255)); $g.FillEllipse($hb,($x+$d*0.26),($y+$d*0.20),($d*0.30),($d*0.30)); $hb.Dispose()
}

function Draw-Ambient($g,[single]$ox,[single]$oy,[single]$scale,[int]$cell,$led){
  $ledD=13*$scale; Draw-Led $g $ox $oy $ledD $led
  $f=New-Object System.Drawing.Font("Segoe UI Semibold",(9.5*$scale),[System.Drawing.FontStyle]::Bold)
  $b=New-Object System.Drawing.SolidBrush((C 150 245 245 248))
  $ns=$g.MeasureString("$cell",$f); $g.DrawString("$cell",$f,$b,($ox+$ledD+7*$scale),($oy+($ledD-$ns.Height)/2))
  $f.Dispose(); $b.Dispose()
}

function Draw-Pill($g,[single]$ox,[single]$oy,[single]$scale,[single]$maxWidth,[int]$cell,$led,[string]$name,[string]$word,[string]$metric,[bool]$showStatus){
  $pad=9*$scale; $gap=9*$scale; $pillH=36*$scale; $radius=9*$scale; $badge=$pillH-2*(6*$scale); $ledD=11*$scale
  $numFont=New-Object System.Drawing.Font("Segoe UI Semibold",(12*$scale),[System.Drawing.FontStyle]::Bold)
  $nameFont=New-Object System.Drawing.Font("Segoe UI Semibold",(11.5*$scale),[System.Drawing.FontStyle]::Bold)
  $statusFont=New-Object System.Drawing.Font("Segoe UI Semibold",(9.5*$scale),[System.Drawing.FontStyle]::Bold)
  $metricFont=New-Object System.Drawing.Font("Segoe UI",(9.5*$scale))
  $hasName=[bool]$name
  $wordSize=if($showStatus){$g.MeasureString($word,$statusFont)}else{New-Object System.Drawing.SizeF(0,0)}
  $metricSize=if($showStatus -and $metric){$g.MeasureString($metric,$metricFont)}else{New-Object System.Drawing.SizeF(0,0)}
  $metricGap=if($metricSize.Width -gt 0){8*$scale}else{0}
  $statusW=if($showStatus){$wordSize.Width+$metricGap+$metricSize.Width}else{0}
  $sepGap=if($hasName -and $showStatus){10*$scale}else{0}
  if($hasName){
    $nameMax=$maxWidth-($pad+$badge+$gap+$ledD+$gap+$sepGap+$statusW+$pad)
    $name=Fit $g $name $nameFont $nameMax
    $hasName=[bool]$name
    $sepGap=if($hasName -and $showStatus){10*$scale}else{0}
  }
  $nameSize=if($hasName){$g.MeasureString($name,$nameFont)}else{New-Object System.Drawing.SizeF(0,0)}
  $nameW=if($hasName){$nameSize.Width}else{0}
  $contentW=$badge+$gap+$ledD+$gap+$nameW+$sepGap+$statusW
  $pillW=$pad+$contentW+$pad

  # shadow
  for($i=4;$i -ge 1;$i--){
    $r=Rounded ($ox-$i*$scale*0.6) ($oy-$i*$scale*0.6+2*$scale) ($pillW+2*$i*$scale*0.6) ($pillH+2*$i*$scale*0.6) ($radius+$i*$scale*0.6)
    $sb=New-Object System.Drawing.SolidBrush((C 16 0 0 0)); $g.FillPath($sb,$r); $sb.Dispose(); $r.Dispose()
  }
  # body
  $path=Rounded $ox $oy $pillW $pillH $radius
  $fb=New-Object System.Drawing.SolidBrush($PillFill); $g.FillPath($fb,$path); $fb.Dispose()
  $sheen=New-Object System.Drawing.RectangleF($ox,$oy,$pillW,($pillH*0.5))
  $gb=New-Object System.Drawing.Drawing2D.LinearGradientBrush($sheen,(C 36 255 255 255),(C 0 255 255 255),[System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
  $clip=$g.Clip; $g.SetClip($path,[System.Drawing.Drawing2D.CombineMode]::Replace); $g.FillRectangle($gb,$sheen); $g.Clip=$clip; $gb.Dispose()
  $bp=New-Object System.Drawing.Pen($PillBorder,1); $g.DrawPath($bp,$path); $bp.Dispose(); $path.Dispose()

  $cx=$ox+$pad; $midY=$oy+$pillH/2
  # number badge
  $br=New-Object System.Drawing.RectangleF($cx,($midY-$badge/2),$badge,$badge)
  $bpath=Rounded $br.X $br.Y $br.Width $br.Height (5*$scale)
  $bg=New-Object System.Drawing.Drawing2D.LinearGradientBrush($br,$BrandAccentHi,$BrandDeep,[System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
  $g.FillPath($bg,$bpath); $bg.Dispose()
  $bb=New-Object System.Drawing.Pen((C 90 255 255 255),1); $g.DrawPath($bb,$bpath); $bb.Dispose(); $bpath.Dispose()
  $nb=New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
  $ns=$g.MeasureString("$cell",$numFont)
  $g.DrawString("$cell",$numFont,$nb,($br.X+($badge-$ns.Width)/2),($br.Y+($badge-$ns.Height)/2)); $nb.Dispose()
  $cx+=$badge+$gap
  # led
  Draw-Led $g $cx ($midY-$ledD/2) $ledD $led; $cx+=$ledD+$gap
  # vessel name (primary)
  if($hasName){
    $nameb=New-Object System.Drawing.SolidBrush($TextPrimary); $g.DrawString($name,$nameFont,$nameb,$cx,($midY-$nameSize.Height/2)); $nameb.Dispose()
    $cx+=$nameSize.Width+$sepGap
  }
  # word + metric (non-healthy only)
  if($showStatus){
    $wb=New-Object System.Drawing.SolidBrush($led); $g.DrawString($word,$statusFont,$wb,$cx,($midY-$wordSize.Height/2)); $wb.Dispose()
    $cx+=$wordSize.Width+$metricGap
    if($metricSize.Width -gt 0){ $mb=New-Object System.Drawing.SolidBrush($TextMuted); $g.DrawString($metric,$metricFont,$mb,$cx,($midY-$metricSize.Height/2)); $mb.Dispose() }
  }
  $numFont.Dispose();$nameFont.Dispose();$statusFont.Dispose();$metricFont.Dispose()
}

# ---- canvas: simulated 2x2 video wall ----
$W=1100; $H=680; $bmp=New-Object System.Drawing.Bitmap($W,$H)
$g=[System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode='AntiAlias'; $g.TextRenderingHint='AntiAlias'; $g.InterpolationMode='HighQualityBicubic'
# fake video quadrants (muted gradients so the frosted pills are visible over real imagery)
$qx=@(0,($W/2),0,($W/2)); $qy=@(0,0,($H/2),($H/2))
$tr=@(40,58,36,60); $tg=@(52,46,54,40); $tb=@(66,40,48,52)
for($i=0;$i -lt 4;$i++){
  $rect=New-Object System.Drawing.RectangleF($qx[$i],$qy[$i],($W/2),($H/2))
  $c1=C 255 $tr[$i] $tg[$i] $tb[$i]
  $c2=C 255 ([Math]::Max(0,$tr[$i]-22)) ([Math]::Max(0,$tg[$i]-22)) ([Math]::Max(0,$tb[$i]-22))
  $lg=New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect,$c1,$c2,45.0); $g.FillRectangle($lg,$rect); $lg.Dispose()
}
$gridPen=New-Object System.Drawing.Pen((C 255 8 10 14),2)
$g.DrawLine($gridPen,($W/2),0,($W/2),$H); $g.DrawLine($gridPen,0,($H/2),$W,$H/2); $gridPen.Dispose()

$scale=($H/2)/540.0
$ins=16*$scale   # quadrant-corner inset
$maxW=($W/2)-2*$ins
# 1: healthy + named -> calm nameplate, name only.   2: connecting + named.
# 3: warn + named -> name + LIVE + metric.            4: down + unnamed -> status-only fallback.
Draw-Pill $g ($qx[0]+$ins) ($qy[0]+$ins) $scale $maxW 1 $LedHealthy "Skandi Skansen" "LIVE" "" $false
Draw-Pill $g ($qx[1]+$ins) ($qy[1]+$ins) $scale $maxW 2 $LedConn   "Skandi Iceman"  "CONNECTING" "" $true
Draw-Pill $g ($qx[2]+$ins) ($qy[2]+$ins) $scale $maxW 3 $LedWarn   "Skandi Hercules" "LIVE" "3.4 Mb/s  ·  lost 12" $true
Draw-Pill $g ($qx[3]+$ins) ($qy[3]+$ins) $scale $maxW 4 $LedDown   "" "RECONNECTING" "" $true

$out="C:\Users\gregor\Downloads\Dev\Vlc\tools\overlay-preview.png"
$bmp.Save($out,[System.Drawing.Imaging.ImageFormat]::Png); $g.Dispose(); $bmp.Dispose()
Write-Host "wrote $out"
