' LeviLauncher AutoCopy - launcher wrapper.
' Runs Sync.ps1 silently (waits for it), then starts LeviLauncher.
' Use this to start LeviLauncher so a newly installed version is topped up
' before you launch a game from it.
'
' The LeviLauncher.exe path is read from "launcherExe=" in config.ini next to
' this script; if missing, common install locations are probed.

Option Explicit

Dim fso, shell, scriptDir, syncScript, launcher, cfg, text, lines, line, parts

Set fso = CreateObject("Scripting.FileSystemObject")
Set shell = CreateObject("WScript.Shell")

scriptDir = fso.GetParentFolderName(WScript.ScriptFullName)
syncScript = fso.BuildPath(scriptDir, "Sync.ps1")
launcher = ""
cfg = fso.BuildPath(scriptDir, "config.ini")

If fso.FileExists(cfg) Then
    text = fso.OpenTextFile(cfg, 1).ReadAll
    lines = Split(text, vbCrLf)
    For Each line In lines
        If InStr(line, "=") > 0 Then
            parts = Split(line, "=", 2)
            If Trim(LCase(parts(0))) = "launcherexe" Then
                launcher = Trim(parts(1))
                launcher = Replace(launcher, """", "")
                Exit For
            End If
        End If
    Next
End If

If launcher = "" Or Not fso.FileExists(launcher) Then
    Dim candidates, c
    candidates = Array( _
        shell.ExpandEnvironmentStrings("%ProgramFiles%\LeviMC\LeviLauncher\LeviLauncher.exe"), _
        shell.ExpandEnvironmentStrings("%ProgramFiles%\LeviLauncher\LeviLauncher.exe"), _
        shell.ExpandEnvironmentStrings("%ProgramFiles(x86)%\LeviLauncher\LeviLauncher.exe"), _
        shell.ExpandEnvironmentStrings("%LOCALAPPDATA%\Programs\LeviLauncher\LeviLauncher.exe"), _
        shell.ExpandEnvironmentStrings("%LOCALAPPDATA%\LeviMC\LeviLauncher\LeviLauncher.exe") _
    )
    For Each c In candidates
        If fso.FileExists(c) Then
            launcher = c
            Exit For
        End If
    Next
End If

If fso.FileExists(syncScript) Then
    shell.Run "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File """ & syncScript & """ -Quiet", 0, True
End If

If launcher <> "" And fso.FileExists(launcher) Then
    shell.Run """" & launcher & """", 1, False
Else
    MsgBox "LeviLauncher.exe was not found." & vbCrLf & vbCrLf & _
           "Set launcherExe=<full path> in:" & vbCrLf & cfg, 16, "LeviLauncher AutoCopy"
End If
