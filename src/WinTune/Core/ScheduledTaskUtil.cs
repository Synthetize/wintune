using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace WinTune.Core;

/// <summary>
/// Windows scheduled tasks created by WinTune, in the "WinTune" folder.
/// The ones run once as SYSTEM turn back on by themselves the features that were turned off "for a while";
/// the ones at sign-in start the external tools with administrator rights.
/// </summary>
internal static class ScheduledTaskUtil
{
    private const string Folder = "WinTune";

    private static string SchTasks => ProcessRunner.SystemTool("schtasks.exe");

    /// <summary>Creates (or replaces) the task. If the PC is off at the planned time, it runs at the first useful start.</summary>
    public static void CreateOnce(string name, DateTime when, string description, IEnumerable<(string Command, string Arguments)> actions)
    {
        var actionXml = new StringBuilder();
        foreach ((string command, string arguments) in actions)
        {
            actionXml.Append("    <Exec>\n");
            actionXml.Append("      <Command>").Append(SecurityElement.Escape(command)).Append("</Command>\n");
            actionXml.Append("      <Arguments>").Append(SecurityElement.Escape(arguments)).Append("</Arguments>\n");
            actionXml.Append("    </Exec>\n");
        }

        string xml =
            "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\n" +
            "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\n" +
            "  <RegistrationInfo>\n" +
            "    <Description>" + SecurityElement.Escape(description) + "</Description>\n" +
            "  </RegistrationInfo>\n" +
            "  <Triggers>\n" +
            "    <TimeTrigger>\n" +
            "      <StartBoundary>" + when.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + "</StartBoundary>\n" +
            "      <Enabled>true</Enabled>\n" +
            "    </TimeTrigger>\n" +
            "  </Triggers>\n" +
            "  <Principals>\n" +
            "    <Principal id=\"Author\">\n" +
            "      <UserId>S-1-5-18</UserId>\n" +
            "      <RunLevel>HighestAvailable</RunLevel>\n" +
            "    </Principal>\n" +
            "  </Principals>\n" +
            "  <Settings>\n" +
            "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\n" +
            "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\n" +
            "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\n" +
            "    <StartWhenAvailable>true</StartWhenAvailable>\n" +
            "    <Enabled>true</Enabled>\n" +
            "    <ExecutionTimeLimit>PT10M</ExecutionTimeLimit>\n" +
            "  </Settings>\n" +
            "  <Actions Context=\"Author\">\n" +
            actionXml +
            "  </Actions>\n" +
            "</Task>\n";

        string file = Path.Combine(Path.GetTempPath(), "wintune-task-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            // schtasks wants the file in UTF-16, as declared in the header.
            File.WriteAllText(file, xml, Encoding.Unicode);
            ProcessRunner.RunOrThrow(SchTasks, "/Create /TN \"" + Folder + "\\" + name + "\" /XML \"" + file + "\" /F");
        }
        finally
        {
            try
            {
                File.Delete(file);
            }
            catch
            {
                // A leftover temporary file is not a problem.
            }
        }
    }

    /// <summary>
    /// Creates (or replaces) a task that starts a program when the current user signs in to Windows,
    /// with the highest privileges and no time limit.
    /// </summary>
    public static void CreateAtLogon(
        string name,
        string description,
        string command,
        string arguments,
        string workingDirectory,
        int repeatEveryHours = 0,
        bool needsNetwork = false)
    {
        // With a repetition the task is a short check: limited time and catch-up if the PC was off.
        string repetition = repeatEveryHours > 0
            ? "      <Repetition>\n" +
              "        <Interval>PT" + repeatEveryHours.ToString(CultureInfo.InvariantCulture) + "H</Interval>\n" +
              "        <StopAtDurationEnd>false</StopAtDurationEnd>\n" +
              "      </Repetition>\n"
            : "";
        string timeLimit = repeatEveryHours > 0 ? "PT30M" : "PT0S";

        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        string userName = identity.Name;
        string userSid = identity.User?.Value ?? userName;

        string xml =
            "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\n" +
            "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\n" +
            "  <RegistrationInfo>\n" +
            "    <Description>" + SecurityElement.Escape(description) + "</Description>\n" +
            "  </RegistrationInfo>\n" +
            "  <Triggers>\n" +
            "    <LogonTrigger>\n" +
            repetition +
            "      <Enabled>true</Enabled>\n" +
            "      <UserId>" + SecurityElement.Escape(userName) + "</UserId>\n" +
            "      <Delay>PT10S</Delay>\n" +
            "    </LogonTrigger>\n" +
            "  </Triggers>\n" +
            "  <Principals>\n" +
            "    <Principal id=\"Author\">\n" +
            "      <UserId>" + SecurityElement.Escape(userSid) + "</UserId>\n" +
            "      <LogonType>InteractiveToken</LogonType>\n" +
            "      <RunLevel>HighestAvailable</RunLevel>\n" +
            "    </Principal>\n" +
            "  </Principals>\n" +
            "  <Settings>\n" +
            "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\n" +
            "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\n" +
            "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\n" +
            (repeatEveryHours > 0 ? "    <StartWhenAvailable>true</StartWhenAvailable>\n" : "") +
            (needsNetwork ? "    <RunOnlyIfNetworkAvailable>true</RunOnlyIfNetworkAvailable>\n" : "") +
            "    <Enabled>true</Enabled>\n" +
            "    <ExecutionTimeLimit>" + timeLimit + "</ExecutionTimeLimit>\n" +
            "  </Settings>\n" +
            "  <Actions Context=\"Author\">\n" +
            "    <Exec>\n" +
            "      <Command>" + SecurityElement.Escape(command) + "</Command>\n" +
            (arguments.Length > 0 ? "      <Arguments>" + SecurityElement.Escape(arguments) + "</Arguments>\n" : "") +
            (workingDirectory.Length > 0 ? "      <WorkingDirectory>" + SecurityElement.Escape(workingDirectory) + "</WorkingDirectory>\n" : "") +
            "    </Exec>\n" +
            "  </Actions>\n" +
            "</Task>\n";

        Register(name, xml);
    }

    private static void Register(string name, string xml)
    {
        string file = Path.Combine(Path.GetTempPath(), "wintune-task-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            // schtasks wants the file in UTF-16, as declared in the header.
            File.WriteAllText(file, xml, Encoding.Unicode);
            ProcessRunner.RunOrThrow(SchTasks, "/Create /TN \"" + Folder + "\\" + name + "\" /XML \"" + file + "\" /F");
        }
        finally
        {
            try
            {
                File.Delete(file);
            }
            catch
            {
                // A leftover temporary file is not a problem.
            }
        }
    }

    /// <summary>True if the task exists.</summary>
    public static bool Exists(string name)
    {
        try
        {
            return ProcessRunner.Run(SchTasks, "/Query /TN \"" + Folder + "\\" + name + "\"").Success;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Deletes the task, if it exists.</summary>
    public static void Delete(string name)
    {
        try
        {
            ProcessRunner.Run(SchTasks, "/Delete /TN \"" + Folder + "\\" + name + "\" /F");
        }
        catch
        {
            // The task may not exist.
        }
    }
}
