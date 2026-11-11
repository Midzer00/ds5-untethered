# SPDX-License-Identifier: GPL-3.0-or-later
# Regenerates the installer's wizard images (tools\installer\wizard\*.bmp) from the repository's logo:
#   WizardImage-WxH.bmp  the tall image on the Welcome / Finished pages: the full logo (docs\brand\logo.png) on the app's
#                        flat graphite backdrop (#16181C), opaque 24-bit; a dark side panel reads as intended on both the
#                        light and the dark wizard
#   WizardSmall-WxH.bmp  the top-right image on the other pages: the controller mark (docs\brand\icon-1024.png) on a
#                        transparent background, 32-bit with alpha (WizardImageAlphaFormat=defined), so it sits on the
#                        light or dark header without a box around it
# One file per size Inno Setup 6 documents for 100 % .. 250 % DPI; DS5Untethered.iss lists them all (Setup picks the
# best fit). Run with Windows PowerShell 5.1 or PowerShell 7 on Windows:  powershell -File tools\installer\make-wizard-images.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$out = Join-Path $PSScriptRoot 'wizard'
New-Item -ItemType Directory -Force $out | Out-Null

$large = @(@(164, 314), @(192, 386), @(246, 459), @(273, 556), @(328, 604), @(355, 700), @(410, 797))
$small = @(@(55, 55), @(64, 68), @(83, 80), @(92, 97), @(110, 106), @(119, 123), @(138, 140))
$backdrop = [System.Drawing.Color]::FromArgb(255, 0x16, 0x18, 0x1C)

# Writes a bottom-up BI_RGB bitmap: 24-bit (opaque) or 32-bit BGRA with straight (not premultiplied) alpha.
function Save-Bmp([System.Drawing.Bitmap] $bitmap, [string] $path, [bool] $alpha) {
	$w = $bitmap.Width; $h = $bitmap.Height
	$bytesPerPixel = if ($alpha) { 4 } else { 3 }
	$stride = [int]([Math]::Ceiling($w * $bytesPerPixel / 4.0) * 4)
	$imageSize = $stride * $h
	$stream = New-Object System.IO.MemoryStream
	$writer = New-Object System.IO.BinaryWriter($stream)
	# BITMAPFILEHEADER
	$writer.Write([byte]0x42); $writer.Write([byte]0x4D)
	$writer.Write([int](14 + 40 + $imageSize)); $writer.Write([int]0); $writer.Write([int](14 + 40))
	# BITMAPINFOHEADER (72 dpi)
	$writer.Write([int]40); $writer.Write([int]$w); $writer.Write([int]$h); $writer.Write([int16]1); $writer.Write([int16]($bytesPerPixel * 8))
	$writer.Write([int]0); $writer.Write([int]$imageSize); $writer.Write([int]2835); $writer.Write([int]2835); $writer.Write([int]0); $writer.Write([int]0)
	$row = New-Object byte[] $stride
	for ($y = $h - 1; $y -ge 0; $y--) {
		[Array]::Clear($row, 0, $stride)
		for ($x = 0; $x -lt $w; $x++) {
			$c = $bitmap.GetPixel($x, $y)
			$i = $x * $bytesPerPixel
			$row[$i] = $c.B; $row[$i + 1] = $c.G; $row[$i + 2] = $c.R
			if ($alpha) { $row[$i + 3] = $c.A }
		}
		$writer.Write($row)
	}
	$writer.Flush()
	[System.IO.File]::WriteAllBytes($path, $stream.ToArray())
	$writer.Dispose()
}

# Draws `source` scaled to fit `box` (aspect kept), centred on (cx, cy).
function Draw-Fit([System.Drawing.Graphics] $g, [System.Drawing.Image] $source, [double] $boxW, [double] $boxH, [double] $cx, [double] $cy) {
	$scale = [Math]::Min($boxW / $source.Width, $boxH / $source.Height)
	$dw = $source.Width * $scale; $dh = $source.Height * $scale
	$g.DrawImage($source, [System.Drawing.RectangleF]::new([single]($cx - $dw / 2), [single]($cy - $dh / 2), [single]$dw, [single]$dh))
}

# The image cut to its visible (alpha > 8) bounds, so the mark fills the small image instead of its padding.
function Crop-ToContent([System.Drawing.Bitmap] $source) {
	$rect = [System.Drawing.Rectangle]::new(0, 0, $source.Width, $source.Height)
	$data = $source.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
	$bytes = New-Object byte[] ($data.Stride * $source.Height)
	[System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
	$stride = $data.Stride
	$source.UnlockBits($data)
	$minX = $source.Width; $minY = $source.Height; $maxX = -1; $maxY = -1
	for ($y = 0; $y -lt $source.Height; $y++) {
		for ($x = 0; $x -lt $source.Width; $x++) {
			if ($bytes[$y * $stride + $x * 4 + 3] -gt 8) {
				if ($x -lt $minX) { $minX = $x }; if ($x -gt $maxX) { $maxX = $x }
				if ($y -lt $minY) { $minY = $y }; if ($y -gt $maxY) { $maxY = $y }
			}
		}
	}
	return $source.Clone([System.Drawing.Rectangle]::new($minX, $minY, $maxX - $minX + 1, $maxY - $minY + 1), $source.PixelFormat)
}

function New-Canvas([int] $w, [int] $h) {
	$bitmap = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
	$g = [System.Drawing.Graphics]::FromImage($bitmap)
	$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
	$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
	$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
	$g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
	return @($bitmap, $g)
}

$logoFile = [System.Drawing.Bitmap]::FromFile((Join-Path $repo 'docs\brand\logo.png'))
$markFile = [System.Drawing.Bitmap]::FromFile((Join-Path $repo 'docs\brand\icon-1024.png'))
$logo = Crop-ToContent $logoFile
$mark = Crop-ToContent $markFile
$logoFile.Dispose()
$markFile.Dispose()
try {
	foreach ($size in $large) {
		$w = $size[0]; $h = $size[1]
		$bitmap, $g = New-Canvas $w $h
		$g.Clear($backdrop)
		# The logo across ~80 % of the width, a little above the middle.
		Draw-Fit $g $logo ($w * 0.8) ($h * 0.5) ($w / 2) ($h * 0.42)
		$g.Dispose()
		Save-Bmp $bitmap (Join-Path $out "WizardImage-${w}x${h}.bmp") $false
		$bitmap.Dispose()
	}
	foreach ($size in $small) {
		$w = $size[0]; $h = $size[1]
		$bitmap, $g = New-Canvas $w $h
		$g.Clear([System.Drawing.Color]::Transparent)
		Draw-Fit $g $mark ($w * 0.96) ($h * 0.96) ($w / 2) ($h / 2)
		$g.Dispose()
		Save-Bmp $bitmap (Join-Path $out "WizardSmall-${w}x${h}.bmp") $true
		$bitmap.Dispose()
	}
}
finally {
	$logo.Dispose()
	$mark.Dispose()
}
Write-Host "Wizard images written to $out"
