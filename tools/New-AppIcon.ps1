$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$taskIconPath = Join-Path $PSScriptRoot '..\src\AudioRouter.App\Assets\AudioRouter.ico'
$taskSizes = @(16, 32, 48, 64, 128, 256)
$taskImages = @()
foreach ($taskSize in $taskSizes) {
    $taskBitmap = New-Object System.Drawing.Bitmap($taskSize, $taskSize)
    $taskGraphics = [System.Drawing.Graphics]::FromImage($taskBitmap)
    $taskGraphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $taskGraphics.ScaleTransform(($taskSize / 256.0), ($taskSize / 256.0))
    $taskBackground = New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml('#54E1BD'))
    $taskShape = New-Object System.Drawing.Drawing2D.GraphicsPath
    $taskShape.AddArc(8, 8, 128, 128, 180, 90)
    $taskShape.AddArc(120, 8, 128, 128, 270, 90)
    $taskShape.AddArc(120, 120, 128, 128, 0, 90)
    $taskShape.AddArc(8, 120, 128, 128, 90, 90)
    $taskShape.CloseFigure()
    $taskGraphics.FillPath($taskBackground, $taskShape)
    $taskPen = New-Object System.Drawing.Pen([System.Drawing.ColorTranslator]::FromHtml('#10251E'), 12)
    $taskPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $taskPen.StartCap = $taskPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $taskPoints = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF(48,128)), (New-Object System.Drawing.PointF(72,128)),
        (New-Object System.Drawing.PointF(72,88)), (New-Object System.Drawing.PointF(96,88)),
        (New-Object System.Drawing.PointF(96,168)), (New-Object System.Drawing.PointF(120,168)),
        (New-Object System.Drawing.PointF(120,56)), (New-Object System.Drawing.PointF(144,56)),
        (New-Object System.Drawing.PointF(144,200)), (New-Object System.Drawing.PointF(168,200)),
        (New-Object System.Drawing.PointF(168,104)), (New-Object System.Drawing.PointF(192,104)),
        (New-Object System.Drawing.PointF(192,128)), (New-Object System.Drawing.PointF(208,128)))
    $taskGraphics.DrawLines($taskPen, $taskPoints)
    $taskMemory = New-Object System.IO.MemoryStream
    $taskBitmap.Save($taskMemory, [System.Drawing.Imaging.ImageFormat]::Png)
    $taskImages += ,$taskMemory.ToArray()
    $taskMemory.Dispose(); $taskPen.Dispose(); $taskShape.Dispose(); $taskBackground.Dispose(); $taskGraphics.Dispose(); $taskBitmap.Dispose()
}
$taskStream = [System.IO.File]::Create($taskIconPath)
$taskWriter = New-Object System.IO.BinaryWriter($taskStream)
try {
    $taskWriter.Write([uint16]0); $taskWriter.Write([uint16]1); $taskWriter.Write([uint16]$taskSizes.Count)
    $taskOffset = 6 + 16 * $taskSizes.Count
    for ($taskIndex = 0; $taskIndex -lt $taskSizes.Count; $taskIndex++) {
        $taskByteSize = $taskSizes[$taskIndex] % 256
        $taskWriter.Write([byte]$taskByteSize); $taskWriter.Write([byte]$taskByteSize)
        $taskWriter.Write([uint16]0); $taskWriter.Write([uint16]1); $taskWriter.Write([uint16]32)
        $taskWriter.Write([uint32]$taskImages[$taskIndex].Length); $taskWriter.Write([uint32]$taskOffset)
        $taskOffset += $taskImages[$taskIndex].Length
    }
    foreach ($taskImage in $taskImages) { $taskWriter.Write([byte[]]$taskImage) }
} finally { $taskWriter.Dispose(); $taskStream.Dispose() }
