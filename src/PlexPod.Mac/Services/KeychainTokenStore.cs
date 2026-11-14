using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using PlexPod.App.Services;

namespace PlexPod.Mac.Services;

/// <summary>
/// The Plex account token in the user's login Keychain (a generic password, service "PlexPod"), via
/// Security.framework: the macOS counterpart of the Windows app's DPAPI file. Never written anywhere
/// else, never logged. If the Keychain can't be read the user is simply signed out.
/// </summary>
public sealed class KeychainTokenStore(ILogger<KeychainTokenStore> logger) : ITokenStore
{
    public const string Service = "PlexPod";
    public const string Account = "plex-account-token";

    public string? Load()
    {
        try
        {
            return Keychain.Read(Service, Account) is { } bytes ? Encoding.UTF8.GetString(bytes) : null;
        }
        catch (KeychainException ex)
        {
            logger.LogWarning("Stored Plex token could not be read from the Keychain (status {Status}); signing out", ex.Status);
            return null;
        }
    }

    public void Save(string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        Keychain.Write(Service, Account, Encoding.UTF8.GetBytes(token));
    }

    public void Clear()
    {
        try
        {
            Keychain.Delete(Service, Account);
        }
        catch (KeychainException ex)
        {
            logger.LogWarning("Could not remove the Plex token from the Keychain (status {Status})", ex.Status);
        }
    }
}

public sealed class KeychainException(int status) : Exception($"Keychain error {status}")
{
    public int Status { get; } = status;
}

/// <summary>Generic-password items through the SecItem API, called directly (no packages).</summary>
internal static partial class Keychain
{
    private const int Success = 0;
    private const int ItemNotFound = -25300; // errSecItemNotFound

    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    private static readonly IntPtr SecurityLib = NativeLibrary.Load(Security);
    private static readonly IntPtr CfLib = NativeLibrary.Load(CoreFoundation);

    private static readonly IntPtr KSecClass = Global(SecurityLib, "kSecClass");
    private static readonly IntPtr KSecClassGenericPassword = Global(SecurityLib, "kSecClassGenericPassword");
    private static readonly IntPtr KSecAttrService = Global(SecurityLib, "kSecAttrService");
    private static readonly IntPtr KSecAttrAccount = Global(SecurityLib, "kSecAttrAccount");
    private static readonly IntPtr KSecAttrLabel = Global(SecurityLib, "kSecAttrLabel");
    private static readonly IntPtr KSecValueData = Global(SecurityLib, "kSecValueData");
    private static readonly IntPtr KSecReturnData = Global(SecurityLib, "kSecReturnData");
    private static readonly IntPtr KSecMatchLimit = Global(SecurityLib, "kSecMatchLimit");
    private static readonly IntPtr KSecMatchLimitOne = Global(SecurityLib, "kSecMatchLimitOne");
    private static readonly IntPtr KCFBooleanTrue = Global(CfLib, "kCFBooleanTrue");
    private static readonly IntPtr KeyCallBacks = NativeLibrary.GetExport(CfLib, "kCFTypeDictionaryKeyCallBacks");
    private static readonly IntPtr ValueCallBacks = NativeLibrary.GetExport(CfLib, "kCFTypeDictionaryValueCallBacks");

    public static byte[]? Read(string service, string account)
    {
        using var s = CfString.Create(service);
        using var a = CfString.Create(account);
        var query = Dictionary(
            (KSecClass, KSecClassGenericPassword),
            (KSecAttrService, s.Handle),
            (KSecAttrAccount, a.Handle),
            (KSecReturnData, KCFBooleanTrue),
            (KSecMatchLimit, KSecMatchLimitOne));
        try
        {
            var status = SecItemCopyMatching(query, out var result);
            if (status == ItemNotFound)
            {
                return null;
            }

            if (status != Success)
            {
                throw new KeychainException(status);
            }

            try
            {
                var length = (int)CFDataGetLength(result);
                var bytes = new byte[length];
                Marshal.Copy(CFDataGetBytePtr(result), bytes, 0, length);
                return bytes;
            }
            finally
            {
                CFRelease(result);
            }
        }
        finally
        {
            CFRelease(query);
        }
    }

    /// <summary>Replaces the item: delete, then add (the simplest correct update for one small secret).</summary>
    public static unsafe void Write(string service, string account, byte[] secret)
    {
        Delete(service, account);
        using var s = CfString.Create(service);
        using var a = CfString.Create(account);
        using var label = CfString.Create("PlexPod (Plex account)");
        IntPtr data;
        fixed (byte* p = secret)
        {
            data = CFDataCreate(IntPtr.Zero, p, secret.Length);
        }

        var attributes = Dictionary(
            (KSecClass, KSecClassGenericPassword),
            (KSecAttrService, s.Handle),
            (KSecAttrAccount, a.Handle),
            (KSecAttrLabel, label.Handle),
            (KSecValueData, data));
        try
        {
            var status = SecItemAdd(attributes, IntPtr.Zero);
            if (status != Success)
            {
                throw new KeychainException(status);
            }
        }
        finally
        {
            CFRelease(attributes);
            CFRelease(data);
        }
    }

    public static void Delete(string service, string account)
    {
        using var s = CfString.Create(service);
        using var a = CfString.Create(account);
        var query = Dictionary((KSecClass, KSecClassGenericPassword), (KSecAttrService, s.Handle), (KSecAttrAccount, a.Handle));
        try
        {
            var status = SecItemDelete(query);
            if (status is not (Success or ItemNotFound))
            {
                throw new KeychainException(status);
            }
        }
        finally
        {
            CFRelease(query);
        }
    }

    private static unsafe IntPtr Dictionary(params (IntPtr Key, IntPtr Value)[] entries)
    {
        var keys = entries.Select(e => e.Key).ToArray();
        var values = entries.Select(e => e.Value).ToArray();
        fixed (IntPtr* k = keys)
        fixed (IntPtr* v = values)
        {
            return CFDictionaryCreate(IntPtr.Zero, k, v, entries.Length, KeyCallBacks, ValueCallBacks);
        }
    }

    private static IntPtr Global(IntPtr library, string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(library, name));

    private readonly struct CfString : IDisposable
    {
        private CfString(IntPtr handle) => Handle = handle;

        public IntPtr Handle { get; }

        public static CfString Create(string value) => new(CFStringCreateWithCString(IntPtr.Zero, value, 0x08000100));

        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
            {
                CFRelease(Handle);
            }
        }
    }

    [LibraryImport(Security)]
    private static partial int SecItemCopyMatching(IntPtr query, out IntPtr result);

    [LibraryImport(Security)]
    private static partial int SecItemAdd(IntPtr attributes, IntPtr result);

    [LibraryImport(Security)]
    private static partial int SecItemDelete(IntPtr query);

    [LibraryImport(CoreFoundation)]
    private static unsafe partial IntPtr CFDataCreate(IntPtr allocator, byte* bytes, nint length);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFDataGetLength(IntPtr data);

    [LibraryImport(CoreFoundation)]
    private static partial IntPtr CFDataGetBytePtr(IntPtr data);

    [LibraryImport(CoreFoundation, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr CFStringCreateWithCString(IntPtr allocator, string value, uint encoding);

    [LibraryImport(CoreFoundation)]
    private static unsafe partial IntPtr CFDictionaryCreate(IntPtr allocator, IntPtr* keys, IntPtr* values, nint count, IntPtr keyCallBacks, IntPtr valueCallBacks);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(IntPtr value);
}
