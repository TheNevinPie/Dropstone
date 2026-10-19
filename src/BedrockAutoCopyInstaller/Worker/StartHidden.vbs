' Bedrock AutoCopy - hidden PowerShell runner.
' Runs a PowerShell script with no visible window (used by the scheduled task).
'
' Usage:
'   wscript.exe StartHidden.vbs <script.ps1> [args...]

Option Explicit

Dim args, i, cmd
Set args = WScript.Arguments
If args.Count < 1 Then
    WScript.Quit 1
End If

cmd = "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File """ & args(0) & """"
For i = 1 To args.Count - 1
    cmd = cmd & " """ & args(i) & """"
Next

CreateObject("WScript.Shell").Run cmd, 0, False
