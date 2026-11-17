using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace WinTune.Core;

/// <summary>
/// Locks made with file permissions: a deny rule for "Everyone" on a file or a folder,
/// like icacls /deny. It stops a program from rewriting what has been removed.
/// </summary>
internal static class AclUtil
{
    private static SecurityIdentifier Everyone => new(WellKnownSidType.WorldSid, null);

    /// <summary>True if the file or folder has a deny rule for "Everyone".</summary>
    public static bool HasDeny(string path)
    {
        try
        {
            FileSystemSecurity security;
            if (Directory.Exists(path))
            {
                security = new DirectoryInfo(path).GetAccessControl();
            }
            else if (File.Exists(path))
            {
                security = new FileInfo(path).GetAccessControl();
            }
            else
            {
                return false;
            }

            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, false, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType == AccessControlType.Deny && rule.IdentityReference.Equals(Everyone))
                {
                    return true;
                }
            }

            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // If even the permissions cannot be read, the lock is there.
            return true;
        }
    }

    /// <summary>Adds or removes the deny rule on a file.</summary>
    public static void SetFileDeny(string path, FileSystemRights rights, bool deny)
    {
        var info = new FileInfo(path);
        FileSecurity security = info.GetAccessControl();
        var rule = new FileSystemAccessRule(Everyone, rights, AccessControlType.Deny);

        if (deny)
        {
            security.AddAccessRule(rule);
        }
        else
        {
            security.RemoveAccessRuleAll(rule);
        }

        info.SetAccessControl(security);
    }

    /// <summary>True if the folder exists and is locked for writing.</summary>
    public static bool IsFolderBlocked(string path) => Directory.Exists(path) && HasDeny(path);

    /// <summary>
    /// Empties the folder (or creates it empty) and denies write access to everyone: anything trying to install
    /// something there fails.
    /// </summary>
    public static void BlockFolder(string path)
    {
        var info = new DirectoryInfo(path);

        if (info.Exists)
        {
            foreach (FileSystemInfo item in info.GetFileSystemInfos())
            {
                try
                {
                    if (item is DirectoryInfo folder)
                    {
                        folder.Delete(true);
                    }
                    else
                    {
                        item.Delete();
                    }
                }
                catch
                {
                    // A file in use stays: the write lock applies anyway.
                }
            }
        }
        else
        {
            info.Create();
        }

        DirectorySecurity security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(Everyone, FileSystemRights.Write, AccessControlType.Deny));
        info.SetAccessControl(security);
    }

    /// <summary>Removes the write lock from the folder, if there is one.</summary>
    public static void UnblockFolder(string path)
    {
        var info = new DirectoryInfo(path);
        if (!info.Exists)
        {
            return;
        }

        DirectorySecurity security = info.GetAccessControl();
        security.RemoveAccessRuleAll(new FileSystemAccessRule(Everyone, FileSystemRights.Write, AccessControlType.Deny));
        info.SetAccessControl(security);
    }
}
