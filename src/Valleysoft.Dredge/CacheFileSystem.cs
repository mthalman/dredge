using System.Security.AccessControl;
using System.Security.Principal;

namespace Valleysoft.Dredge;

internal static class CacheFileSystem
{
    public static void CreateDirectory(string path)
    {
        bool exists = Directory.Exists(path);
        if (exists)
        {
            ValidateNotLink(path);
        }
        if (OperatingSystem.IsWindows())
        {
            DirectoryInfo directory = new(path);
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            SecurityIdentifier user = identity.User ??
                throw new UnauthorizedAccessException("Could not determine the cache owner.");
            DirectorySecurity security = new();
            security.SetOwner(user);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            if (!exists)
            {
                directory.Create(security);
            }
            DirectorySecurity actual = directory.GetAccessControl();
            bool privateAccess = user.Equals(actual.GetOwner(typeof(SecurityIdentifier)));
            foreach (FileSystemAccessRule rule in actual.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType == AccessControlType.Allow &&
                    !user.Equals(rule.IdentityReference) &&
                    !rule.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)) &&
                    !rule.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)))
                {
                    privateAccess = false;
                }
            }
            if (!privateAccess)
            {
                throw new UnauthorizedAccessException($"Cache directory '{path}' must be private to the current user.");
            }
        }
        else
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            UnixFileMode permissions = File.GetUnixFileMode(path);
            if ((permissions & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            {
                throw new UnauthorizedAccessException($"Cache directory '{path}' must be private to the current user (0700).");
            }
        }
    }

    public static FileStream CreateFile(string path)
    {
        FileStreamOptions options = new()
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        return new FileStream(path, options);
    }

    public static void ValidateNotLink(string path)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException($"Cache path '{path}' cannot be a symbolic link or reparse point.");
        }
    }
}
