<#
.SYNOPSIS
  Builds SyncGuard's icon assets (app.png + multi-size app.ico) from a source image.
  Near-white pixels become transparent so the tray/taskbar icon looks clean on dark UI.

.EXAMPLE
  .\Build-Icon.ps1 -InputJpg C:\path\logo.jpg -OutDir ..\src\SyncGuard\Assets
#>
param(
  [Parameter(Mandatory = $true)][string]$InputJpg,
  [Parameter(Mandatory = $true)][string]$OutDir,
  [int]$CropX = 8,
  [int]$CropY = 2,
  [int]$CropW = 337,
  [int]$CropH = 214,
  [byte]$WhiteThreshold = 240
)

Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @"
using System;
using System.Drawing;
using System.Drawing.Imaging;
public static class ChromaKey {
  public static Bitmap WhiteToTransparent(Bitmap src, byte threshold) {
    Bitmap bmp = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
    using (Graphics g = Graphics.FromImage(bmp)) { g.DrawImage(src, 0, 0, src.Width, src.Height); }
    Rectangle r = new Rectangle(0, 0, bmp.Width, bmp.Height);
    BitmapData d = bmp.LockBits(r, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
    int len = Math.Abs(d.Stride) * d.Height;
    byte[] px = new byte[len];
    System.Runtime.InteropServices.Marshal.Copy(d.Scan0, px, 0, len);
    for (int i = 0; i < len; i += 4) {
      byte m = px[i];
      if (px[i + 1] < m) m = px[i + 1];
      if (px[i + 2] < m) m = px[i + 2];
      if (m >= threshold) px[i + 3] = 0;
    }
    System.Runtime.InteropServices.Marshal.Copy(px, 0, d.Scan0, len);
    bmp.UnlockBits(d);
    return bmp;
  }
}
"@

New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

$src = [System.Drawing.Image]::FromFile($InputJpg)
try {
  $cropRect = New-Object System.Drawing.Rectangle($CropX, $CropY, $CropW, $CropH)
  $cropped = $src.Clone($cropRect, $src.PixelFormat)
} finally { $src.Dispose() }

$base = [ChromaKey]::WhiteToTransparent($cropped, $WhiteThreshold)
$cropped.Dispose()

function Resize-Bitmap($img, $w, $h) {
  $b = New-Object System.Drawing.Bitmap($w, $h)
  $b.SetResolution(96, 96)
  $g = [System.Drawing.Graphics]::FromImage($b)
  try {
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.DrawImage($img, 0, 0, $w, $h)
  } finally { $g.Dispose() }
  return $b
}

$png256 = Resize-Bitmap $base 256 256
$png256.Save((Join-Path $OutDir "app.png"), [System.Drawing.Imaging.ImageFormat]::Png)

# Multi-size PNG-compressed ICO (Vista+): 16/32/48/256.
$sizes = @(16, 32, 48, 256)
$blobs = @()
foreach ($s in $sizes) {
  $b = Resize-Bitmap $base $s $s
  $ms = New-Object System.IO.MemoryStream
  try { $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $blobs += ,$ms.ToArray() }
  finally { $b.Dispose(); $ms.Dispose() }
}
$base.Dispose(); $png256.Dispose()

$icoPath = Join-Path $OutDir "app.ico"
$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter($fs)
try {
  $bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$blobs.Count)
  $offset = 6 + 16 * $blobs.Count
  for ($i = 0; $i -lt $blobs.Count; $i++) {
    $s = $sizes[$i]
    if ($s -ge 256) { $bw.Write([Byte]0); $bw.Write([Byte]0) }
    else { $bw.Write([Byte]$s); $bw.Write([Byte]$s) }
    $bw.Write([Byte]0); $bw.Write([Byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$blobs[$i].Length); $bw.Write([UInt32]$offset)
    $offset += $blobs[$i].Length
  }
  foreach ($blob in $blobs) { $bw.Write($blob) }
} finally { $bw.Dispose(); $fs.Dispose() }

Write-Output ("Wrote " + (Join-Path $OutDir "app.png") + " and " + $icoPath)
