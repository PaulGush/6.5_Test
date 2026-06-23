using System;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// Adds a "Tools/Open Claude Code" menu item that launches the Claude Code
/// CLI in a terminal rooted at this Unity project's directory.
/// </summary>
public static class OpenClaudeCode
{
    [MenuItem("Tools/Open Claude Code", priority = 0)]
    public static void Open()
    {
        // Application.dataPath points at <Project>/Assets; the project root is its parent.
        string projectRoot = Directory.GetParent(Application.dataPath).FullName;

        try
        {
#if UNITY_EDITOR_WIN
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/k claude",
                WorkingDirectory = projectRoot,
                UseShellExecute = true
            });
#elif UNITY_EDITOR_OSX
            // Open Terminal.app at the project root and run claude.
            string osa = $"tell application \"Terminal\" to do script \"cd '{projectRoot}' && claude\"\n" +
                         "tell application \"Terminal\" to activate";
            Process.Start(new ProcessStartInfo
            {
                FileName = "osascript",
                Arguments = $"-e \"{osa.Replace("\"", "\\\"")}\"",
                UseShellExecute = false
            });
#else
            // Linux: launch a login shell so ~/.local/bin (where `claude` typically
            // lives) is on PATH, then keep the terminal open after claude exits.
            if (!TryStartLinuxTerminal(projectRoot))
            {
                Debug.LogError("Open Claude Code: no supported terminal found " +
                               "(tried gnome-terminal, x-terminal-emulator, konsole, xterm).");
            }
#endif
        }
        catch (Exception e)
        {
            Debug.LogError($"Open Claude Code: failed to launch terminal. {e.Message}");
        }
    }

#if !UNITY_EDITOR_WIN && !UNITY_EDITOR_OSX
    private static bool TryStartLinuxTerminal(string projectRoot)
    {
        // Login + interactive shell so profile/rc files set PATH for `claude`.
        const string shellCmd = "claude; exec bash";

        var candidates = new[]
        {
            new ProcessStartInfo
            {
                FileName = "gnome-terminal",
                Arguments = $"--working-directory=\"{projectRoot}\" -- bash -lic \"{shellCmd}\""
            },
            new ProcessStartInfo
            {
                FileName = "konsole",
                Arguments = $"--workdir \"{projectRoot}\" -e bash -lic \"{shellCmd}\""
            },
            new ProcessStartInfo
            {
                FileName = "x-terminal-emulator",
                Arguments = $"-e bash -lic \"cd '{projectRoot}' && {shellCmd}\""
            },
            new ProcessStartInfo
            {
                FileName = "xterm",
                Arguments = $"-e bash -lic \"cd '{projectRoot}' && {shellCmd}\""
            },
        };

        foreach (var psi in candidates)
        {
            psi.UseShellExecute = false;
            try
            {
                Process.Start(psi);
                return true;
            }
            catch
            {
                // Terminal not installed; try the next candidate.
            }
        }

        return false;
    }
#endif
}
