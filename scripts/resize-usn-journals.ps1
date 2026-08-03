# Enlarges the NTFS USN change journal on the indexed volumes so an index checkpoint does not age
# out of the (previously tiny) journal window between refresh passes. Resizing preserves the journal
# ID and existing records (it is not a delete/recreate). Requires elevation.
#
# Defaults: 512 MB max size, 64 MB allocation delta. Override via -Volumes / -MaxBytes / -DeltaBytes.
param(
    [string[]] $Volumes = @('D:', 'C:'),
    [long]     $MaxBytes = 536870912,  # 512 MB
    [long]     $DeltaBytes = 67108864, # 64 MB
    [string]   $OutFile = "$env:TEMP\yagu-usn-resize.txt"
)

"USN journal resize - $(Get-Date -Format o)" | Out-File -FilePath $OutFile -Encoding utf8
foreach ($v in $Volumes) {
    "=== $v : before ===" | Out-File -FilePath $OutFile -Append -Encoding utf8
    (fsutil usn queryjournal $v 2>&1 | Out-String) | Out-File -FilePath $OutFile -Append -Encoding utf8
    "=== $v : createjournal m=$MaxBytes a=$DeltaBytes ===" | Out-File -FilePath $OutFile -Append -Encoding utf8
    (fsutil usn createjournal "m=$MaxBytes" "a=$DeltaBytes" $v 2>&1 | Out-String) | Out-File -FilePath $OutFile -Append -Encoding utf8
    "=== $v : after ===" | Out-File -FilePath $OutFile -Append -Encoding utf8
    (fsutil usn queryjournal $v 2>&1 | Out-String) | Out-File -FilePath $OutFile -Append -Encoding utf8
}
"DONE" | Out-File -FilePath $OutFile -Append -Encoding utf8
