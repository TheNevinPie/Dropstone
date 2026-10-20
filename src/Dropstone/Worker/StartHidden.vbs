' Dropstone - hidden PowerShell runner.
' Runs a PowerShell script with no visible window (used by the scheduled task).
'
' The script is started with "wait for it to finish", so the caller (the
' scheduled task) sees the real exit code and the real run time. Without that
' the task would report success while the sync is still running, and the
' task's own time limit and instance control would not apply.
'
' Usage:
'   wscript.exe StartHidden.vbs <script.ps1> [args...]

Option Explicit

Dim args, i, cmd, exitCode, shell
Set args = WScript.Arguments
If args.Count < 1 Then
    WScript.Quit 1
End If

cmd = "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File """ & args(0) & """"
For i = 1 To args.Count - 1
    cmd = cmd & " """ & args(i) & """"
Next

Set shell = CreateObject("WScript.Shell")
exitCode = 0
On Error Resume Next
exitCode = shell.Run(cmd, 0, True)
On Error GoTo 0
If exitCode = 0 And Err.Number <> 0 Then
    exitCode = 1
End If
WScript.Quit exitCode
