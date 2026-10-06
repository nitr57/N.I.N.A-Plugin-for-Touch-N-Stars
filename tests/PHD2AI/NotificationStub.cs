// Only desktop alert delivery is replaced in the standalone TCP client tests.
// The real client, protocol parser, request matching and AI validation are linked.
namespace NINA.Core.Utility.Notification
{
    public static class Notification
    {
        public static void ShowError(string message) { }
        public static void ShowWarning(string message) { }
    }
}
