' ============================================================
'  EQL Metrics — silent launcher
'  Double-click to build + start the overlay with NO console window.
'  (Same as Run-EqlMetrics.cmd, but hidden. If it ever seems to do
'   nothing, run the .cmd instead so you can see build errors.)
' ============================================================
Dim fso, sh, here
Set fso = CreateObject("Scripting.FileSystemObject")
Set sh  = CreateObject("WScript.Shell")
here = fso.GetParentFolderName(WScript.ScriptFullName)
sh.CurrentDirectory = here

' Build quietly, then launch the app detached. Window style 0 = hidden.
sh.Run "cmd /c dotnet build -c Release -v quiet && start """" ""bin\Release\net10.0-windows\EqlMetrics.exe""", 0, False
