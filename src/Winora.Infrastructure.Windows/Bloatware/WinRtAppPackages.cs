// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Winora.Application;

namespace Winora.Infrastructure.Windows.Bloatware;

/// <summary>
/// The only code that reads packages of all users and changes them (a source scan test enforces it): the documented Windows.Management.Deployment.PackageManager runtime class, called through
/// its COM interfaces directly. The SDK projection package is not available to this build, so the interface identifiers and the position of each method in its interface come from the
/// Windows metadata file (<see cref="Slots"/>); a test compares every entry with that file on the machine that runs it. Nothing here takes a path, a command or free text: callers pass package
/// full names and family names that the same API listed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WinRtAppPackages : IAppPackages
{
    private const int Main = 1, Framework = 2, Optional = 32;
    private const uint ListedTypes = Main | Framework | Optional;
    private const int RemovalOptionAllUsers = 0x80000;
    private const int EAccessDenied = unchecked((int)0x80070005);
    private static readonly TimeSpan WaitLimit = TimeSpan.FromMinutes(15);

    /// <summary>Interface, method and position in the interface table (6 is the first method after IInspectable). Verified against the Windows metadata by a test.</summary>
    internal static readonly (string Interface, Guid Iid, string Method, int Slot)[] Slots =
    [
        ("Windows.Management.Deployment.IPackageManager", new("9a7d4b65-5e8f-4fc7-a2e5-7f6925cb8b53"), "FindUsers", 15),
        ("Windows.Management.Deployment.IPackageManager2", new("f7aad08d-0840-46f2-b5d8-cad47693a095"), "RemovePackageAsync", 6),
        ("Windows.Management.Deployment.IPackageManager2", new("f7aad08d-0840-46f2-b5d8-cad47693a095"), "RegisterPackageByFullNameAsync", 8),
        ("Windows.Management.Deployment.IPackageManager2", new("f7aad08d-0840-46f2-b5d8-cad47693a095"), "FindPackagesWithPackageTypes", 9),
        ("Windows.Management.Deployment.IPackageManager2", new("f7aad08d-0840-46f2-b5d8-cad47693a095"), "FindPackagesForUserWithPackageTypes", 10),
        ("Windows.Management.Deployment.IPackageManager8", new("b8575330-1298-4ee2-80ee-7f659c5d2782"), "DeprovisionPackageForAllUsersAsync", 6),
        ("Windows.Management.Deployment.IPackageManager9", new("1aa79035-cc71-4b2e-80a6-c7041d8579a7"), "FindProvisionedPackages", 6),
        ("Windows.Management.Deployment.IPackageUserInformation", new("f6383423-fa09-4cbc-9055-15ca275e2e7e"), "get_UserSecurityId", 6),
        ("Windows.Management.Deployment.IPackageUserInformation", new("f6383423-fa09-4cbc-9055-15ca275e2e7e"), "get_InstallState", 7),
        ("Windows.ApplicationModel.IPackage", new("163c792f-bd75-413c-bf23-b1fe7b95d825"), "get_Id", 6),
        ("Windows.ApplicationModel.IPackage", new("163c792f-bd75-413c-bf23-b1fe7b95d825"), "get_IsFramework", 8),
        ("Windows.ApplicationModel.IPackage", new("163c792f-bd75-413c-bf23-b1fe7b95d825"), "get_Dependencies", 9),
        ("Windows.ApplicationModel.IPackage2", new("a6612fb6-7688-4ace-95fb-359538e7aa01"), "get_DisplayName", 6),
        ("Windows.ApplicationModel.IPackage2", new("a6612fb6-7688-4ace-95fb-359538e7aa01"), "get_IsResourcePackage", 10),
        ("Windows.ApplicationModel.IPackage2", new("a6612fb6-7688-4ace-95fb-359538e7aa01"), "get_IsBundle", 11),
        ("Windows.ApplicationModel.IPackage2", new("a6612fb6-7688-4ace-95fb-359538e7aa01"), "get_IsDevelopmentMode", 12),
        ("Windows.ApplicationModel.IPackage4", new("65aed1ae-b95b-450c-882b-6255187f397e"), "get_SignatureKind", 6),
        ("Windows.ApplicationModel.IPackage4", new("65aed1ae-b95b-450c-882b-6255187f397e"), "get_IsOptional", 7),
        ("Windows.ApplicationModel.IPackage8", new("2c584f7b-ce2a-4be6-a093-77cfbb2a7ea1"), "get_InstalledPath", 9),
        ("Windows.ApplicationModel.IPackageId", new("1adb665e-37c7-4790-9980-dd7ae74e8bb2"), "get_Name", 6),
        ("Windows.ApplicationModel.IPackageId", new("1adb665e-37c7-4790-9980-dd7ae74e8bb2"), "get_Version", 7),
        ("Windows.ApplicationModel.IPackageId", new("1adb665e-37c7-4790-9980-dd7ae74e8bb2"), "get_Architecture", 8),
        ("Windows.ApplicationModel.IPackageId", new("1adb665e-37c7-4790-9980-dd7ae74e8bb2"), "get_Publisher", 10),
        ("Windows.ApplicationModel.IPackageId", new("1adb665e-37c7-4790-9980-dd7ae74e8bb2"), "get_PublisherId", 11),
        ("Windows.ApplicationModel.IPackageId", new("1adb665e-37c7-4790-9980-dd7ae74e8bb2"), "get_FullName", 12),
        ("Windows.ApplicationModel.IPackageId", new("1adb665e-37c7-4790-9980-dd7ae74e8bb2"), "get_FamilyName", 13),
        ("Windows.Foundation.IAsyncInfo", new("00000036-0000-0000-c000-000000000046"), "get_Status", 7),
        ("Windows.Foundation.IAsyncInfo", new("00000036-0000-0000-c000-000000000046"), "get_ErrorCode", 8),
        ("Windows.Foundation.IAsyncInfo", new("00000036-0000-0000-c000-000000000046"), "Close", 10),
    ];

    private static readonly Guid IPackageManager = new("9a7d4b65-5e8f-4fc7-a2e5-7f6925cb8b53"), IPackageManager2 = new("f7aad08d-0840-46f2-b5d8-cad47693a095"),
        IPackageManager8 = new("b8575330-1298-4ee2-80ee-7f659c5d2782"), IPackageManager9 = new("1aa79035-cc71-4b2e-80a6-c7041d8579a7"),
        IPackage2 = new("a6612fb6-7688-4ace-95fb-359538e7aa01"), IPackage4 = new("65aed1ae-b95b-450c-882b-6255187f397e"), IPackage8 = new("2c584f7b-ce2a-4be6-a093-77cfbb2a7ea1"),
        IAsyncInfo = new("00000036-0000-0000-c000-000000000046");

    // Documented COM class PackageDebugSettings (IPackageDebugSettings: EnableDebugging 3, DisableDebugging 4, Suspend 5, Resume 6, TerminateAllProcesses 7).
    private static readonly Guid ClsidPackageDebugSettings = new("b1aec16f-2383-4852-b0e9-8f0b1dc66b4d"), IidPackageDebugSettings = new("f27c3930-8029-4ad1-94e3-3dba417810c1");

    // ---- native entry points ----

    [DllImport("ole32.dll")] private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, int context, ref Guid iid, out IntPtr instance);

    [DllImport("combase.dll")] private static extern int RoInitialize(int initType);
    [DllImport("combase.dll")] private static extern void RoUninitialize();
    [DllImport("combase.dll")] private static extern int RoActivateInstance(IntPtr classId, out IntPtr instance);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string source, int length, out IntPtr hstring);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(IntPtr hstring);
    [DllImport("combase.dll")] private static extern IntPtr WindowsGetStringRawBuffer(IntPtr hstring, out uint length);

    [StructLayout(LayoutKind.Sequential)] private struct PackageVersion { public ushort Major, Minor, Build, Revision; }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int QueryInterfaceFn(IntPtr self, ref Guid iid, out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint ReleaseFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int OutPtrFn(IntPtr self, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int OutIntFn(IntPtr self, out int value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int OutUIntFn(IntPtr self, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int OutByteFn(IntPtr self, out byte value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int OutVersionFn(IntPtr self, out PackageVersion value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int UIntOutPtrFn(IntPtr self, uint argument, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int StrOutPtrFn(IntPtr self, IntPtr text, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int StrUIntOutPtrFn(IntPtr self, IntPtr text, uint argument, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int StrIntOutPtrFn(IntPtr self, IntPtr text, int argument, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int RegisterFn(IntPtr self, IntPtr text, IntPtr dependencies, int options, out IntPtr operation);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int NoArgFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int StringFn(IntPtr self, IntPtr text);

    private static TFn Method<TFn>(IntPtr instance, int slot) where TFn : Delegate =>
        Marshal.GetDelegateForFunctionPointer<TFn>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));

    private static IntPtr Query(IntPtr instance, Guid iid)
    {
        var id = iid;
        return Method<QueryInterfaceFn>(instance, 0)(instance, ref id, out var result) == 0 ? result : IntPtr.Zero;
    }

    private static void Release(IntPtr instance) { if (instance != IntPtr.Zero) Method<ReleaseFn>(instance, 2)(instance); }

    private static IntPtr NewString(string value)
    {
        if (value.Length == 0) return IntPtr.Zero;
        Check(WindowsCreateString(value, value.Length, out var handle));
        return handle;
    }

    private static string ReadString(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return "";
        try { var buffer = WindowsGetStringRawBuffer(handle, out var length); return Marshal.PtrToStringUni(buffer, (int)length) ?? ""; }
        finally { WindowsDeleteString(handle); }
    }

    private static void Check(int hresult) { if (hresult < 0) throw new InvalidOperationException($"Package API call failed (0x{hresult:X8}).", new System.ComponentModel.Win32Exception(hresult)); }

    private static string Text(IntPtr instance, int slot) { Check(Method<OutPtrFn>(instance, slot)(instance, out var value)); return ReadString(value); }
    private static string? TryText(IntPtr instance, int slot) => Method<OutPtrFn>(instance, slot)(instance, out var value) < 0 ? null : ReadString(value);
    private static bool Flag(IntPtr instance, int slot) { Check(Method<OutByteFn>(instance, slot)(instance, out var value)); return value != 0; }
    private static int Number(IntPtr instance, int slot) { Check(Method<OutIntFn>(instance, slot)(instance, out var value)); return value; }

    /// <summary>Runs the work on a thread whose Windows Runtime apartment is initialised (the thread pool and test threads are not, by default).</summary>
    private static T InApartment<T>(Func<T> work)
    {
        var hr = RoInitialize(1); // multithreaded
        var initialised = hr is 0 or 1;
        try { return work(); }
        finally { if (initialised) RoUninitialize(); }
    }

    private static IntPtr Manager()
    {
        Check(NewClassName("Windows.Management.Deployment.PackageManager", out var name));
        try { Check(RoActivateInstance(name, out var instance)); return instance; }
        finally { WindowsDeleteString(name); }
    }

    private static int NewClassName(string value, out IntPtr handle) => WindowsCreateString(value, value.Length, out handle);

    // ---- collections ----

    private static List<IntPtr> Items(IntPtr iterable)
    {
        var list = new List<IntPtr>();
        if (iterable == IntPtr.Zero) return list;
        Check(Method<OutPtrFn>(iterable, 6)(iterable, out var iterator));
        try
        {
            while (true)
            {
                if (!Flag(iterator, 7)) break;
                Check(Method<OutPtrFn>(iterator, 6)(iterator, out var current));
                list.Add(current);
                Check(Method<OutByteFn>(iterator, 8)(iterator, out _));
            }
        }
        catch { foreach (var p in list) Release(p); throw; }
        finally { Release(iterator); }
        return list;
    }

    private static List<IntPtr> VectorItems(IntPtr vector)
    {
        var list = new List<IntPtr>();
        if (vector == IntPtr.Zero) return list;
        Check(Method<OutUIntFn>(vector, 7)(vector, out var size));
        try { for (uint i = 0; i < size; i++) { Check(Method<UIntOutPtrFn>(vector, 6)(vector, i, out var item)); list.Add(item); } }
        catch { foreach (var p in list) Release(p); throw; }
        return list;
    }

    private static void ReleaseAll(IEnumerable<IntPtr> items) { foreach (var p in items) Release(p); }

    // ---- reading ----

    public string WindowsFolder { get; } = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    public PackageSnapshot Read(CancellationToken cancellationToken) => InApartment(() => ReadCore(cancellationToken));

    private PackageSnapshot ReadCore(CancellationToken ct)
    {
        var manager = Manager();
        var manager2 = Query(manager, IPackageManager2);
        var manager9 = Query(manager, IPackageManager9);
        if (manager2 == IntPtr.Zero) { Release(manager); throw new InvalidOperationException("The package manager is not available."); }
        try
        {
            var allUsers = true;
            var hr = Method<UIntOutPtrFn>(manager2, 9)(manager2, ListedTypes, out var all);
            if (hr < 0)
            {
                if (hr != EAccessDenied) Check(hr);
                allUsers = false;
                Check(Method<StrUIntOutPtrFn>(manager2, 10)(manager2, IntPtr.Zero, ListedTypes, out all));
            }
            Check(Method<StrUIntOutPtrFn>(manager2, 10)(manager2, IntPtr.Zero, ListedTypes, out var mine));
            var current = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var mineItems = Items(mine); Release(mine);
            try { foreach (var p in mineItems) current.Add(Names(p).FullName); } finally { ReleaseAll(mineItems); }

            var provisionedFamilies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var provisioned = new List<AppPackage>();
            var provisionedKnown = false;
            if (manager9 != IntPtr.Zero && Method<OutPtrFn>(manager9, 6)(manager9, out var vector) >= 0)
            {
                provisionedKnown = true;
                var provItems = VectorItems(vector); Release(vector);
                try { foreach (var p in provItems) { ct.ThrowIfCancellationRequested(); provisioned.Add(Model(p, true, false, current)); } } finally { ReleaseAll(provItems); }
                foreach (var p in provisioned) provisionedFamilies.Add(p.FamilyName);
            }

            var packages = new Dictionary<string, AppPackage>(StringComparer.OrdinalIgnoreCase);
            var allItems = Items(all); Release(all);
            try
            {
                foreach (var p in allItems)
                {
                    ct.ThrowIfCancellationRequested();
                    var model = Model(p, false, true, current);
                    packages[model.FullName] = model with { IsProvisioned = provisionedFamilies.Contains(model.FamilyName) };
                }
            }
            finally { ReleaseAll(allItems); }
            foreach (var p in provisioned)
                if (!packages.Values.Any(x => string.Equals(x.FamilyName, p.FamilyName, StringComparison.OrdinalIgnoreCase))) packages[p.FullName] = p with { IsInstalled = false, InstalledForCurrentUser = false, IsProvisioned = true };
            return new(packages.Values.ToList(), allUsers, provisionedKnown, DateTimeOffset.UtcNow);
        }
        finally { Release(manager9); Release(manager2); Release(manager); }
    }

    private static (string FullName, string FamilyName) Names(IntPtr package)
    {
        Check(Method<OutPtrFn>(package, 6)(package, out var id));
        try { return (Text(id, 12), Text(id, 13)); } finally { Release(id); }
    }

    private static AppPackage Model(IntPtr package, bool provisioned, bool installed, HashSet<string> current)
    {
        Check(Method<OutPtrFn>(package, 6)(package, out var id));
        string name, publisher, publisherId, fullName, familyName, version, architecture;
        try
        {
            name = Text(id, 6); publisher = Text(id, 10); publisherId = Text(id, 11); fullName = Text(id, 12); familyName = Text(id, 13);
            Check(Method<OutVersionFn>(id, 7)(id, out var v));
            version = $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
            architecture = Number(id, 8) switch { 0 => "x86", 5 => "arm", 9 => "x64", 11 => "neutral", 12 => "arm64", 14 => "x86onarm64", _ => "unknown" };
        }
        finally { Release(id); }

        var framework = Flag(package, 8);
        var families = new List<string>();
        if (Method<OutPtrFn>(package, 9)(package, out var dependencies) >= 0)
        {
            var items = VectorItems(dependencies); Release(dependencies);
            try { foreach (var d in items) { try { families.Add(Names(d).FamilyName); } catch (InvalidOperationException) { /* a dependency that cannot be read is left out */ } } } finally { ReleaseAll(items); }
        }

        string display = name; bool resource = false, bundle = false, development = false;
        var p2 = Query(package, IPackage2);
        if (p2 != IntPtr.Zero)
        {
            try
            {
                if (TryText(p2, 6) is { Length: > 0 } shown) display = shown;
                resource = Flag(p2, 10); bundle = Flag(p2, 11); development = Flag(p2, 12);
            }
            finally { Release(p2); }
        }
        var signature = PackageSignature.Unknown; var optional = false;
        var p4 = Query(package, IPackage4);
        if (p4 != IntPtr.Zero)
        {
            try
            {
                signature = Number(p4, 6) switch { 0 => PackageSignature.None, 1 => PackageSignature.Developer, 2 => PackageSignature.Enterprise, 3 => PackageSignature.Store, 4 => PackageSignature.System, _ => PackageSignature.Unknown };
                optional = Flag(p4, 7);
            }
            finally { Release(p4); }
        }
        string? location = null;
        var p8 = Query(package, IPackage8);
        if (p8 != IntPtr.Zero)
        {
            try { location = TryText(p8, 9); } finally { Release(p8); }
        }
        if (string.IsNullOrWhiteSpace(location)) location = null;
        // A display name such as "ms-resource:AppName" is a resource reference that was not resolved; a readable form of the package name is shown then.
        if (display.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) || display.Length == 0) display = PackageText.FriendlyName(name);
        return new(fullName, familyName, name, publisher, publisherId, version, architecture, display, location, signature, framework, resource, bundle, optional, development,
            installed, installed && current.Contains(fullName), provisioned, families);
    }

    public IReadOnlyList<PackageUser> ReadUsers(string fullName) => InApartment(() =>
    {
        var manager = Manager();
        try
        {
            var name = NewString(fullName);
            try
            {
                Check(Method<StrOutPtrFn>(manager, 15)(manager, name, out var users));
                var items = Items(users); Release(users);
                try
                {
                    return (IReadOnlyList<PackageUser>)items.Select(u => new PackageUser(Text(u, 6), Number(u, 7) switch
                    {
                        0 => PackageInstallState.NotInstalled, 1 => PackageInstallState.Staged, 2 => PackageInstallState.Installed, 6 => PackageInstallState.Paused, _ => PackageInstallState.Unknown,
                    })).ToList();
                }
                finally { ReleaseAll(items); }
            }
            finally { if (name != IntPtr.Zero) WindowsDeleteString(name); }
        }
        finally { Release(manager); }
    });

    public IReadOnlyList<string> InstalledFullNames(string familyName) => InApartment(() =>
    {
        var manager = Manager();
        var manager2 = Query(manager, IPackageManager2);
        try
        {
            if (manager2 == IntPtr.Zero) return (IReadOnlyList<string>)[];
            var hr = Method<UIntOutPtrFn>(manager2, 9)(manager2, ListedTypes, out var all);
            if (hr < 0) Check(Method<StrUIntOutPtrFn>(manager2, 10)(manager2, IntPtr.Zero, ListedTypes, out all));
            var items = Items(all); Release(all);
            try { return items.Select(Names).Where(n => string.Equals(n.FamilyName, familyName, StringComparison.OrdinalIgnoreCase)).Select(n => n.FullName).ToList(); }
            finally { ReleaseAll(items); }
        }
        finally { Release(manager2); Release(manager); }
    });

    public IReadOnlySet<string> InstalledFamilies() => InApartment(() =>
    {
        var manager = Manager();
        var manager2 = Query(manager, IPackageManager2);
        try
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (manager2 == IntPtr.Zero) return (IReadOnlySet<string>)set;
            var hr = Method<UIntOutPtrFn>(manager2, 9)(manager2, ListedTypes, out var all);
            if (hr < 0) Check(Method<StrUIntOutPtrFn>(manager2, 10)(manager2, IntPtr.Zero, ListedTypes, out all));
            var items = Items(all); Release(all);
            try { foreach (var p in items) set.Add(Names(p).FamilyName); }
            finally { ReleaseAll(items); }
            return set;
        }
        finally { Release(manager2); Release(manager); }
    });

    /// <summary>
    /// Closes every process of one package through the documented package debug interface (the call behind the Terminate button of Windows Settings). It takes the full name of a package that the
    /// same API listed, never a process id or a path, and the service calls it only after Windows answered that the package is in use.
    /// </summary>
    public PackageStepResult StopApp(string fullName)
    {
        if (fullName.Length == 0 || fullName.Length > 256 || fullName.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '~'))) return PackageStepResult.Fail(BloatCode.Failed);
        return InApartment(() =>
        {
            var clsid = ClsidPackageDebugSettings; var iid = IidPackageDebugSettings;
            var hr = CoCreateInstance(ref clsid, IntPtr.Zero, 0x17 /* CLSCTX_ALL */, ref iid, out var settings);
            if (hr < 0 || settings == IntPtr.Zero) return hr == EAccessDenied ? PackageStepResult.Fail(BloatCode.AccessDenied, hr) : PackageStepResult.Fail(BloatCode.Unsupported, hr);
            var name = Marshal.StringToHGlobalUni(fullName);
            try
            {
                var call = Method<StringFn>(settings, 7)(settings, name);
                return call < 0 ? Map(call) : PackageStepResult.Success;
            }
            finally { Marshal.FreeHGlobal(name); Release(settings); }
        });
    }

    public bool IsProvisioned(string familyName) => InApartment(() =>
    {
        var manager = Manager();
        var manager9 = Query(manager, IPackageManager9);
        try
        {
            if (manager9 == IntPtr.Zero) return false;
            Check(Method<OutPtrFn>(manager9, 6)(manager9, out var vector));
            var items = VectorItems(vector); Release(vector);
            try { return items.Any(p => string.Equals(Names(p).FamilyName, familyName, StringComparison.OrdinalIgnoreCase)); }
            finally { ReleaseAll(items); }
        }
        finally { Release(manager9); Release(manager); }
    });

    // ---- changes ----

    public PackageStepResult RemoveForAllUsers(string fullName) => InApartment(() =>
    {
        var manager = Manager();
        var manager2 = Query(manager, IPackageManager2);
        var name = NewString(fullName);
        try
        {
            if (manager2 == IntPtr.Zero) return PackageStepResult.Fail(BloatCode.Unsupported);
            var hr = Method<StrIntOutPtrFn>(manager2, 6)(manager2, name, RemovalOptionAllUsers, out var operation);
            return hr < 0 ? Map(hr) : Wait(operation);
        }
        finally { if (name != IntPtr.Zero) WindowsDeleteString(name); Release(manager2); Release(manager); }
    });

    public PackageStepResult Deprovision(string familyName) => InApartment(() =>
    {
        var manager = Manager();
        var manager8 = Query(manager, IPackageManager8);
        var name = NewString(familyName);
        try
        {
            if (manager8 == IntPtr.Zero) return PackageStepResult.Fail(BloatCode.Unsupported);
            var hr = Method<StrOutPtrFn>(manager8, 6)(manager8, name, out var operation);
            return hr < 0 ? Map(hr) : Wait(operation);
        }
        finally { if (name != IntPtr.Zero) WindowsDeleteString(name); Release(manager8); Release(manager); }
    });

    public PackageStepResult RegisterStaged(string fullName) => InApartment(() =>
    {
        var manager = Manager();
        var manager2 = Query(manager, IPackageManager2);
        var name = NewString(fullName);
        try
        {
            if (manager2 == IntPtr.Zero) return PackageStepResult.Fail(BloatCode.Unsupported);
            var hr = Method<RegisterFn>(manager2, 8)(manager2, name, IntPtr.Zero, 0, out var operation);
            return hr < 0 ? Map(hr) : Wait(operation);
        }
        finally { if (name != IntPtr.Zero) WindowsDeleteString(name); Release(manager2); Release(manager); }
    });

    /// <summary>Waits for a deployment operation by polling its status. A running deployment is never cancelled: stopping it half way would leave a package in a state nobody asked for.</summary>
    private static PackageStepResult Wait(IntPtr operation)
    {
        var info = Query(operation, IAsyncInfo);
        try
        {
            if (info == IntPtr.Zero) return PackageStepResult.Fail(BloatCode.Failed);
            var started = DateTime.UtcNow;
            while (true)
            {
                var hr = Method<OutIntFn>(info, 7)(info, out var status);
                if (hr < 0) return Map(hr);
                if (status == 1) return PackageStepResult.Success;
                if (status == 2) return PackageStepResult.Fail(BloatCode.Failed);
                if (status == 3)
                {
                    Method<OutIntFn>(info, 8)(info, out var error);
                    return Map(error);
                }
                if (DateTime.UtcNow - started > WaitLimit) return PackageStepResult.Fail(BloatCode.Failed);
                Thread.Sleep(100);
            }
        }
        finally
        {
            if (info != IntPtr.Zero) { Method<NoArgFn>(info, 10)(info); Release(info); }
            Release(operation);
        }
    }

    internal static PackageStepResult Map(int hresult) => hresult switch
    {
        0 => PackageStepResult.Success,
        EAccessDenied => PackageStepResult.Fail(BloatCode.AccessDenied, hresult),
        unchecked((int)0x80073D02) => PackageStepResult.Fail(BloatCode.InUse, hresult),
        unchecked((int)0x80073CFA) or unchecked((int)0x80070032) => PackageStepResult.Fail(BloatCode.RefusedByWindows, hresult),
        unchecked((int)0x80073CF1) or unchecked((int)0x80070490) => PackageStepResult.Fail(BloatCode.NotFound, hresult),
        _ => PackageStepResult.Fail(BloatCode.Failed, hresult),
    };

    // ---- size and files ----

    public long? MeasureSize(string installLocation, CancellationToken cancellationToken)
    {
        try
        {
            if (!Path.IsPathFullyQualified(installLocation) || !Directory.Exists(installLocation)) return null;
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint, ReturnSpecialDirectories = false };
            long total = 0;
            foreach (var file in new DirectoryInfo(installLocation).EnumerateFiles("*", options)) { cancellationToken.ThrowIfCancellationRequested(); total += file.Length; }
            return total;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }

    public bool HasManifest(string installLocation)
    {
        try { return Path.IsPathFullyQualified(installLocation) && File.Exists(Path.Combine(installLocation, "AppxManifest.xml")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }
}
