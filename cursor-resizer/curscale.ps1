param($cs, $in, $out, $preview)
Add-Type -Path $cs -ReferencedAssemblies System.Drawing
New-Item -ItemType Directory -Force $out | Out-Null
foreach($f in Get-ChildItem $in -File){ $b=[IO.File]::ReadAllBytes($f.FullName); if ($f.Extension -eq '.ani') { $r=[CurScale]::ScaleAni($b,0.85) } else { $r=[CurScale]::ScaleCur($b,0.85) }; [IO.File]::WriteAllBytes((Join-Path $out $f.Name),$r) }
"written: $((Get-ChildItem $out).Count) files"
Add-Type -AssemblyName System.Drawing
Add-Type -Name C -Namespace PrevT -MemberDefinition '[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern System.IntPtr LoadImage(System.IntPtr h, string n, uint t, int cx, int cy, uint f); [DllImport("user32.dll")] public static extern bool DrawIconEx(System.IntPtr hdc, int x, int y, System.IntPtr i, int cx, int cy, uint s, System.IntPtr b, uint f);'
$names='Pointer','Link','Text','Dng1','Move','Unavailiable','Busy'
$bm=New-Object Drawing.Bitmap (80*$names.Count),170; $g=[Drawing.Graphics]::FromImage($bm); $g.Clear([Drawing.Color]::FromArgb(205,205,205))
$i=0; foreach($n in $names){ $ext='.cur'; if($n -eq 'Busy'){$ext='.ani'}; $row=0; foreach($dir in $in,$out){ $c=[PrevT.C]::LoadImage([IntPtr]::Zero,"$dir\$n$ext",2,64,64,0x10); if ($c -eq [IntPtr]::Zero) { "LOAD FAILED: $dir\$n$ext" }; $h=$g.GetHdc(); [PrevT.C]::DrawIconEx($h,$i*80+8,8+$row*85,$c,64,64,0,[IntPtr]::Zero,3)|Out-Null; $g.ReleaseHdc($h); $row++ }; $i++ }
$bm.Save($preview)
