// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Winora.Elevation.Windows;

/// <summary>Owned kernel handle (process, token or job) closed with CloseHandle.</summary>
internal sealed class SafeKernelHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeKernelHandle() : base(true) { }
    protected override bool ReleaseHandle() => Native.CloseHandle(handle);
}

[StructLayout(LayoutKind.Sequential)]
internal struct ByHandleFileInformation
{
    public uint FileAttributes;
    public uint CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
    public uint VolumeSerialNumber;
    public uint FileSizeHigh, FileSizeLow;
    public uint NumberOfLinks;
    public uint FileIndexHigh, FileIndexLow;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct FileIdInfo
{
    public ulong VolumeSerialNumber;
    public fixed byte FileId[16];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WintrustFileInfo
{
    public uint cbStruct;
    public char* pcwszFilePath;
    public nint hFile;
    public Guid* pgKnownSubject;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WintrustData
{
    public uint cbStruct;
    public void* pPolicyCallbackData;
    public void* pSIPClientData;
    public uint dwUIChoice;
    public uint fdwRevocationChecks;
    public uint dwUnionChoice;
    public WintrustFileInfo* pFile;
    public uint dwStateAction;
    public nint hWVTStateData;
    public char* pwszURLReference;
    public uint dwProvFlags;
    public uint dwUIContext;
    public void* pSignatureSettings;
}

/// <summary>Only the leading fields of CRYPT_PROVIDER_SGNR that are read (cbStruct, sftVerifyAsOf, csCertChain, pasCertChain).</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct CryptProviderSgnrHead
{
    public uint cbStruct;
    public uint VerifyLow, VerifyHigh;
    public uint csCertChain;
    public void* pasCertChain;
}

/// <summary>Only the leading fields of CRYPT_PROVIDER_CERT that are read (cbStruct, pCert).</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct CryptProviderCertHead
{
    public uint cbStruct;
    public void* pCert;
}

/// <summary>Leading fields of CERT_CONTEXT: encoding type, encoded bytes pointer and length.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct CertContextHead
{
    public uint dwCertEncodingType;
    public byte* pbCertEncoded;
    public uint cbCertEncoded;
}

[StructLayout(LayoutKind.Sequential)]
internal struct JobBasicLimit
{
    public long PerProcessUserTimeLimit;
    public long PerJobUserTimeLimit;
    public uint LimitFlags;
    public nuint MinimumWorkingSetSize;
    public nuint MaximumWorkingSetSize;
    public uint ActiveProcessLimit;
    public nuint Affinity;
    public uint PriorityClass;
    public uint SchedulingClass;
}

[StructLayout(LayoutKind.Sequential)]
internal struct JobExtendedLimit
{
    public JobBasicLimit Basic;
    public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes;
    public nuint ProcessMemoryLimit;
    public nuint JobMemoryLimit;
    public nuint PeakProcessMemoryUsed;
    public nuint PeakJobMemoryUsed;
}

/// <summary>SECURITY_ATTRIBUTES for CreateNamedPipeW (section 5: explicit descriptor, never inheritable).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SecurityAttributes
{
    public uint nLength;
    public nint lpSecurityDescriptor;
    public int bInheritHandle;
}
/// <summary>
/// Every P/Invoke of this project, in one file. Each entry names the section that needs it. Nothing here starts a process,
/// writes the registry, touches the service manager or creates a pipe: a source-scan test enforces that.
/// </summary>
internal static unsafe partial class Native
{
    internal const nint CurrentProcessPseudoHandle = -1;
    internal const nint CurrentThreadPseudoHandle = -2;

    // ---- security descriptors (section 5: SDDL build and in-memory verification) ----
    [LibraryImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision, out nint descriptor, out uint size);

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertSecurityDescriptorToStringSecurityDescriptorW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ConvertSecurityDescriptorToStringSecurityDescriptorW(nint descriptor, uint revision, uint information, out nint sddl, out uint length);

    [LibraryImport("advapi32.dll")]
    internal static partial uint GetSecurityDescriptorLength(nint descriptor);

    [LibraryImport("kernel32.dll")]
    internal static partial nint LocalFree(nint memory);

    // ---- security descriptor of an open file or directory (section 4.3 step 4 ACL rule; section 6.2 run-dir rule) ----
    [LibraryImport("advapi32.dll", EntryPoint = "GetKernelObjectSecurity", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetKernelObjectSecurity(SafeFileHandle handle, uint information, byte* descriptor, uint length, out uint needed);

    // ---- tokens and processes (section 7: identity facts; section 14.1 step 1: own token self-check) ----
    [LibraryImport("advapi32.dll", EntryPoint = "OpenProcessToken", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool OpenProcessToken(nint process, uint desiredAccess, out SafeKernelHandle token);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenThreadToken", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool OpenThreadToken(nint thread, uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool openAsSelf, out SafeKernelHandle token);

    [LibraryImport("advapi32.dll", EntryPoint = "GetTokenInformation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetTokenInformation(SafeHandle token, int informationClass, byte* buffer, uint length, out uint returned);

    [LibraryImport("kernel32.dll", EntryPoint = "OpenProcess", SetLastError = true)]
    internal static partial SafeKernelHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [LibraryImport("kernel32.dll", EntryPoint = "GetProcessId", SetLastError = true)]
    internal static partial uint GetProcessId(nint process);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool QueryFullProcessImageNameW(nint process, uint flags, char* buffer, ref uint size);

    [LibraryImport("kernel32.dll", EntryPoint = "GetProcessTimes", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetProcessTimes(nint process, out ulong creation, out ulong exit, out ulong kernel, out ulong user);

    // ---- file access for the image guard and the attestation reader (sections 4.3, 6.2, 6.3) ----
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, FileIdInfo* information, uint size);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    internal static partial uint GetFinalPathNameByHandleW(SafeFileHandle file, char* buffer, uint length, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDriveTypeW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint GetDriveTypeW(string rootPath);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetVolumeInformationW(string rootPath, char* volumeName, uint volumeNameSize, uint* serial, uint* maxComponentLength, uint* fileSystemFlags, char* fileSystemName, uint fileSystemNameSize);

    // ---- Authenticode on the held handle (section 4.3 step 5, A.10) ----
    [LibraryImport("wintrust.dll", EntryPoint = "WinVerifyTrust")]
    internal static partial int WinVerifyTrust(nint window, Guid* action, WintrustData* data);

    [LibraryImport("wintrust.dll", EntryPoint = "WTHelperProvDataFromStateData")]
    internal static partial void* WTHelperProvDataFromStateData(nint stateData);

    [LibraryImport("wintrust.dll", EntryPoint = "WTHelperGetProvSignerFromChain")]
    internal static partial CryptProviderSgnrHead* WTHelperGetProvSignerFromChain(void* provData, uint signerIndex, [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterSignerIndex);

    [LibraryImport("wintrust.dll", EntryPoint = "WTHelperGetProvCertFromChain")]
    internal static partial CryptProviderCertHead* WTHelperGetProvCertFromChain(CryptProviderSgnrHead* signer, uint certIndex);

    // ---- process hardening (section 14.1 steps 4 and 5, 14.3) ----
    [LibraryImport("kernel32.dll", EntryPoint = "SetProcessMitigationPolicy", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetProcessMitigationPolicy(int policy, uint* flags, nuint length);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial SafeKernelHandle CreateJobObjectW(nint attributes, string? name);

    [LibraryImport("kernel32.dll", EntryPoint = "SetInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetInformationJobObject(SafeHandle job, int informationClass, JobExtendedLimit* information, uint length);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool QueryInformationJobObject(SafeHandle job, int informationClass, JobExtendedLimit* information, uint length, out uint returned);

    [LibraryImport("kernel32.dll", EntryPoint = "AssignProcessToJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AssignProcessToJobObject(SafeHandle job, nint process);

    // ---- environment NAME enumeration (section 14.2): the single allow-listed environment access ----
    [LibraryImport("kernel32.dll", EntryPoint = "GetEnvironmentStringsW")]
    internal static partial char* GetEnvironmentStringsW();

    [LibraryImport("kernel32.dll", EntryPoint = "FreeEnvironmentStringsW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FreeEnvironmentStringsW(char* block);

    // ---- named pipe server and peer process id (section 5: creation with the exact descriptor; section 7.2 step 2: peer process id). Used only by Worker/PipeServer.cs ----
    [LibraryImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial Microsoft.Win32.SafeHandles.SafePipeHandle CreateNamedPipeW(string name, uint openMode, uint pipeMode, uint maxInstances, uint outBufferSize, uint inBufferSize, uint defaultTimeout, SecurityAttributes* attributes);

    [LibraryImport("kernel32.dll", EntryPoint = "GetNamedPipeClientProcessId", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint clientProcessId);

    // ---- machine registry. Used only by Worker/MachineRegistry.cs and Worker/RegistryFloorStore.cs ----
    internal const uint KeyQueryValue = 0x0001, KeySetValue = 0x0002, KeyWow64_64 = 0x0100, KeyWow64_32 = 0x0200;
    internal const uint RegSz = 1, RegBinary = 3, RegDword = 4, RegQword = 11;
    internal const int ErrorSuccess = 0, ErrorFileNotFound = 2, ErrorPathNotFound = 3, ErrorAccessDenied = 5, ErrorMoreData = 234;
    internal static readonly nint HkeyLocalMachine = unchecked((nint)(int)0x80000002);

    [LibraryImport("advapi32.dll", EntryPoint = "RegOpenKeyExW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int RegOpenKeyEx(nint key, string subKey, uint options, uint sam, out nint result);

    [LibraryImport("advapi32.dll", EntryPoint = "RegCreateKeyExW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int RegCreateKeyEx(nint key, string subKey, uint reserved, nint cls, uint options, uint sam, nint security, out nint result, out uint disposition);

    [LibraryImport("advapi32.dll", EntryPoint = "RegCloseKey")]
    internal static partial int RegCloseKey(nint key);

    [LibraryImport("advapi32.dll", EntryPoint = "RegQueryValueExW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int RegQueryValueEx(nint key, string name, nint reserved, out uint type, byte* data, ref uint size);

    [LibraryImport("advapi32.dll", EntryPoint = "RegSetValueExW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int RegSetValueEx(nint key, string name, uint reserved, uint type, byte* data, uint size);

    [LibraryImport("advapi32.dll", EntryPoint = "RegDeleteValueW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int RegDeleteValue(nint key, string name);

    [LibraryImport("advapi32.dll", EntryPoint = "RegFlushKey")]
    internal static partial int RegFlushKey(nint key);

    [LibraryImport("advapi32.dll", EntryPoint = "RegQueryInfoKeyW")]
    internal static partial int RegQueryInfoKey(nint key, nint cls, nint clsLen, nint reserved, nint subKeys, nint maxSubKey, nint maxClass, nint values, nint maxValueName,
        nint maxValue, nint security, long* lastWrite);

    [LibraryImport("advapi32.dll", EntryPoint = "RegGetKeySecurity")]
    internal static partial int RegGetKeySecurity(nint key, uint securityInformation, byte* descriptor, ref uint length);

    [LibraryImport("ntdll.dll", EntryPoint = "NtQueryKey")]
    internal static partial int NtQueryKey(nint key, int keyInformationClass, byte* buffer, uint length, out uint resultLength);

    // ---- service control manager. Used only by Worker/MachineServices.cs ----
    internal const uint ScManagerConnect = 0x0001, ServiceQueryConfig = 0x0001, ServiceChangeConfig = 0x0002, ServiceNoChange = 0xFFFFFFFF, ServiceConfigDelayedAutoStartInfo = 3;
    internal const int ErrorServiceDoesNotExist = 1060, ErrorInsufficientBuffer = 122;

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint OpenSCManager(string? machine, string? database, uint access);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint OpenService(nint manager, string name, uint access);

    [LibraryImport("advapi32.dll", EntryPoint = "QueryServiceConfigW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool QueryServiceConfig(nint service, byte* buffer, uint size, out uint needed);

    [LibraryImport("advapi32.dll", EntryPoint = "QueryServiceConfig2W", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool QueryServiceConfig2(nint service, uint level, byte* buffer, uint size, out uint needed);

    [LibraryImport("advapi32.dll", EntryPoint = "ChangeServiceConfigW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ChangeServiceConfig(nint service, uint serviceType, uint startType, uint errorControl, nint binaryPath, nint loadOrderGroup, nint tagId,
        nint dependencies, nint serviceStartName, nint password, nint displayName);

    /// <summary>Level 3 only (the delayed automatic start flag). Used only by Worker/MachineServices.cs.</summary>
    [LibraryImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ChangeServiceConfig2(nint service, uint level, byte* info);

    [LibraryImport("advapi32.dll", EntryPoint = "EnumDependentServicesW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumDependentServices(nint service, uint state, byte* buffer, uint size, out uint needed, out uint count);

    internal const uint ServiceEnumerateDependents = 0x0008, ServiceStateAll = 3;

    [LibraryImport("advapi32.dll", EntryPoint = "CloseServiceHandle")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseServiceHandle(nint handle);

    // ---- catalog lookup section: the Microsoft-signed image test of a service (Worker/ServiceImage.cs), read-only ----
    [LibraryImport("wintrust.dll", EntryPoint = "CryptCATAdminAcquireContext2", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CryptCATAdminAcquireContext2(out nint catAdmin, nint subsystem, string hashAlgorithm, nint strongHashPolicy, uint flags);

    [LibraryImport("wintrust.dll", EntryPoint = "CryptCATAdminCalcHashFromFileHandle2", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CryptCATAdminCalcHashFromFileHandle2(nint catAdmin, SafeFileHandle file, ref uint hashSize, byte* hash, uint flags);

    [LibraryImport("wintrust.dll", EntryPoint = "CryptCATAdminEnumCatalogFromHash", SetLastError = true)]
    internal static partial nint CryptCATAdminEnumCatalogFromHash(nint catAdmin, byte* hash, uint hashSize, uint flags, nint* previous);

    [LibraryImport("wintrust.dll", EntryPoint = "CryptCATCatalogInfoFromContext", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CryptCATCatalogInfoFromContext(nint catInfo, byte* info, uint flags);

    [LibraryImport("wintrust.dll", EntryPoint = "CryptCATAdminReleaseCatalogContext")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CryptCATAdminReleaseCatalogContext(nint catAdmin, nint catInfo, uint flags);

    [LibraryImport("wintrust.dll", EntryPoint = "CryptCATAdminReleaseContext")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CryptCATAdminReleaseContext(nint catAdmin, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "GetSystemWindowsDirectoryW")]
    internal static partial uint GetSystemWindowsDirectoryW(char* buffer, uint size);

    // ---- DLL search order and working directory (section 14.1 step 3) ----
    [LibraryImport("kernel32.dll", EntryPoint = "SetDefaultDllDirectories", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetDefaultDllDirectories(uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "SetCurrentDirectoryW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetCurrentDirectoryW(string path);

}
