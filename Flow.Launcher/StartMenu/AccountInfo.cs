using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.System;

namespace Flow.Launcher.StartMenu;

/// <summary>
/// The signed-in user as Start's account box shows them: with a Microsoft account, the account's name and email;
/// with a local account, the account's full name (or user name) and no email.
/// </summary>
public sealed record AccountInfo(string Name, string Email)
{
    /// <summary>Whether Windows has a Microsoft account for this user (the box then shows the Microsoft logo).</summary>
    public bool IsMicrosoftAccount => !string.IsNullOrEmpty(Email);

    /// <summary>Reads the account now, so a change (signing in to or out of a Microsoft account) shows right away.</summary>
    public static async Task<AccountInfo> GetAsync()
    {
        string first = null, last = null, display = null, email = null;
        try
        {
            // Windows' own user information, the same that Start and the lock screen use. It has the Microsoft
            // account's name and email, and is empty for a local account (or when Settings > Privacy > Account
            // info blocks desktop apps)
            var users = await User.FindAllAsync();
            var user = users.FirstOrDefault(u => u.AuthenticationStatus != UserAuthenticationStatus.Unauthenticated)
                       ?? users.FirstOrDefault();
            if (user != null)
            {
                display = await user.GetPropertyAsync(KnownUserProperties.DisplayName) as string;
                first = await user.GetPropertyAsync(KnownUserProperties.FirstName) as string;
                last = await user.GetPropertyAsync(KnownUserProperties.LastName) as string;
                email = await user.GetPropertyAsync(KnownUserProperties.AccountName) as string;
            }
        }
        catch (Exception e)
        {
            App.API.LogException(nameof(AccountInfo), "Failed to read the user's account", e);
        }

        if (email != null && !email.Contains('@')) email = null;

        var name = FirstNonEmpty(display, string.Join(" ", new[] { first, last }.Where(p => !string.IsNullOrWhiteSpace(p))),
            LocalFullName(), Environment.UserName);
        return new AccountInfo(name, email);
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    // The local account's full name, as the lock screen shows it for a local account
    private static string LocalFullName()
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (NetUserGetInfo(null, Environment.UserName, 10, out buffer) != 0 || buffer == IntPtr.Zero) return null;
            return Marshal.PtrToStructure<USER_INFO_10>(buffer).usri10_full_name;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero) NetApiBufferFree(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct USER_INFO_10
    {
        public string usri10_name;
        public string usri10_comment;
        public string usri10_usr_comment;
        public string usri10_full_name;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserGetInfo(string server, string userName, int level, out IntPtr buffer);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);
}
