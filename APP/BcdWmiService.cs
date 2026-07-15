using System.ComponentModel;
using System.Management;
using System.Runtime.InteropServices;
using BootSequence.Core;

namespace BootSequence.SystemServices;

public sealed class BcdWmiService : IBootConfigurationService
{
    private const string MutationMutexName = @"Global\BootSequence.BcdMutation";
    private const uint WindowsOsLoaderType = 0x10200003;
    private const uint ApplicationDevice = 0x11000001;
    private const uint ApplicationPath = 0x12000002;
    private const uint Description = 0x12000004;
    private const uint SystemRoot = 0x22000002;
    private const uint DisplayOrder = 0x24000001;
    private const uint BootSequence = 0x24000002;
    private const uint PersistBootSequence = 0x26000031;
    private const string BootManagerId = "{9dea862c-5cdd-4e70-acc1-f32b344d4795}";
    private readonly IDiagnosticLogger? _logger;

    public BcdWmiService(IDiagnosticLogger? logger = null)
    {
        _logger = logger;
    }

    public IReadOnlyList<BootEntry> ReadEntries()
    {
        using var context = new BcdContext();
        string[] ids = context.ReadIdList(context.BootManagerPath, DisplayOrder);
        string currentId = context.TryReadObjectId("{current}");
        (string currentDrive, string currentRoot) = CurrentWindowsLocation();
        var entries = new List<BootEntry>();

        foreach (string requestedId in ids)
        {
            using ManagementBaseObject? entryObject = context.TryOpenObject(requestedId);
            if (entryObject is null || ReadUInt32(entryObject, "Type") != WindowsOsLoaderType)
            {
                continue;
            }

            string id = ReadString(entryObject, "Id");
            if (string.IsNullOrWhiteSpace(id)) id = requestedId;
            string name = context.ReadStringElement(entryObject, Description);
            if (string.IsNullOrWhiteSpace(name)) name = "Windows";
            string applicationPath = context.ReadStringElement(entryObject, ApplicationPath);
            string drive = ResolveDrive(context.ReadDevicePath(entryObject));
            string systemRoot = context.ReadStringElement(entryObject, SystemRoot).TrimEnd('\\', '/');
            bool matchesCurrentId = !string.IsNullOrEmpty(currentId) &&
                string.Equals(id, currentId, StringComparison.OrdinalIgnoreCase);
            bool matchesCurrentLocation = !string.IsNullOrEmpty(drive) && !string.IsNullOrEmpty(currentDrive) &&
                string.Equals(drive, currentDrive, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(systemRoot) &&
                string.Equals(systemRoot, currentRoot, StringComparison.OrdinalIgnoreCase);
            bool isCurrent = matchesCurrentId || matchesCurrentLocation;

            BootEntryAvailability availability;
            if (string.IsNullOrWhiteSpace(applicationPath))
            {
                availability = BootEntryAvailability.InvalidConfiguration;
            }
            else if (drive.Length != 2 || drive[1] != ':')
            {
                availability = BootEntryAvailability.UnresolvedDevice;
            }
            else
            {
                string loaderFile = drive + applicationPath;
                try
                {
                    FileAttributes attributes = File.GetAttributes(loaderFile);
                    availability = (attributes & FileAttributes.Directory) == 0
                        ? BootEntryAvailability.Available
                        : BootEntryAvailability.LoaderMissing;
                }
                catch (FileNotFoundException)
                {
                    availability = BootEntryAvailability.LoaderMissing;
                }
                catch (DirectoryNotFoundException)
                {
                    availability = BootEntryAvailability.LoaderMissing;
                }
                catch (Exception exception) when (exception is IOException or
                                                   UnauthorizedAccessException or
                                                   ArgumentException or
                                                   NotSupportedException)
                {
                    availability = BootEntryAvailability.InspectionFailed;
                    _logger?.Error("loader.inspect", exception);
                }
            }

            entries.Add(new BootEntry(
                id,
                name,
                string.IsNullOrEmpty(drive) ? ShortId(id) : drive,
                applicationPath,
                isCurrent,
                availability));
        }

        return entries;
    }

    public IReadOnlyList<string> ReadOneTimeSequence()
    {
        using var context = new BcdContext();
        return context.ReadIdList(context.BootManagerPath, BootSequence);
    }

    public BootSequenceMutationResult TrySetOneTimeSequenceIfEmpty(string id)
    {
        return WithMutationLock(() =>
        {
            using var context = new BcdContext();
            BootSequenceMutationResult result = BootSequenceTransaction.TrySetIfEmpty(
                id,
                () => context.ReadIdList(context.BootManagerPath, BootSequence),
                () => IsPersistent(context),
                value => context.SetIdList(context.BootManagerPath, BootSequence, value),
                () => context.DeleteElement(context.BootManagerPath, BootSequence));
            if (result == BootSequenceMutationResult.WriteFailed)
            {
                _logger?.Error("bcd.write-result",
                    new ManagementException("BCD SetObjectListElement reported failure"));
            }
            else if (result == BootSequenceMutationResult.VerificationFailed)
            {
                _logger?.Error("bcd.verify-result",
                    new ManagementException("BootSequence changed before it could be verified"));
            }

            return result;
        });
    }

    public bool ClearOneTimeSequenceIfMatches(string id)
    {
        return WithMutationLock(() =>
        {
            using var context = new BcdContext();
            return BootSequenceTransaction.ClearIfMatches(
                id,
                () => context.ReadIdList(context.BootManagerPath, BootSequence),
                () => context.DeleteElement(context.BootManagerPath, BootSequence));
        });
    }

    private static bool IsPersistent(BcdContext context)
    {
        using ManagementBaseObject? element = context.TryGetElement(
            context.BootManagerPath,
            PersistBootSequence);
        return element is not null && ReadBoolean(element, "Boolean");
    }

    private static T WithMutationLock<T>(Func<T> action)
    {
        using var mutex = new Mutex(false, MutationMutexName);
        bool acquired = false;
        try
        {
            try
            {
                acquired = mutex.WaitOne(TimeSpan.FromSeconds(10));
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (!acquired)
            {
                throw new TimeoutException("Timed out waiting for the BCD mutation lock");
            }

            return action();
        }
        finally
        {
            if (acquired)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    private static (string Drive, string Root) CurrentWindowsLocation()
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string root = Path.GetPathRoot(windows) ?? string.Empty;
        string drive = root.Length >= 2 ? root[..2].ToUpperInvariant() : string.Empty;
        string relativeRoot = windows.Length > drive.Length ? windows[drive.Length..].TrimEnd('\\', '/') : string.Empty;
        return (drive, relativeRoot);
    }

    private static string ResolveDrive(string devicePath)
    {
        const string prefix = "partition=";
        if (devicePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            devicePath = devicePath[prefix.Length..];
        }

        if (devicePath.Length >= 2 && devicePath[1] == ':')
        {
            return devicePath[..2].ToUpperInvariant();
        }

        var target = new char[32768];
        for (char letter = 'A'; letter <= 'Z'; letter++)
        {
            string drive = $"{letter}:";
            uint length = QueryDosDevice(drive, target, target.Length);
            if (length != 0)
            {
                int terminator = Array.IndexOf(target, '\0');
                string mapped = new(target, 0, terminator >= 0 ? terminator : (int)length);
                if (string.Equals(mapped, devicePath, StringComparison.OrdinalIgnoreCase)) return drive;
            }
        }

        return string.Empty;
    }

    private static string ShortId(string id)
    {
        string clean = new(id.Where(character => character is not '{' and not '}' and not '-').ToArray());
        return clean.Length > 6 ? clean[^6..] : clean;
    }

    private static string ReadString(ManagementBaseObject value, string property) =>
        value[property] as string ?? string.Empty;

    private static uint ReadUInt32(ManagementBaseObject value, string property) =>
        value[property] is null ? 0 : Convert.ToUInt32(value[property]);

    private static bool ReadBoolean(ManagementBaseObject value, string property) =>
        value[property] is not null && Convert.ToBoolean(value[property]);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(string deviceName, char[] targetPath, int maxLength);

    private sealed class BcdContext : IDisposable
    {
        private readonly ManagementScope _scope;
        private readonly ManagementClass _storeClass;
        public string BootManagerPath { get; }

        public BcdContext()
        {
            NativePrivileges.EnableOrThrow("SeBackupPrivilege");
            NativePrivileges.EnableOrThrow("SeRestorePrivilege");

            _scope = new ManagementScope(@"\\.\ROOT\WMI", new ConnectionOptions
            {
                EnablePrivileges = true,
                Impersonation = ImpersonationLevel.Impersonate,
                Authentication = AuthenticationLevel.PacketPrivacy
            });
            _scope.Connect();
            _storeClass = new ManagementClass(_scope, new ManagementPath("BcdStore"), null);

            using ManagementBaseObject input = _storeClass.GetMethodParameters("OpenStore");
            input["File"] = string.Empty;
            using ManagementBaseObject output = _storeClass.InvokeMethod("OpenStore", input, null)
                ?? throw new ManagementException("Cannot open the system BCD store");
            if (!MethodSucceeded(output)) throw new ManagementException("Cannot open the system BCD store");
            using ManagementBaseObject store = output["Store"] as ManagementBaseObject
                ?? throw new ManagementException("System BCD store was not returned");
            string storePath = ReadString(store, "__PATH");
            if (string.IsNullOrEmpty(storePath)) storePath = "BcdStore.FilePath=\"\"";

            using ManagementBaseObject bootManager = OpenObject(storePath, BootManagerId)
                ?? throw new ManagementException("Windows Boot Manager was not found");
            BootManagerPath = ReadObjectPath(bootManager);
            if (string.IsNullOrEmpty(BootManagerPath))
            {
                throw new ManagementException(
                    "Windows Boot Manager returned neither __PATH nor __RELPATH");
            }

            StorePath = storePath;
        }

        private string StorePath { get; }

        public ManagementBaseObject? TryOpenObject(string id)
        {
            return OpenObject(StorePath, id);
        }

        public string TryReadObjectId(string id)
        {
            using ManagementBaseObject? value = TryOpenObject(id);
            return value is null ? string.Empty : ReadString(value, "Id");
        }

        public ManagementBaseObject? TryGetElement(string objectPath, uint type)
        {
            using var owner = new ManagementObject(_scope, new ManagementPath(objectPath), null);
            using ManagementBaseObject input = owner.GetMethodParameters("GetElement");
            input["Type"] = type;
            using ManagementBaseObject output = owner.InvokeMethod("GetElement", input, null)
                ?? throw new ManagementException("BCD GetElement returned no result");
            if (!MethodSucceeded(output)) return null;
            return output["Element"] as ManagementBaseObject;
        }

        public string ReadStringElement(ManagementBaseObject owner, uint type)
        {
            string path = ReadObjectPath(owner);
            if (string.IsNullOrEmpty(path))
            {
                throw new ManagementException("BCD object path is unavailable");
            }

            using ManagementBaseObject? element = TryGetElement(path, type);
            return element is null ? string.Empty : ReadString(element, "String");
        }

        public string ReadDevicePath(ManagementBaseObject owner)
        {
            string path = ReadObjectPath(owner);
            if (string.IsNullOrEmpty(path))
            {
                throw new ManagementException("BCD object path is unavailable");
            }

            using ManagementBaseObject? element = TryGetElement(path, ApplicationDevice);
            if (element?["Device"] is not ManagementBaseObject device) return string.Empty;
            using (device) return ReadString(device, "Path");
        }

        public string[] ReadIdList(string objectPath, uint type)
        {
            using ManagementBaseObject? element = TryGetElement(objectPath, type);
            return element?["Ids"] as string[] ?? [];
        }

        public bool SetIdList(string objectPath, uint type, string id)
        {
            using var owner = new ManagementObject(_scope, new ManagementPath(objectPath), null);
            using ManagementBaseObject input = owner.GetMethodParameters("SetObjectListElement");
            input["Type"] = type;
            input["Ids"] = new[] { id };
            using ManagementBaseObject output = owner.InvokeMethod("SetObjectListElement", input, null)
                ?? throw new ManagementException("BCD write returned no result");
            return MethodSucceeded(output);
        }

        public bool DeleteElement(string objectPath, uint type)
        {
            using var owner = new ManagementObject(_scope, new ManagementPath(objectPath), null);
            using ManagementBaseObject input = owner.GetMethodParameters("DeleteElement");
            input["Type"] = type;
            using ManagementBaseObject output = owner.InvokeMethod("DeleteElement", input, null)
                ?? throw new ManagementException("BCD delete returned no result");
            return MethodSucceeded(output);
        }

        private ManagementBaseObject? OpenObject(string storePath, string id)
        {
            using var store = new ManagementObject(_scope, new ManagementPath(storePath), null);
            using ManagementBaseObject input = store.GetMethodParameters("OpenObject");
            input["Id"] = id;
            using ManagementBaseObject output = store.InvokeMethod("OpenObject", input, null)
                ?? throw new ManagementException("BCD OpenObject returned no result");
            if (!MethodSucceeded(output)) return null;
            return output["Object"] as ManagementBaseObject;
        }

        private static bool MethodSucceeded(ManagementBaseObject output)
        {
            object? value = output["ReturnValue"];
            return value switch
            {
                bool boolean => boolean,
                byte number => number != 0,
                int number => number != 0,
                uint number => number != 0,
                _ => false
            };
        }

        public void Dispose() => _storeClass.Dispose();
    }

    private static string ReadObjectPath(ManagementBaseObject value)
    {
        string path = ReadString(value, "__PATH");
        return string.IsNullOrWhiteSpace(path)
            ? ReadString(value, "__RELPATH")
            : path;
    }
}

internal static class NativePrivileges
{
    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x00000002;
    private const int ErrorSuccess = 0;

    public static void EnableOrThrow(string name)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out nint token))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Cannot open the process token for {name}");
        }

        try
        {
            if (!LookupPrivilegeValue(null, name, out Luid luid))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Cannot look up {name}");
            }

            var privileges = new TokenPrivileges
            {
                PrivilegeCount = 1,
                Privileges = new LuidAndAttributes { Luid = luid, Attributes = SePrivilegeEnabled }
            };
            Marshal.SetLastPInvokeError(ErrorSuccess);
            if (!AdjustTokenPrivileges(token, false, ref privileges, 0, nint.Zero, nint.Zero))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Cannot enable {name}");
            }

            int error = Marshal.GetLastPInvokeError();
            if (error != ErrorSuccess)
            {
                throw new Win32Exception(error, $"The process token does not contain {name}");
            }
        }
        finally
        {
            CloseHandle(token);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct LuidAndAttributes { public Luid Luid; public uint Attributes; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges { public uint PrivilegeCount; public LuidAndAttributes Privileges; }

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(nint process, uint desiredAccess, out nint token);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(
        nint token,
        bool disableAllPrivileges,
        ref TokenPrivileges newState,
        uint bufferLength,
        nint previousState,
        nint returnLength);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);
}
