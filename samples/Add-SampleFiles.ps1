<#
.SYNOPSIS
  Puts the AladdinRug sample files on your Desktop (so you can watch the merchant sort them), or takes them off again.

.DESCRIPTION
  Copies the 40 small, harmless files from samples\desktop onto your Desktop. It never overwrites a file that is already
  there. With -Remove it deletes only the files on your Desktop that are byte-for-byte identical to a sample, so anything
  of yours with the same name is left alone. If the merchant has sorted the samples into folders, undo that first
  (right-click the rug: "Undo: put the sorted files back"), then run -Remove.

.EXAMPLE
  .\Add-SampleFiles.ps1            # add the samples to the Desktop
  .\Add-SampleFiles.ps1 -Remove    # take them off again
  .\Add-SampleFiles.ps1 -Desktop D:\SomeFolder   # use another folder instead of the Desktop
#>
param([switch]$Remove, [string]$Desktop = [Environment]::GetFolderPath('DesktopDirectory'))

$source  = Join-Path $PSScriptRoot 'desktop'
$desktop = $Desktop

if (-not (Test-Path $source)) { throw "Can't find the sample files at $source" }

$added = 0; $skipped = 0; $removed = 0; $kept = 0
foreach ($file in Get-ChildItem $source -File) {
    $target = Join-Path $desktop $file.Name
    if ($Remove) {
        if ((Test-Path $target) -and ((Get-FileHash $target).Hash -eq (Get-FileHash $file.FullName).Hash)) {
            Remove-Item $target -Confirm:$false; $removed++
        } elseif (Test-Path $target) { $kept++ }
    } else {
        if (Test-Path $target) { $skipped++ }
        else { Copy-Item $file.FullName $target; $added++ }
    }
}

if ($Remove) { "Removed $removed sample files from $desktop ($kept left alone because they differ from the samples)." }
else         { "Added $added sample files to $desktop ($skipped already there)." }
