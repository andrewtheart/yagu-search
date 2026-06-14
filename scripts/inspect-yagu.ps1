# Quick read-only UIA inspection of the running Yagu window.
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]
$PC = [System.Windows.Automation.PropertyCondition]

$root = $AE::RootElement
$procs = Get-Process -Name Yagu -ErrorAction SilentlyContinue
Write-Host "Yagu processes: $(( $procs | ForEach-Object { $_.Id }) -join ', ')"

$win = $null
$windows = $root.FindAll($TS::Children, $PC::new($AE::ControlTypeProperty, $CT::Window))
foreach ($w in $windows) {
    try { if ($w.Current.Name -like "Yagu*") { $win = $w; break } } catch {}
}
if (-not $win) { Write-Host "No Yagu window found."; exit 1 }
Write-Host "Window: '$($win.Current.Name)' pid=$($win.Current.ProcessId) rect=$($win.Current.BoundingRectangle)"

function Probe([string]$id) {
    try {
        $el = $win.FindFirst($TS::Descendants, $PC::new($AE::AutomationIdProperty, $id))
        if (-not $el) { Write-Host ("  {0,-26} : MISSING" -f $id); return }
        $r = $el.Current.BoundingRectangle
        $off = $el.Current.IsOffscreen
        $val = ""
        try { $vp = $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern); $val = " value='$($vp.Current.Value)'" } catch {}
        Write-Host ("  {0,-26} : ctl={1} off={2} rect=({3:N0},{4:N0},{5:N0},{6:N0}) name='{7}'{8}" -f `
            $id, $el.Current.ControlType.ProgrammaticName.Replace('ControlType.',''), $off, $r.X, $r.Y, $r.Width, $r.Height, $el.Current.Name, $val)
    } catch { Write-Host ("  {0,-26} : ERR $_" -f $id) }
}

Write-Host "Key controls:"
foreach ($id in @('DirectoryBox','QueryBox','SearchCancelButton','SearchCancelLabel','ResultsList','PreviewPanelBorder','MatchNavLabel','PreviewFileLabel','SelectAllFilesCheckBox','AdvancedOptionsExpander')) {
    Probe $id
}
